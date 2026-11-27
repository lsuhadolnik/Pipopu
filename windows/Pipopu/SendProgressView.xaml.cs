using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Pipopu;

internal partial class SendProgressView : UserControl, IProgress<int>
{
    private int lastPercentage;
    internal event Action? CancelRequested;

    public SendProgressView() => InitializeComponent();

    internal void Begin(string deviceName)
    {
        lastPercentage = -1;
        Gauge.BeginAnimation(TransferArc.ProgressProperty, null);
        Gauge.Progress = 0;
        Glyph.Text = "\uE8EA";
        HeadingText.Text = "Sending";
        DeviceText.Text = deviceName;
        Status.Text = "Connecting over Bluetooth…";
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        Visibility = Visibility.Visible;
    }

    public void Report(int percentage)
    {
        if (percentage == lastPercentage)
            return;
        lastPercentage = percentage;
        if (percentage == 100)
            CancelButton.IsEnabled = false;
        AnimateTo(Math.Min(96, percentage) / 100d);
        Status.Text = percentage == 100 ? "Waiting for delivery confirmation…" : $"{percentage}% sent";
    }

    internal void Complete()
    {
        HeadingText.Text = "Delivered";
        Glyph.Text = "\uE73E";
        Status.Text = "The device confirmed receipt.";
        CancelButton.Visibility = Visibility.Hidden;
        AnimateTo(1);
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        Status.Text = "Cancelling…";
        CancelRequested?.Invoke();
    }

    private void AnimateTo(double value)
    {
        double current = Gauge.Progress;
        Gauge.Progress = value;
        var animation = SystemParameters.ClientAreaAnimation && IsVisible
            ? new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(145))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            }
            : null;
        Gauge.BeginAnimation(TransferArc.ProgressProperty, animation);
    }
}

public sealed class TransferArc : Shape
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(TransferArc),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            const double startAngle = 130;
            const double sweep = 280;
            double degrees = sweep * Progress;
            if (degrees <= 0)
                return Geometry.Empty;

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(PointOnCircle(startAngle), isFilled: false, isClosed: false);
                context.ArcTo(PointOnCircle(startAngle + degrees), new Size(67, 67), 0, degrees > 180,
                    SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            return geometry;
        }
    }

    private static Point PointOnCircle(double degrees)
    {
        double angle = degrees * Math.PI / 180;
        return new Point(77 + 67 * Math.Cos(angle), 77 + 67 * Math.Sin(angle));
    }
}
