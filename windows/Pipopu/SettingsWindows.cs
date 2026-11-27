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

    internal static void Cloud(OneDrive service)
    {
        var (window, body) = CreateWindow("OneDrive fallback", 430);
        var lifetime = new CancellationTokenSource();
        body.Children.Add(new TextBlock
        {
            Text = "Use an Entra public-client registration with Files.ReadWrite delegated access. Pipopu uses this account when the local connection is unavailable.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        body.Children.Add(new TextBlock { Text = "Application (client) ID" });
        var client = new TextBox { Text = OneDrive.ClientId, Margin = new Thickness(0, 5, 0, 8) };
        body.Children.Add(client);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
        body.Children.Add(status);
        Uri? signInPage = null;
        var open = AddButton(body, "Open Microsoft sign-in", () =>
        {
            if (signInPage is not null)
                Process.Start(new ProcessStartInfo(signInPage.AbsoluteUri) { UseShellExecute = true });
        });
        open.Visibility = Visibility.Collapsed;
        var signIn = AddButton(body, "Sign in", null);
        var signOut = AddButton(body, "Sign out", null);
        signIn.Click += async (_, _) =>
        {
            signIn.IsEnabled = signOut.IsEnabled = false;
            status.Text = "Requesting a Microsoft sign-in code…";
            try
            {
                await service.SignInAsync(client.Text.Trim(), (code, uri) => window.Dispatcher.Invoke(() =>
                {
                    signInPage = uri;
                    status.Text = $"Enter code {code} on the Microsoft sign-in page.";
                    open.Visibility = Visibility.Visible;
                }), lifetime.Token);
                status.Text = "Signed in to OneDrive.";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { signIn.IsEnabled = signOut.IsEnabled = true; open.Visibility = Visibility.Collapsed; }
        };
        signOut.Click += async (_, _) =>
        {
            await service.SignOutAsync();
            status.Text = "Signed out.";
        };
        window.Closed += (_, _) => lifetime.Cancel();
        window.Show();
        window.Activate();
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

    private static Button AddButton(StackPanel body, string label, Action? click)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 4, 9, 4)
        };
        if (click is not null)
            button.Click += (_, _) => click();
        body.Children.Add(button);
        return button;
    }
}
