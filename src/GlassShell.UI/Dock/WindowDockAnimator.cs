using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassShell.Core.DockPhysics;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Dock;

/// <summary>
/// Zero-injection 120fps macOS Genie Effect (神奇效果) visual transition overlay.
/// Renders 28 horizontal CroppedBitmap slices smoothly curving along an S-shaped funnel into/out of the exact Dock icon.
/// </summary>
public static class WindowDockAnimator
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private sealed class GenieCanvasElement : FrameworkElement
    {
        private const int SliceCount = 28;
        private readonly CroppedBitmap[]? _slices;
        private readonly Brush _fallbackBrush;
        private readonly Rect _localWinRect;
        private readonly Point _localDockCenter;
        private readonly bool _isRestore;
        private readonly double _durationSec;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly Action _onCompleted;
        private double _progress;

        public GenieCanvasElement(
            BitmapSource? snapshot,
            Rect localWinRect,
            Point localDockCenter,
            bool isRestore,
            Action onCompleted)
        {
            _localWinRect = localWinRect;
            _localDockCenter = localDockCenter;
            _isRestore = isRestore;
            _durationSec = isRestore ? 0.27 : 0.30;
            _onCompleted = onCompleted;
            _progress = isRestore ? 1.0 : 0.0;
            IsHitTestVisible = false;

            var fb = new SolidColorBrush(Color.FromArgb(0xCC, 0x24, 0x2A, 0x3A));
            fb.Freeze();
            _fallbackBrush = fb;

            if (snapshot is not null && snapshot.PixelWidth >= 8 && snapshot.PixelHeight >= SliceCount)
            {
                _slices = new CroppedBitmap[SliceCount];
                int pw = snapshot.PixelWidth;
                int ph = snapshot.PixelHeight;
                for (int i = 0; i < SliceCount; i++)
                {
                    int y0 = (int)Math.Floor((double)i * ph / SliceCount);
                    int y1 = (int)Math.Ceiling((double)(i + 1) * ph / SliceCount);
                    int sh = Math.Max(1, Math.Min(ph - y0, y1 - y0));
                    var cb = new CroppedBitmap(snapshot, new Int32Rect(0, y0, pw, sh));
                    cb.Freeze();
                    _slices[i] = cb;
                }
            }

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
            Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
            Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            double raw = Math.Clamp(_sw.Elapsed.TotalSeconds / _durationSec, 0.0, 1.0);
            _progress = _isRestore ? (1.0 - raw) : raw;
            InvalidateVisual();

            if (raw >= 1.0)
            {
                CompositionTarget.Rendering -= OnFrame;
                _onCompleted();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var slices = GenieMeshCalculator.ComputeSlices(
                _localWinRect.Left,
                _localWinRect.Top,
                _localWinRect.Width,
                _localWinRect.Height,
                _localDockCenter.X,
                _localDockCenter.Y,
                _progress,
                SliceCount,
                dockMouthWidth: 36.0);

            double opacity = _isRestore
                ? Math.Clamp(1.0 - Math.Pow(1.0 - _progress, 4.0) * 0.15, 0.25, 0.96)
                : Math.Clamp(1.0 - Math.Pow(_progress, 4.5), 0.0, 0.96);

            dc.PushOpacity(opacity);
            for (int i = 0; i < slices.Count; i++)
            {
                var s = slices[i];
                var rect = new Rect(s.Left, s.Top, s.Width, s.Height);
                if (_slices is not null)
                {
                    dc.DrawImage(_slices[i], rect);
                }
                else
                {
                    dc.DrawRectangle(_fallbackBrush, null, rect);
                }
            }
            dc.Pop();
        }
    }

    public static void PlayMinimizeToDock(BitmapSource? snapshot, Rect windowRectDip, Point dockIconCenterDip)
    {
        if (windowRectDip.Width < 40 || windowRectDip.Height < 40) return;
        PlayGenieTransition(snapshot, windowRectDip, dockIconCenterDip, isRestore: false);
    }

    public static void PlayRestoreFromDock(BitmapSource? snapshot, Point dockIconCenterDip, Rect targetWindowRectDip)
    {
        if (targetWindowRectDip.Width < 40 || targetWindowRectDip.Height < 40) return;
        PlayGenieTransition(snapshot, targetWindowRectDip, dockIconCenterDip, isRestore: true);
    }

    private static void PlayGenieTransition(BitmapSource? snapshot, Rect fullRectDip, Point dockCenterDip, bool isRestore)
    {
        double minX = Math.Min(fullRectDip.Left, dockCenterDip.X - 40);
        double minY = Math.Min(fullRectDip.Top, dockCenterDip.Y - 40);
        double maxX = Math.Max(fullRectDip.Right, dockCenterDip.X + 40);
        double maxY = Math.Max(fullRectDip.Bottom, dockCenterDip.Y + 24);

        double boundsW = Math.Max(64, maxX - minX);
        double boundsH = Math.Max(64, maxY - minY);

        var localWin = new Rect(fullRectDip.Left - minX, fullRectDip.Top - minY, fullRectDip.Width, fullRectDip.Height);
        var localDock = new Point(dockCenterDip.X - minX, dockCenterDip.Y - minY);

        Window? overlay = null;
        var genieElement = new GenieCanvasElement(
            snapshot,
            localWin,
            localDock,
            isRestore,
            onCompleted: () =>
            {
                try { overlay?.Close(); } catch { }
            });

        overlay = new Window
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
            Content = genieElement
        };

        overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(overlay).Handle;
            WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd);
            int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
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

        if (GetWindowRect(hwnd, out RECT rc) && rc.Left > -10000)
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
