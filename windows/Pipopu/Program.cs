using System.Drawing.Imaging;
using System.Text;

namespace Pipopu;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, "Local\\Pipopu.Tray", out bool firstInstance);
        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        using var app = new TrayApp();
        Application.Run(app);
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly BluetoothServer server = new();
    private readonly Composer composer;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu;
    private ReceivedPopup? popup;

    internal TrayApp()
    {
        composer = new Composer(server);
        _ = composer.Handle;
        menu = new ContextMenuStrip();
        menu.Items.Add("Open Pipopu", null, (_, _) => ShowComposer());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "Pipopu",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) ShowComposer(); };

        server.PeersChanged += () => OnUI(composer.RefreshPeers);
        server.StatusChanged += message => OnUI(() => composer.SetBluetoothStatus(message));
        server.TransferReceived += transfer => OnUI(() => ShowReceived(transfer));

        ShowComposer();
    }

    private void OnUI(Action action)
    {
        if (!composer.IsDisposed && composer.IsHandleCreated)
        {
            try { composer.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
    }

    private void ShowComposer()
    {
        composer.Show();
        composer.WindowState = FormWindowState.Normal;
        composer.Activate();
    }

    private void ShowReceived(Transfer transfer)
    {
        popup?.Close();
        popup = new ReceivedPopup(transfer);
        popup.Show();
        popup.Activate();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        server.Dispose();
        popup?.Close();
        composer.Dispose();
        tray.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class Composer : Form
{
    private readonly BluetoothServer server;
    private readonly TextBox text = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Paste or type text" };
    private readonly ListBox peers = new() { Dock = DockStyle.Fill };
    private readonly Button send = new() { Text = "Send", AutoSize = true, Enabled = false };
    private readonly Button clear = new() { Text = "Remove attachment", AutoSize = true, Visible = false };
    private readonly Label attachmentLabel = new() { AutoSize = true, Text = "Drop an image or file here (up to 3 MB)." };
    private readonly Label bluetoothStatus = new() { AutoSize = true, Text = "Starting Bluetooth…" };
    private readonly Label status = new() { AutoSize = true };
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Fill, Visible = false };
    private Transfer? attachment;
    private bool sending;

    internal Composer(BluetoothServer server)
    {
        this.server = server;
        Text = "Pipopu";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 480);
        MinimumSize = new Size(400, 440);
        Icon = SystemIcons.Information;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var paste = new Button { Text = "Paste", AutoSize = true };
        var browse = new Button { Text = "Choose file…", AutoSize = true };
        buttons.Controls.AddRange([paste, browse, clear]);
        layout.Controls.Add(bluetoothStatus, 0, 0);
        layout.Controls.Add(text, 0, 1);
        layout.Controls.Add(attachmentLabel, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(peers, 0, 4);
        layout.Controls.Add(new Label { Text = "Open Pipopu on the iPhone or iPad to make it visible.", AutoSize = true }, 0, 5);
        layout.Controls.Add(send, 0, 6);
        layout.Controls.Add(progressBar, 0, 7);
        layout.Controls.Add(status, 0, 8);
        Controls.Add(layout);

        paste.Click += (_, _) => SetPayloadFromClipboard();
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "Choose a file to send" };
            if (picker.ShowDialog(this) == DialogResult.OK)
                TrySetPayload(() => AttachFile(picker.FileName));
        };
        clear.Click += (_, _) => SetAttachment(null);
        send.Click += async (_, _) => await SendAsync();
        text.TextChanged += (_, _) => RefreshSend();
        peers.SelectedIndexChanged += (_, _) => RefreshSend();

        EnableDrop(this);
        EnableDrop(layout);
        EnableDrop(text);
        EnableDrop(attachmentLabel);
        Shown += async (_, _) =>
        {
            try { await server.StartAsync(); }
            catch (Exception error) { SetBluetoothStatus(error.Message); }
        };
        FormClosing += (_, args) =>
        {
            if (args.CloseReason == CloseReason.UserClosing)
            {
                args.Cancel = true;
                Hide();
            }
        };
    }

    internal void SetBluetoothStatus(string message) => bluetoothStatus.Text = message;

    internal void RefreshPeers()
    {
        var selected = (peers.SelectedItem as BluetoothServer.Peer)?.Id;
        peers.BeginUpdate();
        peers.Items.Clear();
        foreach (var peer in server.GetPeers())
        {
            var index = peers.Items.Add(peer);
            if (peer.Id == selected)
                peers.SelectedIndex = index;
        }
        if (peers.SelectedIndex < 0 && peers.Items.Count == 1)
            peers.SelectedIndex = 0;
        peers.EndUpdate();
        RefreshSend();
    }

    private void SetPayloadFromClipboard() => TrySetPayload(() =>
    {
        if (Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            if (image is not null)
                AttachImage(image);
        }
        else if (Clipboard.ContainsText())
        {
            SetAttachment(null);
            text.Text = Clipboard.GetText();
        }
        else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: 1 } files)
        {
            AttachFile(files[0]!);
        }
        else
        {
            status.Text = "Copy text, an image, or a single file first.";
        }
    });

    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, args) =>
        {
            if (args.Data?.GetDataPresent(DataFormats.FileDrop) == true ||
                args.Data?.GetDataPresent(DataFormats.Bitmap) == true)
                args.Effect = DragDropEffects.Copy;
        };
        control.DragDrop += (_, args) => TrySetPayload(() =>
        {
            if (args.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
                AttachFile(files[0]);
            else if (args.Data?.GetData(DataFormats.Bitmap) is Image image)
                AttachImage(image);
            else
                throw new InvalidDataException("Drop a single image or file.");
        });
    }

    private void TrySetPayload(Action action)
    {
        try
        {
            status.Text = "";
            action();
        }
        catch (Exception error)
        {
            status.Text = error.Message;
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

    private void AttachImage(Image image)
    {
        using var bytes = new MemoryStream();
        image.Save(bytes, ImageFormat.Png);
        if (bytes.Length > Transfer.Limit)
            throw new InvalidDataException("The image exceeds the 3 MB limit.");
        SetAttachment(new(TransferKind.Image, "Clipboard image.png", bytes.ToArray()));
    }

    private void SetAttachment(Transfer? value)
    {
        attachment = value;
        text.Enabled = value is null;
        attachmentLabel.Text = value is null
            ? "Drop an image or file here (up to 3 MB)."
            : $"{value.Name} ({value.Data.Length:N0} bytes)";
        clear.Visible = value is not null;
        RefreshSend();
    }

    private void RefreshSend() => send.Enabled =
        !sending && peers.SelectedItem is BluetoothServer.Peer && (attachment is not null || text.TextLength != 0);

    private async Task SendAsync()
    {
        if (sending || peers.SelectedItem is not BluetoothServer.Peer peer)
            return;
        sending = true;
        RefreshSend();
        status.Text = $"Sending to {peer.Name}…";
        progressBar.Value = 0;
        progressBar.Visible = true;
        try
        {
            var transfer = attachment ?? new Transfer(TransferKind.Text, "", Encoding.UTF8.GetBytes(text.Text));
            int lastPercentage = -1;
            var progress = new DirectProgress(value =>
            {
                if (value != lastPercentage)
                {
                    lastPercentage = value;
                    progressBar.Value = value;
                }
            });
            await server.SendAsync(peer, transfer, progress);
            status.Text = $"Delivered to {peer.Name}";
        }
        catch (Exception error)
        {
            status.Text = error is OperationCanceledException or TimeoutException
                ? "The device stopped responding. Keep Pipopu open and try again."
                : error.Message;
        }
        finally
        {
            progressBar.Visible = false;
            sending = false;
            RefreshSend();
        }
    }

    private sealed class DirectProgress(Action<int> update) : IProgress<int>
    {
        public void Report(int value) => update(value);
    }
}

internal sealed class ReceivedPopup : Form
{
    private Image? preview;
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 26 };

    internal ReceivedPopup(Transfer transfer)
    {
        Text = "Pipopu · Received";
        ClientSize = new Size(400, 265);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Icon = SystemIcons.Information;

        var details = new TextBox
        {
            Text = transfer.Kind == TransferKind.Text ? transfer.Text : transfer.Name,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill
        };
        Control content = details;
        if (transfer.Kind == TransferKind.Image)
        {
            try
            {
                using var stream = new MemoryStream(transfer.Data);
                using var original = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                if ((long)original.Width * original.Height > 32_000_000)
                    throw new InvalidDataException("This image is too large to preview.");
                preview = new Bitmap(original);
                content = new PictureBox { Dock = DockStyle.Fill, Image = preview, SizeMode = PictureBoxSizeMode.Zoom };
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
            {
                status.Text = "Image preview unavailable. Save the original below.";
            }
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
        if (transfer.Kind == TransferKind.Text || preview is not null)
        {
            var copy = new Button { Text = preview is null ? "Copy text" : "Copy image", AutoSize = true };
            copy.Click += (_, _) => Run(() =>
            {
                if (preview is not null) Clipboard.SetImage(preview);
                else if (transfer.Text.Length > 0) Clipboard.SetText(transfer.Text);
                else Clipboard.Clear();
                status.Text = "Copied";
            });
            buttons.Controls.Add(copy);
        }
        if (transfer.Kind != TransferKind.Text)
        {
            var save = new Button { Text = "Save as…", AutoSize = true };
            save.Click += (_, _) => Run(() =>
            {
                string name = Path.GetFileName(transfer.Name.Replace('/', '\\'));
                if (name is "" or "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    name = "Pipopu transfer";
                using var picker = new SaveFileDialog { FileName = name };
                if (picker.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllBytes(picker.FileName, transfer.Data);
                    status.Text = "Saved";
                }
            });
            buttons.Controls.Add(save);
        }
        var done = new Button { Text = "Close", AutoSize = true };
        done.Click += (_, _) => Close();
        buttons.Controls.Add(done);
        Controls.Add(content);
        Controls.Add(status);
        Controls.Add(buttons);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { status.Text = error.Message; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            preview?.Dispose();
        base.Dispose(disposing);
    }
}
