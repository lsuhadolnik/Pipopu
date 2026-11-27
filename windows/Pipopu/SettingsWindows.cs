using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pipopu;

internal static class SettingsWindows
{
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

    internal static void Trusted(BluetoothServer server)
    {
        var (window, body) = CreateWindow("Trusted devices", 390);
        var devices = new ListBox { Height = 180, DisplayMemberPath = "Value" };
        body.Children.Add(devices);
        void Refresh() => devices.ItemsSource = server.Trust.All().OrderBy(item => item.Value).ToArray();
        Refresh();
        AddButton(body, "Forget selected device", () =>
        {
            if (devices.SelectedItem is not KeyValuePair<string, string> peer)
                return;
            try { server.Forget(peer.Key); Refresh(); }
            catch (Exception error) { MessageBox.Show(window, error.Message, "Pipopu"); }
        });
        window.Show();
        window.Activate();
    }

    internal static Window Settings(SettingsStore store, OneDrive service)
    {
        var (window, body) = CreateWindow("Pipopu Settings", 490);
        var lifetime = new CancellationTokenSource();
        var cloudStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
        var location = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 3) };
        var locationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        bool signingIn = false;
        Uri? signInPage = null;

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
                    cloudStatus.Text = $"Enter code {code} on the Microsoft sign-in page.";
                    open.Visibility = Visibility.Visible;
                }, lifetime.Token);
                cloudStatus.Text = "Signed in to OneDrive.";
            }
            catch (Exception error) { cloudStatus.Text = error.Message; }
            finally
            {
                signInPage = null;
                signingIn = false;
                open.Visibility = Visibility.Collapsed;
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
