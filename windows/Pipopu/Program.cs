using System.Windows;
using System.Windows.Threading;
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
    private readonly SettingsStore settings = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool exiting;
    private ReceivedPopup? popup;
    private Window? settingsWindow;

    internal TrayApp(Application app)
    {
        this.app = app;
        composer = new Composer(server);
        using var iconStream = Application.GetResourceStream(new Uri(Program.IconResource)).Stream;
        appIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        menu.Items.Add("Open Pipopu", null, (_, _) => ShowComposer());
        menu.Items.Add("Trusted devices…", null, (_, _) => SettingsWindows.Trusted(server));
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
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
        server.TransferReceived += transfer => OnUI(() =>
        {
            if (exiting)
            {
                transfer.File?.Dispose();
                return;
            }
            var preferences = settings.Current;
            var cancellation = lifetime.Token;
            Task<string>? save = preferences.AutoSave && (transfer.Kind is TransferKind.Image or TransferKind.File)
                ? Task.Run(() => ReceivedFiles.SaveAsync(transfer, preferences, server.Cloud, cancellation))
                : null;
            receipts.Enqueue(new Receipt(transfer, save));
            ShowReceived();
        });
    }

    private void OnUI(Action action) => app.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

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
            settingsWindow = SettingsWindows.Settings(settings, server.Cloud);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized)
            settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
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
        lifetime.Cancel();
        foreach (var receipt in receipts)
            ReceivedFiles.Release(receipt.Transfer, receipt.Save);
        lifetime.Dispose();
    }
}
