using System.IO.Pipes;
using Microsoft.Win32;

namespace Pipopu;

internal static class ExplorerIntegration
{
    private const string Verb = @"Software\Classes\*\shell\OpenInPipopu";
    private const string Argument = "--open-file";
    private static string PipeName => $"Pipopu.OpenFile.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    internal static string? RequestedFile(string[] args) => AppPlacement.IsInstalled && args.Length == 2 &&
        args[0] == Argument && Path.IsPathFullyQualified(args[1]) ? args[1] : null;

    internal static void Register()
    {
        if (!AppPlacement.IsInstalled)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Verb);
            key.SetValue(null, "Open in Pipopu");
            key.SetValue("Icon", AppPlacement.InstalledExe);
            key.SetValue("MultiSelectModel", "Single");
            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{AppPlacement.InstalledExe}\" {Argument} \"%1\"");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TransferLog.Write($"Explorer registration failed reason={error.GetType().Name}");
        }
    }

    internal static bool Forward(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(path);
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            TransferLog.Write($"Explorer handoff failed reason={error.GetType().Name}");
            return false;
        }
    }

    internal static async Task ListenAsync(Action<string> open, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                var path = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
                if (path is not null && Path.IsPathFullyQualified(path))
                    open(path);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TransferLog.Write($"Explorer listener stopped reason={error.GetType().Name}");
        }
    }
}
