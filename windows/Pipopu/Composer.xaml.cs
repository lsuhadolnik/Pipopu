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
    private Transfer? attachment;
    private CancellationTokenSource? sendCancellation;
    private bool sending;
    private bool exiting;
    private bool renaming;

    internal Composer(BluetoothServer server)
    {
        this.server = server;
        InitializeComponent();
        RefreshDeviceName();
        SendView.CancelRequested += () => sendCancellation?.Cancel();
        Loaded += StartBluetooth;
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
    }

    private async void StartBluetooth(object sender, RoutedEventArgs e)
    {
        Loaded -= StartBluetooth;
        try { await server.StartAsync(); }
        catch (Exception error) { SetBluetoothStatus(error.Message); }
    }

    internal void PermitClose() => exiting = true;

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
        if (input.Length > Transfer.Limit)
            throw new InvalidDataException("The file exceeds the 3 MB limit.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
            throw new IOException("The file changed while Pipopu was reading it.");

        string name = Path.GetFileName(path);
        var kind = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".webp" => TransferKind.Image,
            _ => TransferKind.File
        };
        SetAttachment(new(kind, name, bytes));
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
        if (bytes.Length > Transfer.Limit)
            throw new InvalidDataException("The image exceeds the 3 MB limit.");
        SetAttachment(new(TransferKind.Image, "Clipboard image.png", bytes.ToArray()));
    }

    private void SetAttachment(Transfer? value)
    {
        attachment = value;
        Message.IsEnabled = value is null;
        AttachmentLabel.Text = value is null
            ? "Drop an image or file up to 3 MB"
            : $"{value.Name} ({value.Data.Length:N0} bytes)";
        AttachmentLabel.ToolTip = value is null ? null : AttachmentLabel.Text;
        ClearAttachment.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshSend();
    }

    private void RefreshSend(object sender, RoutedEventArgs e) => RefreshSend();

    private void RefreshSend()
    {
        if (SendButton is not null)
            SendButton.IsEnabled = !sending && Peers.SelectedItem is BluetoothServer.Peer &&
                (attachment is not null || Message.Text.Length > 0);
    }

    private async void SendClick(object sender, RoutedEventArgs e)
    {
        if (sending || Peers.SelectedItem is not BluetoothServer.Peer peer)
            return;

        using var cancellation = new CancellationTokenSource();
        sendCancellation = cancellation;
        sending = true;
        RefreshSend();
        Status.Text = "";
        SendView.Begin(peer.Name);
        ComposeView.Visibility = Visibility.Collapsed;
        try
        {
            var transfer = attachment ?? new Transfer(TransferKind.Text, "", Encoding.UTF8.GetBytes(Message.Text));
            await server.SendAsync(peer, transfer, SendView, cancellation.Token);
            if (!exiting)
            {
                SendView.Complete();
                Status.Text = $"Delivered to {peer.Name}";
                await Task.Delay(900);
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
            }
        }
    }
}
