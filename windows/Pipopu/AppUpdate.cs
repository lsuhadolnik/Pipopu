using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pipopu;

internal sealed class AppUpdate
{
    private const string ApplyArgument = "--apply-update";
    private const string HelperSuffix = ".update-helper.exe";
    private const long MaxDownloadBytes = 256 * 1024 * 1024;
    internal const string RestartArgument = "--after-update";
    private static readonly Uri Feed = new("https://pipopu.solutions.si/update.json");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly string target = AppPlacement.CurrentExe;
    private string? readyHash;

    private sealed record Manifest(string Version, string Sha256, long Size);

    internal static string VersionText => typeof(AppUpdate).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];

    internal async Task DownloadAsync(CancellationToken cancellation)
    {
        if (!AppPlacement.IsSingleFile || readyHash is not null)
            return;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var token = deadline.Token;
            using var manifestReply = await Client.GetAsync(Feed, token).ConfigureAwait(false);
            manifestReply.EnsureSuccessStatusCode();
            var manifest = JsonSerializer.Deserialize<Manifest>(await manifestReply.Content.ReadAsStringAsync(token)
                .ConfigureAwait(false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null || !Version.TryParse(manifest.Version, out var version) ||
                !Version.TryParse(VersionText, out var current) || version <= current ||
                manifest.Size is <= 0 or > MaxDownloadBytes || !IsHash(manifest.Sha256))
                return;

            string stage = target + ".update-download";
            try
            {
                var download = new Uri(Feed, $"download/Pipopu.exe?v={version}");
                using var reply = await Client.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                reply.EnsureSuccessStatusCode();
                await using var source = await reply.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using (var output = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        written += count;
                        if (written > manifest.Size)
                            throw new InvalidDataException("The update exceeded its advertised size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                    if (written != manifest.Size)
                        throw new InvalidDataException("The update ended early.");
                }
                await using var file = File.OpenRead(stage);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false))
                    .Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update checksum did not match.");
                await file.DisposeAsync().ConfigureAwait(false);
                File.Move(stage, target + ".update-ready", overwrite: true);
                readyHash = manifest.Sha256;
            }
            finally { File.Delete(stage); }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
            or JsonException or OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
                TransferLog.Write($"Update check failed reason={error.GetType().Name}");
        }
    }

    internal bool IsReady => readyHash is not null;

    internal async Task PrepareHelperAsync(CancellationToken cancellation) =>
        await Task.Run(() => AppPlacement.StageExecutable(target, target + HelperSuffix), cancellation);

    internal void LaunchHelper()
    {
        using var parent = Process.GetCurrentProcess();
        Start(target + HelperSuffix, ApplyArgument, parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), readyHash!);
    }

    internal static bool RunHelper(string[] args)
    {
        if (!AppPlacement.CurrentExe.EndsWith(HelperSuffix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (args.Length != 4 || args[0] != ApplyArgument || !int.TryParse(args[1], out int pid) ||
            !long.TryParse(args[2], out long started) || !IsHash(args[3]))
            return true;
        string target = AppPlacement.CurrentExe[..^HelperSuffix.Length];
        string stage = target + ".update-ready";
        string backup = target + ".update-backup";
        try
        {
            try
            {
                using var parent = Process.GetProcessById(pid);
                if (parent.StartTime.ToUniversalTime().Ticks == started && !parent.WaitForExit(30_000))
                    return true;
            }
            catch (ArgumentException) { }

            using var instance = new Mutex(false, Program.InstanceName);
            try { if (!instance.WaitOne(TimeSpan.FromSeconds(15))) return true; }
            catch (AbandonedMutexException) { }
            try
            {
                try
                {
                    using (var file = File.OpenRead(stage))
                    {
                        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(args[3], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The staged update checksum changed.");
                    }
                    File.Replace(stage, target, backup);
                    try { Start(target, RestartArgument); }
                    catch
                    {
                        File.Move(backup, target, overwrite: true);
                        throw;
                    }
                }
                catch (Exception error) when (IsApplyError(error))
                {
                    TransferLog.Write($"Update apply failed reason={error.GetType().Name}");
                    Start(target, RestartArgument);
                }
            }
            finally { instance.ReleaseMutex(); }
        }
        catch (Exception error) when (IsApplyError(error))
        {
            TransferLog.Write($"Update restart failed reason={error.GetType().Name}");
        }
        return true;
    }

    internal static void CleanUp()
    {
        foreach (string suffix in new[] { HelperSuffix, ".update-backup" })
        {
            try { File.Delete(AppPlacement.CurrentExe + suffix); }
            catch (Exception error) when (IsApplyError(error)) { }
        }
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static bool IsApplyError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or System.ComponentModel.Win32Exception;

    private static void Start(string exe, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Pipopu could not restart.");
    }
}
