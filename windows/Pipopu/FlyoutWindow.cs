using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pipopu;

public class FlyoutWindow : Window
{
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDwmCompositionChanged = 0x031E;
    private const int DwmImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int DwmRound = 2;
    private const int DwmNoBackdrop = 1;
    private const int DwmDesktopAcrylic = 3;
    private const uint NoSizeNoZOrderNoActivate = 0x0015;
    private const double Inset = 20;
    private const double EntranceDistance = 18;
    private const double EntranceMilliseconds = 210;

    private readonly DispatcherTimer entrance = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(15)
    };
    private readonly Stopwatch elapsed = new();
    private HwndSource? source;

    protected FlyoutWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SetResourceReference(IconProperty, "AppIcon");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        entrance.Tick += (_, _) =>
        {
            double progress = Math.Min(1, elapsed.Elapsed.TotalMilliseconds / EntranceMilliseconds);
            MoveToCorner(EntranceDistance * Math.Pow(1 - progress, 3));
            if (progress == 1)
                entrance.Stop();
        };
    }

    internal void Present(bool animate = false)
    {
        entrance.Stop();
        WindowState = WindowState.Normal;
        if (source is null)
        {
            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - Width - Inset);
            Top = Math.Max(work.Top, work.Bottom - Height - Inset);
            _ = new WindowInteropHelper(this).EnsureHandle();
        }

        bool slide = animate && !IsVisible && SystemParameters.ClientAreaAnimation;
        MoveToCorner(slide ? EntranceDistance : 0);
        Show();
        Activate();
        if (slide)
        {
            elapsed.Restart();
            entrance.Start();
        }
    }

    private void MoveToCorner(double offset = 0)
    {
        if (source?.CompositionTarget is not { } target ||
            Forms.Screen.PrimaryScreen is not { } screen ||
            !GetWindowRect(source.Handle, out var bounds))
            return;

        var work = screen.WorkingArea;
        Matrix scale = target.TransformToDevice;
        int left = Math.Max(work.Left, work.Right - (bounds.Right - bounds.Left) - (int)Math.Round(Inset * scale.M11));
        int top = Math.Max(work.Top, work.Bottom - (bounds.Bottom - bounds.Top) - (int)Math.Round(Inset * scale.M22));
        SetWindowPos(source.Handle, IntPtr.Zero, left, top + (int)Math.Round(offset * scale.M22), 0, 0, NoSizeNoZOrderNoActivate);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source.AddHook(WindowMessage);
        ApplySurface();
    }

    private void ApplySurface()
    {
        if (source?.CompositionTarget is not { } target)
            return;

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush foreground &&
                foreground.Color.R > 127 ? 1 : 0;
            DwmSetWindowAttribute(source.Handle, DwmImmersiveDarkMode, dark, sizeof(int));
            DwmSetWindowAttribute(source.Handle, DwmWindowCornerPreference, DwmRound, sizeof(int));
        }

        bool acrylic = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int backdrop = SystemParameters.HighContrast ? DwmNoBackdrop : DwmDesktopAcrylic;
            var margins = new Margins(backdrop == DwmDesktopAcrylic ? -1 : 0);
            acrylic = DwmSetWindowAttribute(source.Handle, DwmSystemBackdropType, backdrop, sizeof(int)) >= 0 &&
                DwmExtendFrameIntoClientArea(source.Handle, in margins) >= 0 && backdrop == DwmDesktopAcrylic;
        }

        target.BackgroundColor = acrylic ? Colors.Transparent : Colors.Black;
        if (acrylic)
            Background = Brushes.Transparent;
        else
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is WmDisplayChange or WmSettingChange or WmDwmCompositionChanged)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (message is WmSettingChange or WmDwmCompositionChanged)
                    ApplySurface();
                if (IsVisible && !entrance.IsEnabled)
                    MoveToCorner();
            });
        }
        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsVisible && !entrance.IsEnabled)
                MoveToCorner();
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        entrance.Stop();
        source?.RemoveHook(WindowMessage);
        source = null;
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Margins(int width)
    {
        private readonly int left = width;
        private readonly int right = width;
        private readonly int top = width;
        private readonly int bottom = width;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, in Margins margins);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
