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

    internal ReceivedPopup(Transfer transfer, Task<string>? automaticSave = null)
    {
        this.transfer = transfer;
        InitializeComponent();
        Closed += (_, _) => ReceivedFiles.Release(transfer, automaticSave);

        HeadingText.Text = transfer.Kind switch
        {
            TransferKind.Text => "Text received",
            TransferKind.Image => "Image received",
            _ => "File received"
        };
        Details.Text = transfer.Kind == TransferKind.Text ? transfer.Text : transfer.Name;
        if (transfer.Kind == TransferKind.Image)
        {
            try
            {
                using var stream = transfer.OpenRead();
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                if ((long)frame.PixelWidth * frame.PixelHeight > 32_000_000)
                    throw new InvalidDataException("This image is too large to preview.");
                image = new WriteableBitmap(frame);
                image.Freeze();
                Preview.Source = image;
                Preview.Visibility = Visibility.Visible;
                Details.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException
                or IOException or NotSupportedException or OutOfMemoryException or COMException)
            {
                Status.Text = "Preview unavailable. You can save the original.";
            }
        }

        CopyText.Text = image is null ? "Copy text" : "Copy image";
        AutomationProperties.SetName(CopyButton, CopyText.Text);
        if (image is null)
            Height = 248;
        CopyButton.Visibility = transfer.Kind == TransferKind.Text || image is not null ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Visibility = transfer.Kind == TransferKind.Text ? Visibility.Collapsed : Visibility.Visible;
        if (image is not null)
        {
            SaveButton.SetResourceReference(StyleProperty, "QuietButton");
            SaveButton.Margin = new Thickness(8, 0, 0, 0);
        }
        if (automaticSave is not null)
            Loaded += async (_, _) =>
            {
                if (!automaticSave.IsCompleted)
                    Status.Text = "Saving automatically…";
                string result = await automaticSave;
                if (IsVisible)
                    Status.Text = result;
            };
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
            Status.Text = "Saved";
        }
    });

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { Status.Text = error.Message; }
    }
}
