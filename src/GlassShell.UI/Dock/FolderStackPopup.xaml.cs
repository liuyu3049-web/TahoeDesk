using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Dock;

public partial class FolderStackPopup : Popup
{
    private readonly IconExtractor _iconExtractor = new();
    private readonly OutsideClickDismissHook _dismissHook;
    private string _currentFolderPath = string.Empty;
    private bool _isDarkMode = true;

    public FolderStackPopup()
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

        Resources["StackFgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x12, 0x13, 0x18));
        Resources["StackSubtleBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0x12, 0x13, 0x18));
        Resources["StackDividerBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        Resources["StackBtnBgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        Resources["StackHoverBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
    }

    public void ShowForFolder(UIElement anchor, string title, string folderPath)
    {
        _currentFolderPath = folderPath;
        FolderTitleText.Text = title;

        var items = FolderStackScanner.Scan(folderPath, maxItems: 20);
        ItemCountText.Text = $"{items.Count} 项";
        ItemsGrid.Children.Clear();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items)
        {
            ItemsGrid.Children.Add(BuildTile(item));
        }

        PlacementTarget = anchor;
        HorizontalOffset = -150;
        VerticalOffset = -10;
        IsOpen = true;
    }

    private Border BuildTile(FolderStackItem item)
    {
        UIElement icon = BuildFileIcon(item);
        var hoverBrush = Resources["StackHoverBrush"] as Brush ?? Brushes.Transparent;
        var fgBrush = Resources["StackFgBrush"] as Brush ?? Brushes.White;

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 11,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = fgBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 30,
            Margin = new Thickness(2, 4, 2, 0)
        });

        var border = new Border
        {
            Width = 74,
            Height = 78,
            Margin = new Thickness(2),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = item.Name,
            Child = panel
        };

        border.MouseEnter += (_, _) => border.Background = hoverBrush;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            IsOpen = false;
            LaunchTarget(item.FullPath);
        };

        return border;
    }

    private UIElement BuildFileIcon(FolderStackItem item)
    {
        var raw = _iconExtractor.ExtractHighResIcon(item.FullPath, 64);
        if (raw is not null)
        {
            var bmp = BitmapSource.Create(raw.Width, raw.Height, 96, 96, PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
            bmp.Freeze();
            var img = new Image
            {
                Source = bmp,
                Width = 34,
                Height = 34,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        return new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(item.IsDirectory ? Color.FromRgb(0x38, 0xB6, 0xFF) : (_isDarkMode ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x28, 0x00, 0x00, 0x00))),
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private void OnOpenInExplorerClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        if (!string.IsNullOrWhiteSpace(_currentFolderPath))
        {
            LaunchTarget(_currentFolderPath);
        }
    }

    private static void LaunchTarget(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
