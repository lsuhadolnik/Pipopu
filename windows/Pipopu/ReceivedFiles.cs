using System.Text.Json;
using Microsoft.Win32;

namespace Pipopu;

internal sealed record OneDriveFolder(string DriveId, string Id, string Path);
internal sealed record ReceivedSaveResult(string Message, string? LocalPath = null);

internal sealed record ReceivedFileSettings(bool AutoSave, string LocalFolder, OneDriveFolder? CloudFolder)
{
    internal string Location => CloudFolder?.Path ?? LocalFolder;
}

internal sealed class SettingsStore
{
    private const string RegistryPath = @"Software\Pipopu";
    private const string Value = "ReceivedFileSettings";
    private const string SetupSeen = "DesktopSetupWizardSeen";

    internal ReceivedFileSettings Current { get; private set; } = Load();

    internal bool HasSeenSetupWizard()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(SetupSeen) is int seen && seen != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TransferLog.Write($"Could not read Windows setup preference reason={error.GetType().Name}");
            return false;
        }
    }

    internal void MarkSetupWizardSeen()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(SetupSeen, 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TransferLog.Write($"Could not save Windows setup preference reason={error.GetType().Name}");
        }
    }

    internal void Update(ReceivedFileSettings settings)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue(Value, JsonSerializer.Serialize(settings));
        Current = settings;
    }

    private static ReceivedFileSettings Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue(Value) is string json &&
                JsonSerializer.Deserialize<ReceivedFileSettings>(json) is { } settings &&
                !string.IsNullOrWhiteSpace(settings.LocalFolder))
                return settings;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Pipopu");
        return new ReceivedFileSettings(false, downloads, null);
    }
}

internal static class ReceivedFiles
{
    internal static string SafeName(string name)
    {
        string leaf = Path.GetFileName(name.Replace('/', '\\'));
        var invalid = Path.GetInvalidFileNameChars();
        leaf = new string(leaf.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        if (leaf.Length == 0)
            return "Pipopu transfer";

        string stem = leaf.Split('.')[0].TrimEnd(' ');
        if (leaf.StartsWith("~$", StringComparison.Ordinal) ||
            stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "0123456789¹²³".Contains(stem[3])))
            leaf = "_" + leaf;
        return leaf;
    }

    internal static async Task<ReceivedSaveResult> SaveAsync(
        Transfer transfer, ReceivedFileSettings settings, OneDrive oneDrive, CancellationToken cancellation)
    {
        try
        {
            await using var source = transfer.OpenRead();
            string name = SafeName(transfer.Name);
            if (settings.CloudFolder is { } cloud)
            {
                await oneDrive.SaveReceivedAsync(cloud, name, source, transfer.Size, cancellation);
                return new("Saved to OneDrive.");
            }

            string localPath = await SaveLocalAsync(settings.LocalFolder, name, source, cancellation);
            return new("Saved automatically.", localPath);
        }
        catch (Exception error)
        {
            return new(error is OperationCanceledException ? "Automatic save cancelled." : $"Automatic save failed. {error.Message}");
        }
    }

    internal static async Task<string> SaveLocalAsync(string folder, string name, Stream source, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, $".pipopu-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(output, cancellation);
            }

            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            if (extension.Length > 32)
            {
                stem = name;
                extension = "";
            }
            for (int number = 1; ; number++)
            {
                cancellation.ThrowIfCancellationRequested();
                string suffix = number == 1 ? "" : $" ({number})";
                int available = 255 - suffix.Length - extension.Length;
                int length = Math.Min(available, stem.Length);
                if (length > 0 && char.IsHighSurrogate(stem[length - 1]))
                    length--;
                string destination = Path.Combine(folder, stem[..length] + suffix + extension);
                try
                {
                    File.Move(staging, destination);
                    return destination;
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
            }
        }
        finally
        {
            File.Delete(staging);
        }
    }

    internal static void Release(Transfer transfer, Task<ReceivedSaveResult>? save)
    {
        if (transfer.File is not { } file)
            return;
        if (save is { IsCompleted: false })
            _ = save.ContinueWith(_ => file.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        else
            file.Dispose();
    }
}
