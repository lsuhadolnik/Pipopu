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
    private readonly Application app;
    private readonly BluetoothServer server = new();
    private readonly Composer composer;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon appIcon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Queue<Transfer> receipts = new();
    private bool exiting;
    private ReceivedPopup? popup;

    internal TrayApp(Application app)
    {
        this.app = app;
        composer = new Composer(server);
        using var iconStream = Application.GetResourceStream(new Uri(Program.IconResource)).Stream;
        appIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        menu.Items.Add("Open Pipopu", null, (_, _) => ShowComposer());
        menu.Items.Add("Trusted devices…", null, (_, _) => SettingsWindows.Trusted(server));
        menu.Items.Add("OneDrive fallback…", null, (_, _) => SettingsWindows.Cloud(server.Cloud));
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
            receipts.Enqueue(transfer);
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

    private void ShowReceived()
    {
        if (exiting || popup is not null || !receipts.TryDequeue(out var transfer))
            return;
        var received = new ReceivedPopup(transfer);
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
        foreach (var transfer in receipts)
            transfer.File?.Dispose();
    }
}
