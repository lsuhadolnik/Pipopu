using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pipopu;

internal static class SettingsWindows
{
    internal static void ShowOneDriveSetup(Action markSeen, Action openSettings)
    {
        var (window, body) = CreateWindow("Welcome to Pipopu", 430);
        window.ShowInTaskbar = false;
        var heading = Section("Send large files with OneDrive");
        heading.FontSize = 22;
        body.Children.Add(heading);
        body.Children.Add(new TextBlock
        {
            Text = "Use OneDrive to transfer encrypted files when your devices are on different networks. You can also save received files to a OneDrive folder.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12)
        });
        var actions = Actions(body);
        var setup = AddButton(actions, "Set up OneDrive", () => { window.Close(); openSettings(); });
        setup.SetResourceReference(Control.StyleProperty, "AccentButtonStyle");
        AddButton(actions, "Maybe later", window.Close);
        window.Closed += (_, _) => markSeen();
        window.Show();
        window.Activate();
    }

    internal static async Task<bool> ConfirmAsync(Dispatcher dispatcher, string name, string code, CancellationToken cancellation)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? dialog = null;
        using var registration = cancellation.Register(() => dispatcher.BeginInvoke(() =>
        {
            answer.TrySetResult(false);
            dialog?.Close();
        }));
        await dispatcher.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested)
            {
                answer.TrySetResult(false);
                return;
            }
            (dialog, var body) = CreateWindow("Trust this device?", 390);
            var window = dialog;
            window.Topmost = true;
            body.Children.Add(new TextBlock
            {
                Text = $"Only trust {name} if the same code is shown on that device.",
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock { Text = code, FontSize = 30, Margin = new Thickness(0, 12, 0, 12) });
            AddButton(body, "Trust device", () => { answer.TrySetResult(true); window.Close(); });
            AddButton(body, "Decline", () => window.Close());
            window.Closed += (_, _) => { dialog = null; answer.TrySetResult(false); };
            window.Show();
            window.Activate();
        });
        return await answer.Task;
    }

    internal static Window Settings(SettingsStore store, BluetoothServer server)
    {
        var (window, body) = CreateWindow("Pipopu Settings", 490);
        var service = server.Cloud;
        var lifetime = new CancellationTokenSource();
        var cloudStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
        var location = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 3) };
        var locationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        bool signingIn = false;
        Uri? signInPage = null;
        string? signInCode = null;

        body.Children.Add(Section("OneDrive"));
        body.Children.Add(new TextBlock
        {
            Text = "Sign in for the transfer fallback and to save received files to OneDrive.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(cloudStatus);
        var accountActions = Actions(body);
        var signIn = AddButton(accountActions, "Sign in to OneDrive", null);
        var signOut = AddButton(accountActions, "Sign out", null);
        var copyCode = AddButton(accountActions, "Copy code", () => TryCopyCode());
        copyCode.Visibility = Visibility.Collapsed;
        var open = AddButton(body, "Open Microsoft sign-in", () =>
        {
            if (signInPage is not null)
            {
                try { Process.Start(new ProcessStartInfo(signInPage.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception error) { cloudStatus.Text = error.Message; }
            }
        });
        open.Visibility = Visibility.Collapsed;

        var heading = Section("Received files");
        heading.Margin = new Thickness(0, 22, 0, 8);
        body.Children.Add(heading);
        var autoSave = new CheckBox { Content = "Save received files automatically" };
        body.Children.Add(autoSave);
        body.Children.Add(new TextBlock
        {
            Text = "Received files location", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0)
        });
        body.Children.Add(location);
        var folderActions = Actions(body);
        var localFolder = AddButton(folderActions, "Choose local folder…", null);
        var cloudFolder = AddButton(folderActions, "Choose OneDrive folder…", null);
        body.Children.Add(locationStatus);
        AddTrustedDevices(window, body, server);
        var logs = AddButton(body, "Open transfer logs", () =>
        {
            try
            {
                Directory.CreateDirectory(TransferLog.Folder);
                Process.Start(new ProcessStartInfo(TransferLog.Folder) { UseShellExecute = true });
            }
            catch (Exception error) { MessageBox.Show(window, error.Message, "Pipopu"); }
        });
        logs.Margin = new Thickness(0, 14, 0, 0);
        var assembly = typeof(SettingsWindows).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString(3);
        if (version is not null)
        {
            var versionText = new TextBlock
            {
                Text = $"Version {version}", Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            versionText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            body.Children.Add(versionText);
        }
        cloudStatus.Text = service.IsSignedIn ? "Signed in to OneDrive." : "Sign in when you want to use OneDrive.";
        Refresh();

        autoSave.Click += (_, _) => Apply(store.Current with { AutoSave = autoSave.IsChecked == true });
        localFolder.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for received files" };
            if (Directory.Exists(store.Current.LocalFolder))
                picker.InitialDirectory = store.Current.LocalFolder;
            if (picker.ShowDialog(window) == true)
                Apply(store.Current with { LocalFolder = picker.FolderName, CloudFolder = null });
        };
        cloudFolder.Click += (_, _) =>
        {
            if (service.IsSignedIn && ChooseOneDriveFolder(window, service) is { } folder)
                Apply(store.Current with { CloudFolder = folder });
        };
        signIn.Click += async (_, _) =>
        {
            signingIn = true;
            Refresh();
            cloudStatus.Text = "Requesting a Microsoft sign-in code…";
            try
            {
                await service.SignInAsync((code, uri) =>
                {
                    signInPage = uri;
                    signInCode = code;
                    copyCode.Visibility = Visibility.Visible;
                    open.Visibility = Visibility.Visible;
                    TryCopyCode();
                }, lifetime.Token);
                cloudStatus.Text = "Signed in to OneDrive.";
            }
            catch (Exception error) { cloudStatus.Text = error.Message; }
            finally
            {
                signInPage = null;
                signInCode = null;
                signingIn = false;
                open.Visibility = Visibility.Collapsed;
                copyCode.Visibility = Visibility.Collapsed;
                Refresh();
            }
        };
        signOut.Click += async (_, _) =>
        {
            await service.SignOutAsync();
            cloudStatus.Text = "Signed out.";
            Refresh();
        };
        window.Closed += (_, _) => lifetime.Cancel();
        return window;

        void TryCopyCode()
        {
            if (signInCode is not { } code)
                return;
            try
            {
                Clipboard.SetText(code);
                cloudStatus.Text = $"Code {code} copied. Paste it on the Microsoft sign-in page.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                cloudStatus.Text = $"Enter code {code} on the Microsoft sign-in page, or try Copy code again.";
            }
        }

        void Apply(ReceivedFileSettings next)
        {
            try
            {
                store.Update(next);
                locationStatus.Text = "";
            }
            catch (Exception error) { locationStatus.Text = error.Message; }
            Refresh();
        }

        void Refresh()
        {
            var current = store.Current;
            autoSave.IsChecked = current.AutoSave;
            location.Text = current.Location;
            signIn.IsEnabled = !signingIn && !service.IsSignedIn;
            signOut.IsEnabled = !signingIn && service.IsSignedIn;
            cloudFolder.IsEnabled = !signingIn && service.IsSignedIn;
            if (locationStatus.Text.Length == 0 || locationStatus.Text == "Sign in to use the selected OneDrive folder.")
                locationStatus.Text = current.CloudFolder is not null && !service.IsSignedIn
                    ? "Sign in to use the selected OneDrive folder." : "";
        }
    }

    private static void AddTrustedDevices(Window window, Panel body, BluetoothServer server)
    {
        var heading = Section("Trusted devices");
        heading.Margin = new Thickness(0, 22, 0, 8);
        body.Children.Add(heading);
        var empty = new TextBlock { Text = "No trusted devices yet." };
        body.Children.Add(empty);
        var devices = new ListBox { MaxHeight = 140, DisplayMemberPath = "Value", SelectedValuePath = "Key" };
        System.Windows.Automation.AutomationProperties.SetName(devices, "Trusted devices");
        body.Children.Add(devices);
        var forget = AddButton(body, "Forget selected device", () =>
        {
            if (devices.SelectedValue is not string identity)
                return;
            try { server.Forget(identity); }
            catch (Exception error) { MessageBox.Show(window, error.Message, "Pipopu"); }
        });
        devices.SelectionChanged += (_, _) => forget.IsEnabled = devices.SelectedValue is not null;

        void Refresh()
        {
            var selection = devices.SelectedValue;
            var peers = server.Trust.All().OrderBy(peer => peer.Value).ToArray();
            devices.ItemsSource = peers;
            devices.SelectedValue = selection;
            devices.Visibility = forget.Visibility = peers.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            empty.Visibility = peers.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            forget.IsEnabled = devices.SelectedValue is not null;
        }

        void OnChange() => window.Dispatcher.BeginInvoke(Refresh);
        server.Trust.Changed += OnChange;
        window.Closed += (_, _) => server.Trust.Changed -= OnChange;
        Refresh();
    }

    private static OneDriveFolder? ChooseOneDriveFolder(Window owner, OneDrive service)
    {
        var (window, body) = CreateWindow("Choose OneDrive folder", 470);
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var lifetime = new CancellationTokenSource();
        var history = new List<OneDriveFolder>();
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        var listing = new ListBox { Height = 218, DisplayMemberPath = nameof(FolderRow.Name),
            Margin = new Thickness(0, 10, 0, 5) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(path);
        body.Children.Add(listing);
        body.Children.Add(status);
        var actions = Actions(body);
        var up = AddButton(actions, "Up", null);
        var open = AddButton(actions, "Open folder", null);
        var select = AddButton(actions, "Use this folder", null);
        AddButton(actions, "Cancel", () => window.Close());
        bool loading = true;
        bool folderAvailable = false;
        OneDriveFolder? chosen = null;

        window.Loaded += async (_, _) =>
        {
            try
            {
                history.Add(await service.RootFolderAsync(lifetime.Token));
                await RefreshAsync();
            }
            catch (Exception error)
            {
                status.Text = error.Message;
                loading = false;
                UpdateActions();
            }
        };
        window.Closed += (_, _) => lifetime.Cancel();
        listing.SelectionChanged += (_, _) => UpdateActions();
        listing.MouseDoubleClick += async (_, _) => await OpenSelectedAsync();
        open.Click += async (_, _) => await OpenSelectedAsync();
        up.Click += async (_, _) =>
        {
            if (loading || history.Count < 2)
                return;
            history.RemoveAt(history.Count - 1);
            await RefreshAsync();
        };
        select.Click += (_, _) =>
        {
            if (loading || !folderAvailable)
                return;
            chosen = history[^1];
            window.DialogResult = true;
        };
        UpdateActions();
        _ = window.ShowDialog();
        return chosen;

        async Task OpenSelectedAsync()
        {
            if (loading || listing.SelectedItem is not FolderRow selected)
                return;
            history.Add(selected.Folder);
            await RefreshAsync();
        }

        async Task RefreshAsync()
        {
            loading = true;
            folderAvailable = false;
            listing.ItemsSource = null;
            UpdateActions();
            path.Text = history[^1].Path;
            status.Text = "Loading folders…";
            try
            {
                var children = await service.ListFoldersAsync(history[^1], lifetime.Token);
                listing.ItemsSource = children.Select(folder => new FolderRow(folder)).ToArray();
                folderAvailable = true;
                status.Text = children.Length == 0 ? "This folder has no subfolders." : "";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { loading = false; UpdateActions(); }
        }

        void UpdateActions()
        {
            up.IsEnabled = !loading && history.Count > 1;
            open.IsEnabled = !loading && listing.SelectedItem is FolderRow;
            select.IsEnabled = !loading && folderAvailable;
        }
    }

    private sealed record FolderRow(OneDriveFolder Folder)
    {
        public string Name => Folder.Path[(Folder.Path.LastIndexOf('/') + 1)..];
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
    };

    private static WrapPanel Actions(Panel parent)
    {
        var actions = new WrapPanel();
        parent.Children.Add(actions);
        return actions;
    }
    internal static KeyValuePair<string, string>? ChooseCloudRecipient(Window owner, TrustStore trust)
    {
        var (window, body) = CreateWindow("Send over cloud", 390);
        window.Owner = owner;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        body.Children.Add(new TextBlock { Text = "Choose a trusted device." });
        var peers = new ListBox
        {
            Height = 150, DisplayMemberPath = "Value", Margin = new Thickness(0, 8, 0, 0),
            ItemsSource = trust.All().OrderBy(peer => peer.Value).ToArray()
        };
        body.Children.Add(peers);
        var send = AddButton(Actions(body), "Queue transfer", () => window.DialogResult = true);
        send.IsEnabled = false;
        peers.SelectionChanged += (_, _) => send.IsEnabled = peers.SelectedItem is KeyValuePair<string, string>;
        return window.ShowDialog() == true && peers.SelectedItem is KeyValuePair<string, string> peer ? peer : null;
    }

    internal static Window CloudStatus(CloudTransfers queue, OneDrive oneDrive, Func<Task> refreshCloud,
        Action<Transfer> openReceived)
    {
        var (window, body) = CreateWindow("Pipopu Cloud transfers", 550);
        body.Children.Add(new TextBlock
        {
            Text = "Queued transfers continue on OneDrive. Check here after reopening Pipopu.",
            TextWrapping = TextWrapping.Wrap
        });
        var list = new ListBox
        {
            Height = 230, DisplayMemberPath = nameof(CloudSummary.Caption), Margin = new Thickness(0, 10, 0, 5)
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(list);
        body.Children.Add(status);
        var actions = Actions(body);
        var refresh = AddButton(actions, "Check now", null);
        var open = AddButton(actions, "Open received file", null);
        var retry = AddButton(actions, "Retry upload", null);
        var lifetime = new CancellationTokenSource();
        bool closed = false;

        void Refresh()
        {
            if (closed)
                return;
            string? selected = (list.SelectedItem as CloudSummary)?.Id;
            var jobs = queue.List();
            list.ItemsSource = jobs;
            list.SelectedItem = jobs.FirstOrDefault(job => job.Id == selected);
            status.Text = !oneDrive.IsSignedIn ? "Sign in to OneDrive in Settings to resume cloud transfers." :
                jobs.Length == 0 ? "No cloud transfers yet." : "";
        }

        void OnChange() => window.Dispatcher.BeginInvoke(Refresh);
        queue.Changed += OnChange;
        window.Closed += (_, _) =>
        {
            closed = true;
            queue.Changed -= OnChange;
            lifetime.Cancel();
        };
        refresh.Click += async (_, _) =>
        {
            await refreshCloud();
            Refresh();
        };
        retry.Click += async (_, _) =>
        {
            if (list.SelectedItem is not CloudSummary { Outgoing: true, Status: CloudTransfers.Failed } job)
                return;
            queue.Retry(job.Id);
            await refreshCloud();
            Refresh();
        };
        open.Click += async (_, _) =>
        {
            if (list.SelectedItem is not CloudSummary { Outgoing: false, Status: CloudTransfers.Received } job)
                return;
            try { openReceived(await queue.OpenReceivedAsync(job.Id, lifetime.Token)); }
            catch (Exception error) { status.Text = error.Message; }
        };
        list.SelectionChanged += (_, _) =>
        {
            open.IsEnabled = list.SelectedItem is CloudSummary { Outgoing: false, Status: CloudTransfers.Received };
            retry.IsEnabled = list.SelectedItem is CloudSummary { Outgoing: true, Status: CloudTransfers.Failed };
        };
        open.IsEnabled = retry.IsEnabled = false;
        Refresh();
        return window;
    }

    private static (Window, StackPanel) CreateWindow(string title, double width)
    {
        var layout = new StackPanel { Margin = new Thickness(20) };
        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = layout
        };
        return (window, layout);
    }

    private static Button AddButton(Panel body, string label, Action? click)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(0, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 4, 9, 4)
        };
        if (click is not null)
            button.Click += (_, _) => click();
        body.Children.Add(button);
        return button;
    }
}
