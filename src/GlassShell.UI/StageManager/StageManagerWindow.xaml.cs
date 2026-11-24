using System;
using System.Collections.Generic;
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

public partial class StageManagerWindow : Window
{
    private static readonly DropShadowEffect CardShadow;
    private static readonly DropShadowEffect LabelShadow;

    static StageManagerWindow()
    {
        CardShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 14,
            ShadowDepth = 4,
            Opacity = 0.42
        };
        CardShadow.Freeze();

        LabelShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 5,
            ShadowDepth = 1,
            Opacity = 0.72
        };
        LabelShadow.Freeze();
    }

    private readonly IconExtractor _iconExtractor = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private MonitorContext _monitor;
    private bool _isDarkMode = true;

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
    }

    public event Action<nint, uint>? AppGroupSelected;

    public void ApplyTheme(bool isDarkMode) => _isDarkMode = isDarkMode;

    public void PositionOnLeftEdge(MonitorContext monitor)
    {
        _monitor = monitor;
        Left = (monitor.Bounds.Left / monitor.DpiScale) + 12;
        double estHeight = ActualHeight > 40 ? ActualHeight : 360;
        Top = (monitor.Bounds.Top / monitor.DpiScale) + Math.Max(46, (monitor.HeightDip - estHeight) * 0.46);
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
        var scale = new ScaleTransform(1.0, 1.0);
        var skew = new SkewTransform(0, -3.5);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(skew);
        transformGroup.Children.Add(scale);

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
                    _isDarkMode ? Color.FromArgb(0xCC, 0x26, 0x2D, 0x3E) : Color.FromArgb(0xD8, 0xE6, 0xEC, 0xF7),
                    _isDarkMode ? Color.FromArgb(0xCC, 0x18, 0x1D, 0x29) : Color.FromArgb(0xD8, 0xCF, 0xDA, 0xEB),
                    90),
                Children =
                {
                    new TextBlock
                    {
                        Text = group.AppName,
                        FontSize = 11.5,
                        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                        Foreground = new SolidColorBrush(_isDarkMode ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x18, 0x1A, 0x22)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(8)
                    }
                }
            };
        }

        var thumbBorder = new Border
        {
            Width = 144,
            Height = 88,
            CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Effect = CardShadow,
            Child = previewContent
        };

        var cardGrid = new Grid
        {
            Width = 152,
            Height = 96,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = transformGroup
        };
        cardGrid.Children.Add(thumbBorder);

        var badgeBmp = GetAppIcon(group.ExecutablePath);
        if (badgeBmp is not null)
        {
            var badgeImg = new Image
            {
                Source = badgeBmp,
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2)
            };
            RenderOptions.SetBitmapScalingMode(badgeImg, BitmapScalingMode.HighQuality);
            cardGrid.Children.Add(badgeImg);
        }

        var label = new TextBlock
        {
            Text = group.WindowCount > 1 ? $"{group.AppName} ({group.WindowCount})" : group.AppName,
            FontSize = 11,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 146,
            Margin = new Thickness(0, 3, 0, 0),
            Effect = LabelShadow
        };

        var container = new StackPanel
        {
            Margin = new Thickness(0, 6, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(group.WindowTitle) ? group.AppName : group.WindowTitle,
            Children = { cardGrid, label }
        };

        container.MouseEnter += (_, _) =>
        {
            var dur = TimeSpan.FromMilliseconds(130);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, dur));
            skew.BeginAnimation(SkewTransform.AngleYProperty, new DoubleAnimation(0.0, dur));
        };

        container.MouseLeave += (_, _) =>
        {
            var dur = TimeSpan.FromMilliseconds(140);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
            skew.BeginAnimation(SkewTransform.AngleYProperty, new DoubleAnimation(-3.5, dur));
        };

        container.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AppGroupSelected?.Invoke(group.RepresentativeHwnd, group.ProcessId);
        };

        return container;
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
}
