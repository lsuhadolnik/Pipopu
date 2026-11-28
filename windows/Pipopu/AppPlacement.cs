using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Pipopu;

internal static class AppPlacement
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Pipopu";
    internal const string StartupArgument = "--startup";
    internal const string FinishMoveArgument = "--finish-move";
    internal static string InstalledExe { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pipopu", "App", "Pipopu.exe");

    private sealed record MoveReceipt(string Source, int Parent, long ParentStartedUtc, bool OpenOneDrive);

    internal static string CurrentExe => Environment.ProcessPath
        ?? throw new IOException("Pipopu could not find its executable.");
    internal static bool IsInstalled => string.Equals(CurrentExe, InstalledExe, StringComparison.OrdinalIgnoreCase);

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location identifies the single-file bundle.")]
    internal static bool IsSingleFile => string.IsNullOrEmpty(typeof(AppPlacement).Assembly.Location);
    internal static bool CanMove => !IsInstalled && IsSingleFile;

    internal static string? StartupCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) as string;
    }

    internal static void SetStartupCommand(string? command)
    {
        if (command is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }
        using var writable = Registry.CurrentUser.CreateSubKey(RunKey);
        writable.SetValue(RunValue, command);
    }

    internal static string StartupCommandFor(string exe) => $"\"{exe}\" {StartupArgument}";

    internal static void PrepareMove()
    {
        if (!CanMove)
            throw new IOException("Use the single-file Windows executable to move Pipopu to Local AppData.");
        StageExecutable(CurrentExe, InstalledExe);
    }

    internal static void RestartInstalled(bool openOneDrive)
    {
        using var parent = Process.GetCurrentProcess();
        var receipt = new MoveReceipt(CurrentExe, parent.Id, parent.StartTime.ToUniversalTime().Ticks, openOneDrive);
        string id = Guid.NewGuid().ToString("N");
        string receiptPath = ReceiptPath(id);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        try
        {
            var start = new ProcessStartInfo(InstalledExe) { WorkingDirectory = Path.GetDirectoryName(InstalledExe)! };
            start.ArgumentList.Add(FinishMoveArgument);
            start.ArgumentList.Add(id);
            using var next = Process.Start(start) ?? throw new IOException("Pipopu could not launch from Local AppData.");
        }
        catch
        {
            File.Delete(receiptPath);
            throw;
        }
    }

    internal static bool IsMoveRestart(string[] args) => IsInstalled && args.Length == 2 &&
        args[0] == FinishMoveArgument && Guid.TryParseExact(args[1], "N", out _);

    internal static (bool OpenOneDrive, bool CopyLeftBehind) FinishMove(string id)
    {
        string path = ReceiptPath(Guid.ParseExact(id, "N").ToString("N"));
        bool openOneDrive = false;
        try
        {
            var receipt = JsonSerializer.Deserialize<MoveReceipt>(File.ReadAllText(path))
                ?? throw new IOException("Missing Pipopu move receipt.");
            openOneDrive = receipt.OpenOneDrive;
            File.Delete(path);
            try
            {
                using var parent = Process.GetProcessById(receipt.Parent);
                if (!parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == receipt.ParentStartedUtc &&
                    !parent.WaitForExit(10_000))
                    return (openOneDrive, true);
            }
            catch (ArgumentException) { } // The original process has already exited.
            return (openOneDrive, !RemoveOriginalCopy(receipt.Source, InstalledExe));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException
            or System.Security.SecurityException)
        {
            TransferLog.Write($"Pipopu move cleanup failed reason={error.GetType().Name}");
            return (openOneDrive, true);
        }
    }

    internal static void StageExecutable(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static bool RemoveOriginalCopy(string source, string installed)
    {
        if (!Path.IsPathFullyQualified(source) ||
            string.Equals(Path.GetFullPath(source), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var original = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var running = File.OpenRead(installed);
            if (original.Length != running.Length || !SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(running)))
                return false;
            File.Delete(source);
            return true;
        }
        catch (FileNotFoundException error) when (string.Equals(error.FileName, source, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    private static string ReceiptPath(string id) => Path.Combine(Path.GetDirectoryName(InstalledExe)!, $"move-{id}.json");
}
