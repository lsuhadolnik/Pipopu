using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Pipopu;

internal partial class Composer : FlyoutWindow
{
    private readonly BluetoothServer server;
    private readonly Action showSettings;
    private Transfer? attachment;
    private string? pendingExplorerFile;
    private CancellationTokenSource? sendCancellation;
    private bool sending;
    private bool exiting;
    private bool renaming;

    internal Composer(BluetoothServer server, Action showSettings)
    {
        this.server = server;
        this.showSettings = showSettings;
        InitializeComponent();
        RefreshDeviceName();
        SendView.CancelRequested += () => sendCancellation?.Cancel();
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
    }

    internal void PermitClose() => exiting = true;
    internal bool IsBusy => sending || attachment is not null || Message.Text.Length > 0;

    internal void OpenFile(string path)
    {
        if (sending)
        {
            pendingExplorerFile = path;
            return;
        }
        TrySetPayload(() => AttachFile(path));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!exiting)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(Hide);
        }
        base.OnClosing(e);
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        FinishRename(save: true);
        Hide();
    }

    internal void SetBluetoothStatus(string message)
    {
        BluetoothIndicator.ToolTip = message;
        AutomationProperties.SetName(BluetoothIndicator, message);
    }

    internal void SetPairingCode(string? code) => SendView.PairingCode(code);

    private void RefreshDeviceName()
    {
        NameLabel.Text = server.DeviceName;
        AutomationProperties.SetName(NameButton, $"Change device name: {server.DeviceName}");
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
        NameEdit.Text = server.DeviceName;
        renaming = true;
        NameButton.Visibility = Visibility.Collapsed;
        NameEdit.Visibility = Visibility.Visible;
        NameEdit.Focus();
        NameEdit.SelectAll();
    }

    private void NameEditLostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename(save: true);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (renaming && e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            FinishRename(save: e.Key == Key.Enter);
            NameButton.Focus();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void FinishRename(bool save)
    {
        if (!renaming)
            return;
        renaming = false;

        if (save)
        {
            TrySetPayload(() => server.Rename(NameEdit.Text));
            RefreshDeviceName();
        }
        NameEdit.Visibility = Visibility.Collapsed;
        NameButton.Visibility = Visibility.Visible;
    }

    internal void RefreshPeers()
    {
        string? selected = (Peers.SelectedItem as BluetoothServer.Peer)?.Id;
        var devices = server.GetPeers();
        Peers.ItemsSource = devices;
        Peers.SelectedItem = devices.FirstOrDefault(peer => peer.Id == selected);
        if (Peers.SelectedIndex < 0 && devices.Length == 1)
            Peers.SelectedIndex = 0;
        Peers.Visibility = devices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPeers.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSend();
    }

    private void PasteClick(object sender, RoutedEventArgs e) => TrySetPayload(() =>
    {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            AttachImage(image);
        }
        else if (Clipboard.ContainsText())
        {
            SetAttachment(null);
            Message.Text = Clipboard.GetText();
        }
        else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: 1 } files)
        {
            AttachFile(files[0]!);
        }
        else
        {
            Status.Text = "Copy text, an image, or a single file first.";
        }
    });

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose a file to send" };
        if (picker.ShowDialog(this) == true)
            TrySetPayload(() => AttachFile(picker.FileName));
    }

    private void ClearClick(object sender, RoutedEventArgs e) => SetAttachment(null);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = !sending && (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (sending)
        {
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Handled = true;
            TrySetPayload(() =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
                    AttachFile(files[0]);
                else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                    AttachImage(bitmap);
                else if (e.Data.GetData(DataFormats.Bitmap) is System.Drawing.Image image)
                {
                    using var bytes = new MemoryStream();
                    image.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
                    SetImageAttachment(bytes);
                }
                else
                    throw new InvalidDataException("Drop a single image or file.");
            });
        }
    }

    private void TrySetPayload(Action action)
    {
        try
        {
            Status.Text = "";
            action();
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
        }
    }

    private void AttachFile(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > Transfer.FileLimit)
            throw new InvalidDataException("The file exceeds the 5 GB limit.");

        string name = Path.GetFileName(path);
        var kind = Transfer.IsImageFileName(name) ? TransferKind.Image : TransferKind.File;
        if (input.Length > Transfer.Limit)
            SetAttachment(new(kind, name, [], new TransferFile(path, input.Length, owned: false)));
        else
        {
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
                throw new IOException("The file changed while Pipopu was reading it.");
            SetAttachment(new(kind, name, bytes));
        }
    }

    private void AttachImage(BitmapSource image)
    {
        using var bytes = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(bytes);
        SetImageAttachment(bytes);
    }

    private void SetImageAttachment(MemoryStream bytes)
    {
        if (bytes.Length > Transfer.FileLimit)
            throw new InvalidDataException("The image exceeds the 5 GB limit.");
        if (bytes.Length <= Transfer.Limit)
        {
            SetAttachment(new(TransferKind.Image, "Clipboard image.png", bytes.ToArray()));
        }
        else
        {
            var file = new TransferFile(TransferFile.TemporaryPath(), bytes.Length);
            try
            {
                using (var output = File.Create(file.Path))
                    bytes.WriteTo(output);
                SetAttachment(new(TransferKind.Image, "Clipboard image.png", [], file));
            }
            catch { file.Dispose(); throw; }
        }
    }

    private void SetAttachment(Transfer? value)
    {
        attachment?.File?.Dispose();
        attachment = value;
        MessageInput.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
        AttachmentLabel.Text = value is null
            ? "Drop an image or file up to 5 GB"
            : $"{value.Name} ({value.Size:N0} bytes)";
        AttachmentLabel.ToolTip = value is null ? null : AttachmentLabel.Text;
        ClearAttachment.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshSend();
    }

    private void RefreshSend(object sender, RoutedEventArgs e) => RefreshSend();

    private void RefreshSend()
    {
        if (SecureSendButton is null)
            return;
        bool hasPeer = !sending && Peers.SelectedItem is BluetoothServer.Peer;
        bool hasDraft = attachment is not null || Message.Text.Length > 0;
        SendButton.IsEnabled = hasPeer && hasDraft;
        SecureSendButton.IsEnabled = hasPeer && Peers.SelectedItem is BluetoothServer.Peer peer &&
            peer.Version >= WireProtocol.CloudAndSecure;
    }

    private void SendOptionsClick(object sender, RoutedEventArgs e)
    {
        CloudSendOption.IsEnabled = (attachment is not null || Message.Text.Length > 0) &&
            (SecureSendButton.IsEnabled || (Peers.SelectedItem is null && server.Trust.All().Length > 0));
        SendOptions.ContextMenu.PlacementTarget = SendOptions;
        SendOptions.ContextMenu.IsOpen = true;
    }

    private void SettingsClick(object sender, RoutedEventArgs e) => showSettings();

    private async void SendClick(object sender, RoutedEventArgs e) => await SendDraftAsync(overCloud: false);

    private async void SendCloudClick(object sender, RoutedEventArgs e)
    {
        if (Peers.SelectedItem is BluetoothServer.Peer)
        {
            await SendDraftAsync(overCloud: true);
        }
        else if (SettingsWindows.ChooseCloudRecipient(this, server.Trust) is { } recipient)
        {
            await SendAsync(attachment ?? new Transfer(TransferKind.Text, "", Encoding.UTF8.GetBytes(Message.Text)),
                overCloud: true, offline: recipient);
        }
    }

    private Task SendDraftAsync(bool overCloud) =>
        SendAsync(attachment ?? new Transfer(TransferKind.Text, "", Encoding.UTF8.GetBytes(Message.Text)), overCloud);

    private async void SendSecureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string value = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (value.Length == 0)
            {
                Status.Text = "Copy the text you want to send securely first.";
                return;
            }
            if (Encoding.UTF8.GetByteCount(value) > Transfer.Limit)
            {
                Status.Text = "Secure clipboard text must be 500 KB or smaller.";
                return;
            }
            await SendAsync(new Transfer(TransferKind.Text, "", Encoding.UTF8.GetBytes(value), Secure: true), overCloud: false);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Status.Text = "The clipboard is busy. Try again.";
        }
    }

    private async Task SendAsync(Transfer transfer, bool overCloud, KeyValuePair<string, string>? offline = null)
    {
        var peer = Peers.SelectedItem as BluetoothServer.Peer;
        if (sending || (offline is null && peer is null))
            return;
        string recipient = offline?.Value ?? peer!.Name;

        using var cancellation = new CancellationTokenSource();
        sendCancellation = cancellation;
        sending = true;
        RefreshSend();
        Status.Text = "";
        SendView.Begin(recipient);
        ComposeView.Visibility = Visibility.Collapsed;
        try
        {
            if (offline is { } trusted)
            {
                if (!server.Trust.Contains(trusted.Key))
                    throw new IOException("This device is no longer trusted.");
                await server.Deferred.EnqueueAsync(Guid.NewGuid().ToString("N"), trusted.Key, trusted.Value,
                    transfer, cancellation.Token);
            }
            else
            {
                await server.SendAsync(peer!, transfer, new Progress<int>(value =>
                {
                    if (sendCancellation == cancellation)
                        SendView.Report(value);
                }), cancellation.Token, overCloud, new Progress<TransferProgress>(value =>
                {
                    if (sendCancellation == cancellation)
                        SendView.ReportNetwork(value);
                }));
            }
            if (!exiting)
            {
                if (ReferenceEquals(transfer, attachment))
                    SetAttachment(null);
                if (overCloud)
                    Status.Text = "Queued. Check Cloud transfers from the tray.";
                else
                {
                    SendView.Complete();
                    Status.Text = $"Delivered to {recipient}";
                    await Task.Delay(900);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status.Text = "Send cancelled.";
        }
        catch (Exception error)
        {
            if (!exiting)
                Status.Text = error is OperationCanceledException or TimeoutException
                    ? "The device stopped responding. Keep Pipopu open and try again."
                    : error.Message;
        }
        finally
        {
            sendCancellation = null;
            sending = false;
            if (!exiting)
            {
                SendView.Visibility = Visibility.Collapsed;
                ComposeView.Visibility = Visibility.Visible;
                RefreshSend();
                if (pendingExplorerFile is { } path)
                {
                    pendingExplorerFile = null;
                    OpenFile(path);
                }
            }
        }
    }
}
