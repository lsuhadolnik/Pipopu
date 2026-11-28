using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using Forms = System.Windows.Forms;

namespace Pipopu;

internal static class Program
{
    internal const string IconResource = "pack://application:,,,/Pipopu;component/Pipopu.ico";

    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, "Local\\Pipopu.Tray", out bool firstInstance);
        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", true);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Pipopu;component/FlyoutStyles.xaml")
        });

        using var tray = new TrayApp(app);
        tray.ShowComposer();
        app.Run();
    }
}

internal sealed class TrayApp : IDisposable
{
    private sealed record Receipt(Transfer Transfer, Task<string>? Save);

    private readonly Application app;
    private readonly BluetoothServer server = new();
    private readonly Composer composer;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon appIcon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Queue<Receipt> receipts = new();
    private readonly Dictionary<string, TransferProgress> incoming = new();
    private readonly HashSet<string> dismissedIncoming = new();
    private readonly SettingsStore settings = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer cloudTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool exiting;
    private ReceivedPopup? popup;
    private IncomingPopup? incomingPopup;
    private string? displayedIncoming;
    private Window? settingsWindow;
    private Window? cloudWindow;

    internal TrayApp(Application app)
    {
        this.app = app;
        composer = new Composer(server);
        using var iconStream = Application.GetResourceStream(new Uri(Program.IconResource)).Stream;
        appIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        menu.Items.Add("Open Pipopu", null, (_, _) => ShowComposer());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Cloud transfers…", null, (_, _) => ShowCloudTransfers());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            exiting = true;
            composer.PermitClose();
            app.Shutdown();
        });
        tray = new Forms.NotifyIcon
        {
            Icon = appIcon,
            Text = "Pipopu",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowComposer(); };

        server.PeersChanged += () => OnUI(composer.RefreshPeers);
        server.StatusChanged += message => OnUI(() => composer.SetBluetoothStatus(message));
        server.PairingCodeChanged += code => OnUI(() => composer.SetPairingCode(code));
        server.ConfirmTrust = (name, code, cancellation) =>
            SettingsWindows.ConfirmAsync(app.Dispatcher, name, code, cancellation);
        server.TransferReceived += transfer => OnUI(() => QueueReceipt(transfer, autoSave: true));
        server.IncomingTransferProgress += progress => OnUI(() => UpdateIncoming(progress));
        cloudTimer.Tick += async (_, _) => await CheckCloudAsync();
        cloudTimer.Start();
    }

    private async Task CheckCloudAsync()
    {
        try { await server.Deferred.PumpAsync(lifetime.Token); }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    private void QueueReceipt(Transfer transfer, bool autoSave)
    {
        if (exiting)
        {
            transfer.File?.Dispose();
            return;
        }
        var preferences = settings.Current;
        var cancellation = lifetime.Token;
        Task<string>? save = autoSave && preferences.AutoSave && (transfer.Kind is TransferKind.Image or TransferKind.File)
            ? Task.Run(() => ReceivedFiles.SaveAsync(transfer, preferences, server.Cloud, cancellation))
            : null;
        receipts.Enqueue(new Receipt(transfer, save));
        ShowReceived();
    }

    private void OnUI(Action action) => app.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    private void UpdateIncoming(TransferProgress progress)
    {
        if (exiting)
            return;
        if (progress.Done)
        {
            bool dismissed = dismissedIncoming.Remove(progress.Id);
            if (progress.Failed && !dismissed)
                incoming[progress.Id] = progress;
            else
                incoming.Remove(progress.Id);
        }
        else if (!dismissedIncoming.Contains(progress.Id))
        {
            if (!incoming.ContainsKey(progress.Id))
                displayedIncoming = progress.Id;
            incoming[progress.Id] = progress;
        }
        ShowIncoming();
    }

    private void ShowIncoming()
    {
        if (displayedIncoming is null || !incoming.TryGetValue(displayedIncoming, out var progress))
            progress = incoming.Values.FirstOrDefault(entry => !entry.Done) ?? incoming.Values.FirstOrDefault();
        if (progress is null)
        {
            displayedIncoming = null;
            incomingPopup?.Close();
            return;
        }
        displayedIncoming = progress.Id;
        if (incomingPopup is null)
        {
            incomingPopup = new IncomingPopup();
            incomingPopup.Closed += (_, _) =>
            {
                if (displayedIncoming is string id && incoming.Remove(id, out var closed) && !closed.Done)
                    dismissedIncoming.Add(id);
                incomingPopup = null;
                displayedIncoming = null;
                if (!exiting)
                    ShowIncoming();
            };
            incomingPopup.Update(progress);
            incomingPopup.Present(animate: true);
        }
        else
        {
            incomingPopup.Update(progress);
        }
    }

    internal void ShowComposer()
    {
        if (popup is not null)
            popup.Topmost = false;
        composer.Present();
    }

    private void ShowSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = SettingsWindows.Settings(settings, server);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized)
            settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    private void ShowCloudTransfers()
    {
        if (cloudWindow is null)
        {
            cloudWindow = SettingsWindows.CloudStatus(server.Deferred, server.Cloud, CheckCloudAsync,
                transfer => QueueReceipt(transfer, autoSave: false));
            cloudWindow.Closed += (_, _) => cloudWindow = null;
            cloudWindow.Show();
        }
        if (cloudWindow.WindowState == WindowState.Minimized)
            cloudWindow.WindowState = WindowState.Normal;
        cloudWindow.Activate();
    }

    private void ShowReceived()
    {
        if (exiting || popup is not null || !receipts.TryDequeue(out var receipt))
            return;
        var received = new ReceivedPopup(receipt.Transfer, receipt.Save);
        received.Closed += (_, _) =>
        {
            popup = null;
            ShowReceived();
        };
        popup = received;
        received.Present(animate: true);
    }

    public void Dispose()
    {
        tray.Dispose();
        appIcon.Dispose();
        menu.Dispose();
        server.Dispose();
        cloudTimer.Stop();
        lifetime.Cancel();
        foreach (var receipt in receipts)
            ReceivedFiles.Release(receipt.Transfer, receipt.Save);
        lifetime.Dispose();
    }
}
