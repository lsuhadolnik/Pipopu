using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pipopu;

internal static class Program
{
    internal const string IconResource = "pack://application:,,,/Pipopu;component/Pipopu.ico";
    internal const string InstanceName = "Local\\Pipopu.Tray";

    [STAThread]
    private static void Main(string[] args)
    {
        if (AppUpdate.RunHelper(args))
            return;
        bool finishingMove = AppPlacement.IsMoveRestart(args);
        string? file = ExplorerIntegration.RequestedFile(args);
        bool restarting = args.Contains(AppUpdate.RestartArgument, StringComparer.Ordinal);
        using var instance = new Mutex(false, InstanceName);
        try
        {
            if (!instance.WaitOne(finishingMove || restarting ? TimeSpan.FromSeconds(30) : TimeSpan.Zero))
            {
                if (file is not null && !ExplorerIntegration.Forward(file))
                    MessageBox.Show("Pipopu could not open this file. Try again in a moment.", "Pipopu");
                return;
            }
        }
        catch (AbandonedMutexException) { }

        try { Run(args, finishingMove, restarting, file); }
        finally { instance.ReleaseMutex(); }
    }

    private static void Run(string[] args, bool finishingMove, bool restarting, string? file)
    {
        TransferLog.Write("Pipopu started");
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
        ExplorerIntegration.Register();
        if (finishingMove)
        {
            var move = AppPlacement.FinishMove(args[1]);
            tray.ShowComposer();
            if (move.OpenOneDrive)
                tray.ShowSettings();
            if (move.CopyLeftBehind)
                MessageBox.Show("Pipopu is running from Local AppData. The original copy could not be removed.",
                    "Pipopu", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (file is not null)
        {
            tray.OpenFile(file);
        }
        else if (!restarting && !args.Contains(AppPlacement.StartupArgument, StringComparer.Ordinal))
        {
            if (!tray.ShowFirstRunSetup())
                tray.ShowComposer();
        }
        app.Run();
    }
}

internal sealed class TrayApp : IDisposable
{
    private sealed record Receipt(Transfer Transfer, Task<ReceivedSaveResult>? Save);

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
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly AppUpdate updater = new();
    private readonly System.Diagnostics.Stopwatch idle = System.Diagnostics.Stopwatch.StartNew();
    private DateTime nextCheck = DateTime.MinValue;
    private int cloudChecks;
    private bool updating;
    private bool helperPrepared;
    private bool exiting;
    private ReceivedPopup? popup;
    private IncomingPopup? incomingPopup;
    private string? displayedIncoming;
    private Window? settingsWindow;
    private Window? cloudWindow;
    private SetupWizard? setupWizard;

    internal TrayApp(Application app)
    {
        this.app = app;
        composer = new Composer(server, ShowSettings);
        using var iconStream = Application.GetResourceStream(new Uri(Program.IconResource)).Stream;
        appIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        menu.Items.Add("Open Pipopu", null, (_, _) => ShowComposer());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Cloud transfers…", null, (_, _) => ShowCloudTransfers());
        menu.Items.Add("Exit", null, (_, _) => Exit());
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
        if (AppPlacement.IsInstalled)
            _ = ExplorerIntegration.ListenAsync(path => OnUI(() => OpenFile(path)), lifetime.Token);
        OnUI(async () =>
        {
            try { await server.StartAsync(); }
            catch (Exception error) { composer.SetBluetoothStatus(error.Message); }
        });
        if (AppPlacement.IsSingleFile)
        {
            InputManager.Current.PostProcessInput += RecordInput;
            updateTimer.Tick += async (_, _) => await CheckUpdateAsync();
            updateTimer.Start();
            OnUI(async () => await CheckUpdateAsync());
        }
    }

    private void RecordInput(object sender, ProcessInputEventArgs args)
    {
        if (args.StagingItem.Input is KeyEventArgs or MouseButtonEventArgs or MouseWheelEventArgs or TouchEventArgs)
            idle.Restart();
    }

    private bool Busy => composer.IsBusy || server.HasActiveTransfer || cloudChecks > 0 ||
        popup is not null || setupWizard is not null;

    private async Task CheckUpdateAsync()
    {
        if (exiting)
            return;
        if (Busy)
            idle.Restart();
        if (updating)
            return;

        updating = true;
        try
        {
            if (!updater.IsReady && DateTime.UtcNow >= nextCheck)
            {
                AppUpdate.CleanUp();
                nextCheck = DateTime.UtcNow.AddHours(1);
                await updater.DownloadAsync(lifetime.Token);
            }
            if (updater.IsReady && !helperPrepared)
            {
                await updater.PrepareHelperAsync(lifetime.Token);
                helperPrepared = true;
            }
            if (updater.IsReady && !exiting && !Busy && idle.Elapsed >= TimeSpan.FromMinutes(10) &&
                server.TryPauseForUpdate())
            {
                try { updater.LaunchHelper(); }
                catch { server.ResumeAfterUpdateFailure(); throw; }
                Exit();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            idle.Restart();
            helperPrepared = false;
            if (!exiting)
                TransferLog.Write($"Update preparation failed reason={error.GetType().Name}");
        }
        finally { updating = false; }
    }

    private async Task CheckCloudAsync()
    {
        cloudChecks++;
        try { await server.Deferred.PumpAsync(lifetime.Token); }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested)
                TransferLog.Write($"Cloud queue check failed reason={error.GetType().Name}");
        }
        finally { cloudChecks--; }
    }

    private void QueueReceipt(Transfer transfer, bool autoSave)
    {
        if (exiting)
        {
            transfer.File?.Dispose();
            return;
        }
        idle.Restart();
        var preferences = settings.Current;
        var cancellation = lifetime.Token;
        Task<ReceivedSaveResult>? save = autoSave && preferences.AutoSave && (transfer.Kind is TransferKind.Image or TransferKind.File)
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
        idle.Restart();
        if (popup is not null)
            popup.Topmost = false;
        composer.Present();
    }

    internal void OpenFile(string path)
    {
        if (exiting)
            return;
        ShowComposer();
        composer.OpenFile(path);
    }

    internal bool ShowFirstRunSetup()
    {
        if (settings.HasSeenSetupWizard())
            return false;
        ShowSetupWizard(openComposerOnClose: true);
        return true;
    }

    private void ShowSetupWizard(bool openComposerOnClose = false)
    {
        if (setupWizard is not null)
        {
            setupWizard.Activate();
            return;
        }

        bool openOneDrive = false;
        string? startup = AppPlacement.StartupCommand();
        var wizard = new SetupWizard(AppPlacement.CanMove, AppPlacement.IsInstalled, startup is not null, choices =>
        {
            string target = choices.Move ? AppPlacement.InstalledExe : AppPlacement.CurrentExe;
            string? desiredStartup = choices.StartAtSignIn ? AppPlacement.StartupCommandFor(target) : null;
            string? previousStartup = AppPlacement.StartupCommand();
            bool startupChanged = !string.Equals(previousStartup, desiredStartup, StringComparison.Ordinal);
            if (choices.Move)
                AppPlacement.PrepareMove();
            if (startupChanged)
                AppPlacement.SetStartupCommand(desiredStartup);
            try
            {
                if (choices.Move)
                    AppPlacement.RestartInstalled(choices.OpenOneDrive);
            }
            catch
            {
                if (startupChanged)
                    AppPlacement.SetStartupCommand(previousStartup);
                throw;
            }
            if (choices.Move)
            {
                settings.MarkSetupWizardSeen();
                Exit();
            }
            else
                openOneDrive = choices.OpenOneDrive;
        });
        setupWizard = wizard;
        wizard.Closed += (_, _) =>
        {
            setupWizard = null;
            if (exiting)
                return;
            settings.MarkSetupWizardSeen();
            if (openComposerOnClose)
                ShowComposer();
            if (wizard.Completed && openOneDrive)
                ShowSettings();
        };
        wizard.Show();
        wizard.Activate();
    }

    internal void ShowSettings()
    {
        idle.Restart();
        if (settingsWindow is null)
        {
            settingsWindow = SettingsWindows.Settings(settings, server, () => ShowSetupWizard());
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized)
            settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    private void Exit()
    {
        exiting = true;
        composer.PermitClose();
        app.Shutdown();
    }

    private void ShowCloudTransfers()
    {
        idle.Restart();
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
        updateTimer.Stop();
        InputManager.Current.PostProcessInput -= RecordInput;
        lifetime.Cancel();
        foreach (var receipt in receipts)
            ReceivedFiles.Release(receipt.Transfer, receipt.Save);
        lifetime.Dispose();
    }
}
