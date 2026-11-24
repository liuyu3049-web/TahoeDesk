using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GlassShell.Core.Discovery;
using GlassShell.Core.Launchpad;
using GlassShell.Core.Search;
using GlassShell.Core.Shell;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Launchpad;

public partial class LaunchpadWindow : Window
{
    private static readonly DropShadowEffect LabelShadow;

    static LaunchpadWindow()
    {
        LabelShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 6,
            ShadowDepth = 1,
            Opacity = 0.65
        };
        LabelShadow.Freeze();
    }

    private readonly LaunchpadLayoutManager _layout = new();
    private readonly IconExtractor _iconExtractor = new();
    private readonly ConcurrentDictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _layoutPath;

    private int _currentPage;
    private string? _openFolderId;
    private string? _draggingEntryId;
    private Point _dragStart;
    private int _renderGeneration;

    public LaunchpadWindow(DxgiBackdropSampler sampler)
    {
        InitializeComponent();
        _ = sampler;

        _layoutPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassShell",
            "launchpad.json");
        _layout.Load(_layoutPath);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowInteropHelperEx.ExcludeFromCapture(hwnd);
        };
    }

    public void SyncApps(IReadOnlyList<InstalledAppEntry> apps)
    {
        _layout.SyncWithInstalledApps(apps);
        _currentPage = _layout.ClampPage(_currentPage);

        // Pre-warm wallpaper blur and app icons completely off the UI thread so Launchpad opens in <10ms!
        _ = Task.Run(() =>
        {
            BlurredWallpaperProvider.GetBlurredWallpaper();
            foreach (var app in apps.Take(70))
            {
                string? path = app.IconPath ?? app.LaunchTarget;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    PreloadIconBitmap(path);
                }
            }
        });

        if (IsVisible)
        {
            RenderCurrentView();
        }
    }

    public void ToggleOnMonitor(MonitorContext monitor, IReadOnlyList<InstalledAppEntry> apps)
    {
        if (IsVisible)
        {
            DismissSmooth();
            return;
        }

        _layout.SyncWithInstalledApps(apps);
        _currentPage = _layout.ClampPage(_currentPage);
        _openFolderId = null;
        FolderOverlay.Visibility = Visibility.Collapsed;

        WallpaperBlurImage.Source = BlurredWallpaperProvider.GetBlurredWallpaper();

        Left = monitor.Bounds.Left / monitor.DpiScale;
        Top = monitor.Bounds.Top / monitor.DpiScale;
        Width = monitor.WidthDip;
        Height = monitor.HeightDip;

        SearchBox.Text = string.Empty;
        SearchPlaceholder.Visibility = Visibility.Visible;
        RenderCurrentView();

        Show();
        Activate();
        SearchBox.Focus();
        AnimateEntrance();
    }

    public void DismissSmooth()
    {
        if (!IsVisible) return;
        FolderOverlay.Visibility = Visibility.Collapsed;
        _openFolderId = null;
        Hide();
    }

    private void AnimateEntrance()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);

        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.04, 1.0, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.04, 1.0, dur) { EasingFunction = ease });
    }

    private void RenderCurrentView()
    {
        int gen = ++_renderGeneration;
        AppsGrid.Children.Clear();
        PageDotsPanel.Children.Clear();

        string query = SearchBox.Text.Trim();
        IReadOnlyList<LaunchpadEntry> pageItems = string.IsNullOrEmpty(query)
            ? _layout.GetPage(_currentPage)
            : _layout.Filter(query).Take(LaunchpadLayoutManager.ItemsPerPage).ToList();

        var pendingAsyncLoads = new List<(string Path, Image Target)>();

        foreach (var entry in pageItems)
        {
            AppsGrid.Children.Add(BuildTile(entry, isInsideFolder: false, pendingAsyncLoads));
        }

        if (pendingAsyncLoads.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (var (path, target) in pendingAsyncLoads)
                {
                    if (_renderGeneration != gen) break;
                    var bmp = PreloadIconBitmap(path);
                    if (bmp is not null)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (_renderGeneration == gen) target.Source = bmp;
                        });
                    }
                }
            });
        }

        if (string.IsNullOrEmpty(query))
        {
            int count = _layout.PageCount;
            for (int p = 0; p < count; p++)
            {
                int pageIdx = p;
                var dot = new Ellipse
                {
                    Width = pageIdx == _currentPage ? 8.5 : 6.5,
                    Height = pageIdx == _currentPage ? 8.5 : 6.5,
                    Margin = new Thickness(5, 0, 5, 0),
                    Cursor = Cursors.Hand,
                    Fill = new SolidColorBrush(pageIdx == _currentPage
                        ? Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF))
                };
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _currentPage = pageIdx;
                    RenderCurrentView();
                };
                PageDotsPanel.Children.Add(dot);
            }
        }
    }

    private UIElement BuildTile(LaunchpadEntry entry, bool isInsideFolder, List<(string Path, Image Target)>? asyncLoads)
    {
        var iconScale = new ScaleTransform(1.0, 1.0);
        UIElement iconVisual = entry.IsFolder
            ? BuildFolderTileVisual(entry, asyncLoads)
            : BuildAppIconVisual(entry.IconPath ?? entry.LaunchTarget, size: 70, asyncLoads);

        var iconHost = new Grid
        {
            Width = 74,
            Height = 74,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = iconScale,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        iconHost.Children.Add(iconVisual);

        var label = new TextBlock
        {
            Text = entry.Title,
            FontSize = 12.5,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 112,
            Margin = new Thickness(0, 8, 0, 0),
            Effect = LabelShadow
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(iconHost);
        stack.Children.Add(label);

        var tile = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            AllowDrop = !isInsideFolder,
            Child = stack
        };

        // Authentic macOS subtle icon lift on hover (no ugly rectangular highlight box!)
        tile.MouseEnter += (_, _) =>
        {
            var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(110));
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };
        tile.MouseLeave += (_, _) =>
        {
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120));
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };

        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _dragStart = e.GetPosition(this);
            _draggingEntryId = entry.Id;
        };

        tile.MouseMove += (_, e) =>
        {
            if (!isInsideFolder && e.LeftButton == MouseButtonState.Pressed && _draggingEntryId == entry.Id)
            {
                var pos = e.GetPosition(this);
                if ((pos - _dragStart).Length > 10)
                {
                    _draggingEntryId = null;
                    DragDrop.DoDragDrop(tile, new DataObject("GlassShell.LaunchpadId", entry.Id), DragDropEffects.Move);
                }
            }
        };

        tile.Drop += (_, e) =>
        {
            if (isInsideFolder || !e.Data.GetDataPresent("GlassShell.LaunchpadId")) return;
            e.Handled = true;
            string sourceId = (string)e.Data.GetData("GlassShell.LaunchpadId");
            if (string.Equals(sourceId, entry.Id, StringComparison.OrdinalIgnoreCase)) return;

            bool shiftReorder = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool changed = shiftReorder
                ? _layout.Reorder(sourceId, entry.Id)
                : _layout.MergeIntoFolder(sourceId, entry.Id);
            if (changed)
            {
                _layout.Save(_layoutPath);
                RenderCurrentView();
            }
        };

        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _draggingEntryId = null;
            if (entry.IsFolder)
            {
                OpenFolderOverlay(entry);
            }
            else if (!string.IsNullOrWhiteSpace(entry.LaunchTarget))
            {
                DismissSmooth();
                LaunchApp(entry.LaunchTarget);
            }
        };

        if (isInsideFolder && _openFolderId is not null)
        {
            string folderId = _openFolderId;
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_layout.RemoveFromFolder(folderId, entry.Id))
                {
                    _layout.Save(_layoutPath);
                    var updatedFolder = _layout.Entries.FirstOrDefault(x => x.IsFolder && x.Id == folderId);
                    if (updatedFolder is null)
                    {
                        FolderOverlay.Visibility = Visibility.Collapsed;
                        _openFolderId = null;
                    }
                    else
                    {
                        OpenFolderOverlay(updatedFolder);
                    }
                    RenderCurrentView();
                }
            };
        }

        return tile;
    }

    private UIElement BuildFolderTileVisual(LaunchpadEntry folder, List<(string Path, Image Target)>? asyncLoads)
    {
        var miniGrid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(7) };
        foreach (var child in folder.Children.Take(9))
        {
            miniGrid.Children.Add(BuildAppIconVisual(child.IconPath ?? child.LaunchTarget, size: 17, asyncLoads));
        }

        return new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0x3E, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = miniGrid
        };
    }

    private UIElement BuildAppIconVisual(string? path, double size, List<(string Path, Image Target)>? asyncLoads)
    {
        var img = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (_iconCache.TryGetValue(path, out var cached))
            {
                if (cached is not null)
                {
                    img.Source = cached;
                    return img;
                }
            }
            else if (asyncLoads is not null)
            {
                asyncLoads.Add((path, img));
                return img;
            }
            else
            {
                var loaded = PreloadIconBitmap(path);
                if (loaded is not null)
                {
                    img.Source = loaded;
                    return img;
                }
            }
        }

        return new Ellipse
        {
            Width = size * 0.82,
            Height = size * 0.82,
            Fill = new LinearGradientBrush(Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0x0A, 0x84, 0xFF), 45)
        };
    }

    private BitmapSource? PreloadIconBitmap(string path)
    {
        return _iconCache.GetOrAdd(path, p =>
        {
            var raw = _iconExtractor.ExtractHighResIcon(p, 128);
            if (raw is null) return null;
            var bmp = BitmapSource.Create(raw.Width, raw.Height, 96, 96, PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
            bmp.Freeze();
            return bmp;
        });
    }

    private void OpenFolderOverlay(LaunchpadEntry folder)
    {
        _openFolderId = folder.Id;
        FolderTitleBox.Text = folder.Title;
        FolderChildrenPanel.Children.Clear();
        foreach (var child in folder.Children)
        {
            FolderChildrenPanel.Children.Add(BuildTile(child, isInsideFolder: true, asyncLoads: null));
        }
        FolderOverlay.Visibility = Visibility.Visible;
    }

    private void OnBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == VeilBackdrop)
        {
            DismissSmooth();
        }
    }

    private void OnFolderBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == FolderOverlay)
        {
            CommitFolderTitle();
            FolderOverlay.Visibility = Visibility.Collapsed;
            _openFolderId = null;
        }
    }

    private void OnFolderCardMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void OnFolderTitleCommit(object sender, RoutedEventArgs e) => CommitFolderTitle();

    private void OnFolderTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFolderTitle();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CommitFolderTitle()
    {
        if (_openFolderId is not null && _layout.RenameFolder(_openFolderId, FolderTitleBox.Text))
        {
            _layout.Save(_layoutPath);
            RenderCurrentView();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderCurrentView();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (FolderOverlay.Visibility == Visibility.Visible)
            {
                CommitFolderTitle();
                FolderOverlay.Visibility = Visibility.Collapsed;
                _openFolderId = null;
            }
            else
            {
                DismissSmooth();
            }
            e.Handled = true;
            return;
        }

        if (FolderOverlay.Visibility == Visibility.Visible) return;

        if (e.Key == Key.Left && string.IsNullOrEmpty(SearchBox.Text))
        {
            _currentPage = _layout.ClampPage(_currentPage - 1);
            RenderCurrentView();
            e.Handled = true;
        }
        else if (e.Key == Key.Right && string.IsNullOrEmpty(SearchBox.Text))
        {
            _currentPage = _layout.ClampPage(_currentPage + 1);
            RenderCurrentView();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            var first = _layout.Filter(SearchBox.Text.Trim()).FirstOrDefault(x => !x.IsFolder);
            if (first?.LaunchTarget is not null)
            {
                DismissSmooth();
                LaunchApp(first.LaunchTarget);
                e.Handled = true;
            }
        }
    }

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FolderOverlay.Visibility == Visibility.Visible || !string.IsNullOrEmpty(SearchBox.Text)) return;
        int next = e.Delta < 0 ? _currentPage + 1 : _currentPage - 1;
        int clamped = _layout.ClampPage(next);
        if (clamped != _currentPage)
        {
            _currentPage = clamped;
            RenderCurrentView();
        }
    }

    private static void LaunchApp(string target)
    {
        try
        {
            string? workDir = System.IO.Path.GetDirectoryName(target);
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir) ? workDir : string.Empty
            });
        }
        catch { }
    }
}
