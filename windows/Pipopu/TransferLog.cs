using System.Diagnostics;

namespace Pipopu;

internal static class TransferLog
{
    private const long MaximumBytes = 512 * 1024;
    private static readonly object Gate = new();
    internal static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pipopu", "Logs");

    // Pass only application event text, local transfer IDs, validated hosts, and numeric metadata.
    internal static void Write(string eventName)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string current = Path.Combine(Folder, "pipopu.log");
                if (File.Exists(current) && new FileInfo(current).Length >= MaximumBytes)
                    File.Move(current, Path.Combine(Folder, "pipopu.previous.log"), overwrite: true);
                File.AppendAllText(current, $"{DateTimeOffset.UtcNow:O} {eventName}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Debug.WriteLine(error.GetType().Name);
            }
        }
    }

    internal static string Host(Uri uri)
    {
        string host = uri.IdnHost;
        return host.Length is > 0 and <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            ? host : "(invalid host)";
    }
}
