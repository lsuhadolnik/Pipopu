using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Pipopu;

internal partial class ReceivedPopup : FlyoutWindow
{
    private readonly Transfer transfer;
    private readonly BitmapSource? image;
    private string? savedPath;
    private bool savedManually;

    internal ReceivedPopup(Transfer transfer, Task<ReceivedSaveResult>? automaticSave = null)
    {
        this.transfer = transfer;
        InitializeComponent();
        Closed += (_, _) => ReceivedFiles.Release(transfer, automaticSave);

        bool isImage = transfer.Kind == TransferKind.Image ||
            (transfer.Kind == TransferKind.File && Transfer.IsImageFileName(transfer.Name));
        HeadingText.Text = transfer.Kind == TransferKind.Text
            ? transfer.Secure ? "Secure text received" : "Text received"
            : isImage ? "Image received" : "File received";
        Details.Text = transfer.Secure ? "Use Copy secure text to put it on your clipboard." :
            transfer.Kind == TransferKind.Text ? transfer.Text : transfer.Name;
        if (isImage)
        {
            try
            {
                using var stream = transfer.OpenRead();
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                image = (long)frame.PixelWidth * frame.PixelHeight <= 32_000_000
                    ? new WriteableBitmap(frame) : DecodeLargeImage(transfer, frame.PixelWidth >= frame.PixelHeight);
                image.Freeze();
                Preview.Source = image;
                ImageName.Text = transfer.Name;
                ImageName.ToolTip = transfer.Name;
                ImageContent.Visibility = Visibility.Visible;
                Details.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException
                or IOException or NotSupportedException or OutOfMemoryException or COMException)
            {
                Details.Text = transfer.Name + "\nPreview unavailable.";
            }
        }

        string copyLabel = transfer.Secure ? "Copy secure text" : image is null ? "Copy text" : "Copy image";
        CopyButton.ToolTip = copyLabel;
        AutomationProperties.SetName(CopyButton, copyLabel);
        if (image is null)
            Height = 248;
        CopyButton.Visibility = transfer.Kind == TransferKind.Text || image is not null ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = transfer.Kind == TransferKind.Text ? Visibility.Collapsed : Visibility.Visible;
        if (automaticSave is not null)
            Loaded += async (_, _) =>
            {
                if (!automaticSave.IsCompleted)
                    Status.Text = "Saving automatically…";
                var result = await automaticSave;
                if (IsVisible && !savedManually)
                    ShowSaveResult(result);
            };
    }

    private static BitmapImage DecodeLargeImage(Transfer transfer, bool landscape)
    {
        using var stream = transfer.OpenRead();
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = stream;
        if (landscape)
            preview.DecodePixelWidth = 3840;
        else
            preview.DecodePixelHeight = 3840;
        preview.EndInit();
        return preview;
    }

    private void ShowSaveResult(ReceivedSaveResult result)
    {
        savedPath = result.LocalPath;
        OpenFolderButton.Visibility = savedPath is null ? Visibility.Collapsed : Visibility.Visible;
        Status.Text = result.Message;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void CopyClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (image is not null)
            Clipboard.SetImage(image);
        else if (transfer.Text.Length > 0)
            Clipboard.SetText(transfer.Text);
        else
            Clipboard.Clear();
        Status.Text = "Copied";
    });

    private void SaveClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var picker = new SaveFileDialog { FileName = ReceivedFiles.SafeName(transfer.Name) };
        if (picker.ShowDialog(this) == true)
        {
            using var source = transfer.OpenRead();
            using var destination = File.Create(picker.FileName);
            source.CopyTo(destination);
            savedManually = true;
            ShowSaveResult(new("Saved.", picker.FileName));
        }
    });

    private void OpenFolderClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (savedPath is not { } path)
            return;
        if (File.Exists(path) && SHParseDisplayName(path, IntPtr.Zero, out var item, 0, out _) >= 0 && item != IntPtr.Zero)
        {
            try
            {
                if (SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0) >= 0)
                    return;
            }
            finally { Marshal.FreeCoTaskMem(item); }
        }
        string? folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
            throw new IOException("The saved folder is no longer available.");
        using var explorer = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    });

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { Status.Text = error.Message; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr item,
        uint attributes, out uint actualAttributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint childCount, IntPtr children, uint flags);
}
