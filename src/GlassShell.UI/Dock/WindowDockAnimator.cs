using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Dock;

/// <summary>
/// Zero-injection macOS-style Scale-into-Dock & Scale-from-Dock visual transition overlay.
/// Renders a click-through transient snapshot that smoothly shrinks into (or expands from) the exact Dock icon.
/// </summary>
public static class WindowDockAnimator
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    public static void PlayMinimizeToDock(BitmapSource? snapshot, Rect windowRectDip, Point dockIconCenterDip)
    {
        if (windowRectDip.Width < 40 || windowRectDip.Height < 40) return;
        PlayScaleTransition(snapshot, windowRectDip, dockIconCenterDip, isRestore: false);
    }

    public static void PlayRestoreFromDock(BitmapSource? snapshot, Point dockIconCenterDip, Rect targetWindowRectDip)
    {
        if (targetWindowRectDip.Width < 40 || targetWindowRectDip.Height < 40) return;
        PlayScaleTransition(snapshot, targetWindowRectDip, dockIconCenterDip, isRestore: true);
    }

    private static void PlayScaleTransition(BitmapSource? snapshot, Rect fullRectDip, Point dockCenterDip, bool isRestore)
    {
        double minX = Math.Min(fullRectDip.Left, dockCenterDip.X - 36);
        double minY = Math.Min(fullRectDip.Top, dockCenterDip.Y - 36);
        double maxX = Math.Max(fullRectDip.Right, dockCenterDip.X + 36);
        double maxY = Math.Max(fullRectDip.Bottom, dockCenterDip.Y + 36);

        double boundsW = Math.Max(64, maxX - minX);
        double boundsH = Math.Max(64, maxY - minY);

        var scale = new ScaleTransform(isRestore ? 0.08 : 1.0, isRestore ? 0.08 : 1.0);
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);

        UIElement cardContent = snapshot is not null
            ? new Image
            {
                Source = snapshot,
                Stretch = Stretch.Fill
            }
            : new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xA8, 0x26, 0x2C, 0x3D))
            };

        if (cardContent is Image img)
        {
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
        }

        var card = new Border
        {
            Width = fullRectDip.Width,
            Height = fullRectDip.Height,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = group,
            Opacity = isRestore ? 0.25 : 0.94,
            Child = cardContent
        };

        double startX = fullRectDip.Left - minX;
        double startY = fullRectDip.Top - minY;
        double endX = (dockCenterDip.X - minX) - fullRectDip.Width * 0.5;
        double endY = (dockCenterDip.Y - minY) - fullRectDip.Height * 0.5;

        translate.X = isRestore ? endX : startX;
        translate.Y = isRestore ? endY : startY;

        var canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };
        canvas.Children.Add(card);

        var overlay = new Window
        {
            Left = minX,
            Top = minY,
            Width = boundsW,
            Height = boundsH,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Content = canvas
        };

        overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(overlay).Handle;
            WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd);
            int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
        };

        overlay.Loaded += (_, _) =>
        {
            var dur = TimeSpan.FromMilliseconds(isRestore ? 200 : 230);
            IEasingFunction ease = isRestore
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new PowerEase { Power = 2.2, EasingMode = EasingMode.EaseInOut };

            var animScale = new DoubleAnimation(isRestore ? 0.08 : 1.0, isRestore ? 1.0 : 0.08, dur) { EasingFunction = ease };
            var animX = new DoubleAnimation(isRestore ? endX : startX, isRestore ? startX : endX, dur) { EasingFunction = ease };
            var animY = new DoubleAnimation(isRestore ? endY : startY, isRestore ? startY : endY, dur) { EasingFunction = ease };
            var animOpacity = new DoubleAnimation(isRestore ? 0.35 : 0.94, isRestore ? 0.0 : 0.0, dur)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            animScale.Completed += (_, _) =>
            {
                try { overlay.Close(); } catch { }
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            translate.BeginAnimation(TranslateTransform.XProperty, animX);
            translate.BeginAnimation(TranslateTransform.YProperty, animY);
            card.BeginAnimation(UIElement.OpacityProperty, animOpacity);
        };

        try
        {
            overlay.Show();
        }
        catch { }
    }

    public static bool TryGetWindowRectDip(nint hwnd, double dpiScale, out Rect rectDip)
    {
        rectDip = default;
        if (hwnd == 0 || dpiScale <= 0) return false;

        if (IsIconic(hwnd))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                rectDip = new Rect(
                    wp.rcNormalPosition.Left / dpiScale,
                    wp.rcNormalPosition.Top / dpiScale,
                    Math.Max(100, (wp.rcNormalPosition.Right - wp.rcNormalPosition.Left) / dpiScale),
                    Math.Max(80, (wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top) / dpiScale));
                return true;
            }
            return false;
        }

        if (GetWindowRect(hwnd, out RECT rc))
        {
            rectDip = new Rect(
                rc.Left / dpiScale,
                rc.Top / dpiScale,
                Math.Max(100, (rc.Right - rc.Left) / dpiScale),
                Math.Max(80, (rc.Bottom - rc.Top) / dpiScale));
            return true;
        }
        return false;
    }

    public static bool IsWindowMinimized(nint hwnd) => hwnd != 0 && IsIconic(hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);
}
