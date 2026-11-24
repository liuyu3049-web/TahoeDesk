using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GlassShell.Core.Discovery;
using GlassShell.Core.Shell;
using GlassShell.UI.Glass;

namespace GlassShell.UI.StageManager;

public sealed record StageGroupItem(
    nint RepresentativeHwnd,
    uint ProcessId,
    string AppName,
    string WindowTitle,
    string ExecutablePath,
    int WindowCount,
    BitmapSource? Snapshot);

/// <summary>
/// Authentic macOS Stage Manager left-edge thumbnail strip:
/// - Fanned perspective mini-window stacks with crisp corner app icon badges (no text clutter)
/// - Auto-slides off left edge when active window overlaps left 185 DIP, glides smoothly back when pointer touches left edge
/// </summary>
public partial class StageManagerWindow : Window
{
    private static readonly DropShadowEffect CardShadow;

    static StageManagerWindow()
    {
        CardShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 18,
            ShadowDepth = 5,
            Opacity = 0.46
        };
        CardShadow.Freeze();
    }

    private readonly IconExtractor _iconExtractor = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private MonitorContext _monitor;
    private bool _isDarkMode = true;
    private bool _activeWindowOverlapsLeftEdge;
    private bool _isSlidOut;

    public StageManagerWindow(MonitorContext monitor, bool isDarkMode)
    {
        InitializeComponent();
        _monitor = monitor;
        _isDarkMode = isDarkMode;
        PositionOnLeftEdge(monitor);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd);
        };

        Loaded += (_, _) => CompositionTarget.Rendering += OnEdgeTrackFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnEdgeTrackFrame;
    }

    public event Action<nint, uint>? AppGroupSelected;

    public void ApplyTheme(bool isDarkMode) => _isDarkMode = isDarkMode;

    public void SetLeftEdgeOverlapped(bool overlapped) => _activeWindowOverlapsLeftEdge = overlapped;

    public void PositionOnLeftEdge(MonitorContext monitor)
    {
        _monitor = monitor;
        Left = (monitor.Bounds.Left / monitor.DpiScale) + 6;
        double estHeight = ActualHeight > 40 ? ActualHeight : 380;
        Top = (monitor.Bounds.Top / monitor.DpiScale) + Math.Max(46, (monitor.HeightDip - estHeight) * 0.45);
    }

    private void OnEdgeTrackFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        bool shouldHide = false;

        if (_activeWindowOverlapsLeftEdge && GetCursorPos(out POINT pt))
        {
            bool onMonitorY = pt.Y >= _monitor.Bounds.Top && pt.Y <= _monitor.Bounds.Bottom;
            bool touchingLeftEdge = onMonitorY && pt.X >= _monitor.Bounds.Left && pt.X <= _monitor.Bounds.Left + 8;
            bool hoveringStrip = !_isSlidOut && onMonitorY && pt.X >= _monitor.Bounds.Left && pt.X <= _monitor.Bounds.Left + (int)(192 * _monitor.DpiScale);
            shouldHide = !(touchingLeftEdge || hoveringStrip);
        }

        if (shouldHide != _isSlidOut)
        {
            _isSlidOut = shouldHide;
            var anim = new DoubleAnimation(shouldHide ? -204.0 : 0.0, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            SlideTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    public void UpdateGroups(IReadOnlyList<StageGroupItem> groups)
    {
        CardsPanel.Children.Clear();
        foreach (var g in groups)
        {
            CardsPanel.Children.Add(BuildStageCard(g));
        }

        if (IsVisible)
        {
            Dispatcher.BeginInvoke(() => PositionOnLeftEdge(_monitor));
        }
    }

    private UIElement BuildStageCard(StageGroupItem group)
    {
        var scale = new ScaleTransform(0.95, 0.98);
        var skew = new SkewTransform(0, -6.5);
        var slide = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(skew);
        transformGroup.Children.Add(scale);
        transformGroup.Children.Add(slide);

        UIElement previewContent;
        if (group.Snapshot is not null)
        {
            var img = new Image
            {
                Source = group.Snapshot,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
            previewContent = img;
        }
        else
        {
            previewContent = new Grid
            {
                Background = new LinearGradientBrush(
                    _isDarkMode ? Color.FromArgb(0xDC, 0x26, 0x2D, 0x3F) : Color.FromArgb(0xE6, 0xEC, 0xF1, 0xFA),
                    _isDarkMode ? Color.FromArgb(0xDC, 0x17, 0x1C, 0x28) : Color.FromArgb(0xE6, 0xD6, 0xE0, 0xF0),
                    90)
            };
        }

        var cardGrid = new Grid
        {
            Width = 152,
            Height = 96,
            Margin = new Thickness(0, 8, 0, 8),
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(group.WindowTitle) ? group.AppName : $"{group.AppName} — {group.WindowTitle}",
            RenderTransformOrigin = new Point(0.15, 0.5),
            RenderTransform = transformGroup
        };

        // Authentic macOS multi-window fanned card peeking behind when app has 2+ windows
        if (group.WindowCount > 1)
        {
            var backCard = new Border
            {
                Width = 134,
                Height = 82,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(_isDarkMode ? Color.FromArgb(0x9E, 0x2B, 0x32, 0x45) : Color.FromArgb(0xB8, 0xF0, 0xF4, 0xFC)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0.9),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(3.2)
            };
            cardGrid.Children.Add(backCard);
        }

        var thumbBorder = new Border
        {
            Width = 138,
            Height = 86,
            CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 4),
            ClipToBounds = true,
            Effect = CardShadow,
            Child = previewContent
        };
        cardGrid.Children.Add(thumbBorder);

        // macOS Stage Manager crisp overlapping app icon badge at bottom-center/left (NO cluttered text underneath!)
        var badgeBmp = GetAppIcon(group.ExecutablePath);
        if (badgeBmp is not null)
        {
            var badgeImg = new Image
            {
                Source = badgeBmp,
                Width = 34,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 0, -2)
            };
            RenderOptions.SetBitmapScalingMode(badgeImg, BitmapScalingMode.HighQuality);
            cardGrid.Children.Add(badgeImg);
        }

        cardGrid.MouseEnter += (_, _) =>
        {
            var dur = TimeSpan.FromMilliseconds(140);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, dur) { EasingFunction = ease });
            skew.BeginAnimation(SkewTransform.AngleYProperty, new DoubleAnimation(0.0, dur) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10.0, dur) { EasingFunction = ease });
        };

        cardGrid.MouseLeave += (_, _) =>
        {
            var dur = TimeSpan.FromMilliseconds(150);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, dur) { EasingFunction = ease });
            skew.BeginAnimation(SkewTransform.AngleYProperty, new DoubleAnimation(-6.5, dur) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, dur) { EasingFunction = ease });
        };

        cardGrid.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppGroupSelected?.Invoke(group.RepresentativeHwnd, group.ProcessId);
        };

        return cardGrid;
    }

    private BitmapSource? GetAppIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (_iconCache.TryGetValue(exePath, out var cached)) return cached;

        BitmapSource? bmp = null;
        var raw = _iconExtractor.ExtractHighResIcon(exePath, 64);
        if (raw is not null)
        {
            bmp = BitmapSource.Create(raw.Width, raw.Height, 96, 96, PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
            bmp.Freeze();
        }
        _iconCache[exePath] = bmp;
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
