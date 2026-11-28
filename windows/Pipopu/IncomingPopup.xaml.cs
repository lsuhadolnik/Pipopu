using System.Windows;

namespace Pipopu;

internal partial class IncomingPopup : FlyoutWindow
{
    internal IncomingPopup() => InitializeComponent();

    internal void Update(TransferProgress progress)
    {
        FileName.Text = progress.Name;
        FileName.ToolTip = progress.Name;
        Stage.Text = progress.Stage;
        Stage.ToolTip = progress.Failed ? progress.Stage : null;
        Bar.Visibility = progress.Failed ? Visibility.Collapsed : Visibility.Visible;
        Bar.IsIndeterminate = progress.Bytes is null;
        Details.Text = "";
        if (progress.Bytes is not long bytes)
            return;
        double percent = Math.Clamp(100d * bytes / progress.Total, 0, 100);
        Bar.Value = percent;
        Details.Text = $"{percent:0}%  ·  {TransferProgress.FormatBytes(bytes)} / {TransferProgress.FormatBytes(progress.Total)}";
        if (progress.BytesPerSecond is double speed)
            Details.Text += $"  ·  {TransferProgress.FormatBytes(speed)}/s";
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
