using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using GlassShell.Core.Config;
using GlassShell.Core.Shell;
using GlassShell.Core.Tray;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Tray;

public partial class TrayOverflowPopup : Popup
{
    private readonly OutsideClickDismissHook _dismissHook;
    private bool _isDarkMode = true;

    public TrayOverflowPopup()
    {
        InitializeComponent();
        _dismissHook = new OutsideClickDismissHook(
            CollectOpenHwnds,
            () => Dispatcher.BeginInvoke(() => IsOpen = false));

        Opened += (_, _) =>
        {
            WindowInteropHelperEx.ExcludeVisualFromCapture(Child);
            _dismissHook.Start();
        };
        Closed += (_, _) => _dismissHook.Stop();
    }

    public nint OwnerAnchorHwnd { get; set; }

    public event Action<TrayDisplayItem, TrayMouseAction, ScreenRect, Point>? TrayIconInvoked;
    public event Action<TrayDisplayItem>? TrayIconPinToggled;
    public event Action? NativeTaskbarPeekRequested;

    private IEnumerable<nint> CollectOpenHwnds()
    {
        if (OwnerAnchorHwnd != 0) yield return OwnerAnchorHwnd;
        if (Child is not null && PresentationSource.FromVisual(Child) is HwndSource src && src.Handle != 0)
        {
            yield return src.Handle;
        }
    }

    public void ConfigureVisuals(DxgiBackdropSampler? sampler, GlassOpacityPreset preset, bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
        GlassBackdrop.OpacityPreset = preset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null)
        {
            GlassBackdrop.AttachSampler(sampler);
        }

        Resources["TrayFgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x12, 0x13, 0x18));
        Resources["TraySubtleBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xA0, 0x12, 0x13, 0x18));
        Resources["TrayDividerBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        Resources["TrayBtnBgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        Resources["TrayTileHoverBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
    }

    public void UpdateNativePeekLabel(bool isPeekActive)
    {
        NativeTaskbarPeekButton.Content = isPeekActive ? "收起原生任务栏" : "临时展开原生任务栏";
    }

    public void Populate(IReadOnlyList<TrayDisplayItem> overflowItems)
    {
        IconsWrapPanel.Children.Clear();
        EmptyHintText.Visibility = overflowItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in overflowItems)
        {
            IconsWrapPanel.Children.Add(BuildTile(item));
        }
    }

    private Border BuildTile(TrayDisplayItem item)
    {
        UIElement iconEl = item.IconBitmap is not null
            ? CreateIconImage(item.IconBitmap, 18)
            : new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(_isDarkMode ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x18, 0x1A, 0x22))
            };

        var hoverBrush = Resources["TrayTileHoverBrush"] as Brush ?? Brushes.Transparent;
        var tile = new Border
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(item.Record.Tooltip) ? "托盘应用" : item.Record.Tooltip,
            Child = iconEl
        };

        tile.MouseEnter += (_, _) => tile.Background = hoverBrush;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;

        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                TrayIconPinToggled?.Invoke(item);
                return;
            }

            var (rect, pt) = CaptureScreenMetrics(tile);
            var action = e.ClickCount >= 2 ? TrayMouseAction.LeftDoubleClick : TrayMouseAction.LeftClick;
            IsOpen = false;
            TrayIconInvoked?.Invoke(item, action, rect, pt);
        };

        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var (rect, pt) = CaptureScreenMetrics(tile);
            IsOpen = false;
            TrayIconInvoked?.Invoke(item, TrayMouseAction.RightClick, rect, pt);
        };

        tile.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                var (rect, pt) = CaptureScreenMetrics(tile);
                IsOpen = false;
                TrayIconInvoked?.Invoke(item, TrayMouseAction.MiddleClick, rect, pt);
            }
        };

        return tile;
    }

    private static Image CreateIconImage(System.Windows.Media.Imaging.BitmapSource source, double size)
    {
        var img = new Image
        {
            Source = source,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private static (ScreenRect Rect, Point AnchorPoint) CaptureScreenMetrics(FrameworkElement element)
    {
        try
        {
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            var rect = new ScreenRect(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X),
                (int)Math.Round(bottomRight.Y));
            var pt = new Point((topLeft.X + bottomRight.X) * 0.5, bottomRight.Y);
            return (rect, pt);
        }
        catch
        {
            return (default, new Point(0, 0));
        }
    }

    private void OnNativeTaskbarPeekClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        NativeTaskbarPeekRequested?.Invoke();
    }
}
