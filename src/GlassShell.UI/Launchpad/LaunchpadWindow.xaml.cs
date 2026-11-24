using System;
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
using System.Windows.Threading;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.Core.Launchpad;
using GlassShell.Core.Search;
using GlassShell.Core.Shell;
using GlassShell.UI.Glass;
using GlassShell.UI.MenuBar;
using Microsoft.Win32;

namespace GlassShell.UI.Launchpad;

public partial class LaunchpadWindow : Window
{
    private static readonly DropShadowEffect LabelShadow;
    private static readonly DropShadowEffect GhostShadow;
    private static readonly Geometry TinyLockGeometry;
    private static readonly Brush LockBadgeBg;
    private static readonly Brush LockBadgeBorder;
    private static readonly Brush LockGlyphStroke;

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

        GhostShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 18,
            ShadowDepth = 6,
            Opacity = 0.55
        };
        GhostShadow.Freeze();

        TinyLockGeometry = Geometry.Parse("M2.2,4.4 V3.1 C2.2,2.0 3.0,1.2 4.0,1.2 C5.0,1.2 5.8,2.0 5.8,3.1 V4.4 M1.4,4.4 H6.6 V7.8 H1.4 Z");
        TinyLockGeometry.Freeze();

        var bg = new SolidColorBrush(Color.FromArgb(0xA6, 0x16, 0x18, 0x20));
        bg.Freeze();
        LockBadgeBg = bg;

        var border = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        border.Freeze();
        LockBadgeBorder = border;

        var stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
        stroke.Freeze();
        LockGlyphStroke = stroke;
    }

    private readonly LaunchpadLayoutManager _layout = new();
    private readonly List<(LaunchpadEntry Entry, Border Tile)> _renderedTiles = [];
    private readonly LiquidGlassMenuPopup _tileContextMenu = new();
    private readonly DxgiBackdropSampler _sampler;
    private readonly DispatcherTimer _edgeFlipTimer;
    private readonly string _layoutPath;
    private bool _isDarkMode = true;

    private int _currentPage;
    private LaunchpadFilterMode _filterMode = LaunchpadFilterMode.Primary;
    private string? _openFolderId;
    private int _renderGeneration;

    // Pure-WPF mouse-capture drag-to-reorder state (supports cross-page drag!)
    private LaunchpadEntry? _pressEntry;
    private Border? _pressTile;
    private Point _pressStartPoint;
    private int _dragOriginPage;
    private int _pendingEdgeDirection; // -1 = prev page, +1 = next page, 0 = none
    private bool _isDragging;
    private Border? _dragGhost;
    private Border? _hoveredDropTile;
    private LaunchpadEntry? _hoveredDropEntry;

    public LaunchpadWindow(DxgiBackdropSampler sampler)
    {
        InitializeComponent();
        _sampler = sampler;
        _tileContextMenu.ConfigureVisuals(sampler, GlassOpacityPreset.Balanced, isDarkMode: true);

        _layoutPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassShell",
            "launchpad.json");
        _layout.Load(_layoutPath);

        _edgeFlipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _edgeFlipTimer.Tick += OnEdgeFlipTick;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowInteropHelperEx.ExcludeFromCapture(hwnd);
            _tileContextMenu.OwnerAnchorHwnd = hwnd;
        };
    }

    public event Action? RescanRequested;
    public event Action<string>? PinToDockToggled;
    public Func<string, bool>? IsPinnedToDockCheck { get; set; }

    public void ApplySettings(GlassSettings settings, bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
        _tileContextMenu.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);

        int cols = Math.Clamp(settings.LaunchpadColumns, 4, 10);
        int rows = Math.Clamp(settings.LaunchpadRows, 3, 8);
        bool gridChanged = _layout.Columns != cols || _layout.Rows != rows;
        _layout.SetGridSize(cols, rows);
        AppsGrid.Columns = _layout.Columns;
        AppsGrid.Rows = _layout.Rows;

        if (gridChanged && IsVisible)
        {
            _currentPage = _layout.ClampPage(_currentPage, _filterMode);
            RenderCurrentView();
        }
    }

    public void SyncApps(IReadOnlyList<InstalledAppEntry> apps)
    {
        _layout.SyncWithInstalledApps(apps);
        _currentPage = _layout.ClampPage(_currentPage, _filterMode);

        // Pre-warm wallpaper blur and page-1 app icons (35 items @ 96px) off the UI thread
        _ = Task.Run(() =>
        {
            BlurredWallpaperProvider.GetBlurredWallpaper();
            foreach (var app in _layout.ManualApps.Concat(apps).Take(_layout.ItemsPerPage))
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
        _currentPage = _layout.ClampPage(_currentPage, _filterMode);
        _openFolderId = null;
        FolderOverlay.Visibility = Visibility.Collapsed;
        CancelActiveDrag();

        WallpaperBlurImage.Source = BlurredWallpaperProvider.GetBlurredWallpaper();

        Left = monitor.Bounds.Left / monitor.DpiScale;
        Top = monitor.Bounds.Top / monitor.DpiScale;
        Width = monitor.WidthDip;
        Height = monitor.HeightDip;

        SearchBox.Text = string.Empty;
        SearchPlaceholder.Visibility = Visibility.Visible;
        UpdateSortLabel();
        RenderCurrentView();

        Show();
        Activate();
        SearchBox.Focus();
        AnimateEntrance();
    }

    public void DismissSmooth()
    {
        if (!IsVisible) return;
        _tileContextMenu.IsOpen = false;
        CancelActiveDrag();
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

    private void RenderCurrentView(bool preserveDrag = false)
    {
        if (!preserveDrag)
        {
            CancelActiveDrag();
        }
        else
        {
            if (_hoveredDropTile is not null)
            {
                _hoveredDropTile.Background = Brushes.Transparent;
                _hoveredDropTile.BorderBrush = Brushes.Transparent;
            }
            _hoveredDropTile = null;
            _hoveredDropEntry = null;
        }

        int gen = ++_renderGeneration;
        AppsGrid.Children.Clear();
        PageDotsPanel.Children.Clear();
        _renderedTiles.Clear();

        string query = SearchBox.Text.Trim();
        _currentPage = _layout.ClampPage(_currentPage, _filterMode);
        IReadOnlyList<LaunchpadEntry> pageItems = string.IsNullOrEmpty(query)
            ? _layout.GetPage(_currentPage, _filterMode)
            : _layout.Filter(query, _filterMode).Take(_layout.ItemsPerPage).ToList();

        var pendingAsyncLoads = new List<(string Path, Image Target)>();

        foreach (var entry in pageItems)
        {
            var tile = BuildTile(entry, isInsideFolder: false, pendingAsyncLoads);
            if (preserveDrag && _pressEntry is not null && string.Equals(entry.Id, _pressEntry.Id, StringComparison.OrdinalIgnoreCase))
            {
                tile.Opacity = 0.22;
                _pressTile = tile;
            }
            _renderedTiles.Add((entry, tile));
            AppsGrid.Children.Add(tile);
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
            int count = _layout.GetPageCount(_filterMode);
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

        if (preserveDrag && _isDragging)
        {
            UpdateEdgeHintsVisibility();
        }
    }

    private Border BuildTile(LaunchpadEntry entry, bool isInsideFolder, List<(string Path, Image Target)>? asyncLoads)
    {
        var iconScale = new ScaleTransform(1.0, 1.0);
        UIElement iconVisual = entry.IsFolder
            ? BuildFolderTileVisual(entry, asyncLoads)
            : BuildAppIconVisual(_layout.GetEffectiveIconPath(entry), size: 70, asyncLoads);

        var iconHost = new Grid
        {
            Width = 74,
            Height = 74,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = iconScale,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        iconHost.Children.Add(iconVisual);

        // Delicate, refined 12x12 monochrome vector lock badge at top-right (never oversized!)
        if (!isInsideFolder && _layout.IsLocked(entry.Id))
        {
            iconHost.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = LockBadgeBg,
                BorderBrush = LockBadgeBorder,
                BorderThickness = new Thickness(0.7),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0),
                IsHitTestVisible = false,
                Child = new System.Windows.Shapes.Path
                {
                    Data = TinyLockGeometry,
                    Stroke = LockGlyphStroke,
                    StrokeThickness = 1.0,
                    Width = 6.5,
                    Height = 6.5,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

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
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = stack
        };

        tile.MouseEnter += (_, _) =>
        {
            if (_isDragging) return;
            var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(110));
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };
        tile.MouseLeave += (_, _) =>
        {
            if (_isDragging) return;
            var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120));
            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };

        if (isInsideFolder)
        {
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(entry.LaunchTarget))
                {
                    DismissSmooth();
                    LaunchApp(entry.LaunchTarget);
                }
            };
        }
        else
        {
            tile.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _pressEntry = entry;
                _pressTile = tile;
                _pressStartPoint = e.GetPosition(RootGrid);
                _dragOriginPage = _currentPage;
                _isDragging = false;
                RootGrid.CaptureMouse();
            };
        }

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
        else if (!entry.IsFolder)
        {
            // Authentic macOS rounded Liquid Glass context menu (4 smooth rounded corners, no sharp Win32 rectangle!)
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowTileContextMenu(tile, entry);
            };
        }

        return tile;
    }

    private void ShowTileContextMenu(UIElement anchor, LaunchpadEntry entry)
    {
        var items = new List<PopupMenuEntry>();

        if (_filterMode == LaunchpadFilterMode.Hidden)
        {
            items.Add(new("取消隐藏（恢复显示）", OnClick: () =>
            {
                if (_layout.RestoreApp(entry.Id))
                {
                    _layout.Save(_layoutPath);
                    RenderCurrentView();
                }
            }));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(entry.LaunchTarget) && !entry.LaunchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                bool alreadyPinned = IsPinnedToDockCheck?.Invoke(entry.LaunchTarget) == true;
                items.Add(new(alreadyPinned ? "从程序坞中移除" : "添加到程序坞", OnClick: () =>
                {
                    PinToDockToggled?.Invoke(entry.LaunchTarget);
                }));
                items.Add(new(string.Empty, IsSeparator: true));
            }

            items.Add(new("更换图标…", OnClick: () => PickCustomIconForEntry(entry)));
            if (_layout.HasCustomIcon(entry.Id))
            {
                items.Add(new("恢复默认图标", OnClick: () =>
                {
                    if (_layout.ClearCustomIcon(entry.Id))
                    {
                        _layout.Save(_layoutPath);
                        RenderCurrentView();
                    }
                }));
            }

            items.Add(new(string.Empty, IsSeparator: true));

            bool locked = _layout.IsLocked(entry.Id);
            items.Add(new(locked ? "解除位置锁定" : "锁定此图标位置", OnClick: () =>
            {
                _layout.ToggleLock(entry.Id);
                _layout.Save(_layoutPath);
                RenderCurrentView();
            }));

            items.Add(new("在启动台中隐藏此应用", OnClick: () =>
            {
                if (_layout.HideApp(entry.Id))
                {
                    _layout.Save(_layoutPath);
                    RenderCurrentView();
                }
            }));

            if (_layout.IsManualApp(entry.Id))
            {
                items.Add(new(string.Empty, IsSeparator: true));
                items.Add(new("从启动台移除（手动添加）", OnClick: () =>
                {
                    if (_layout.RemoveManualApp(entry.Id))
                    {
                        _layout.Save(_layoutPath);
                        RenderCurrentView();
                    }
                }));
            }
        }

        _tileContextMenu.Placement = PlacementMode.MousePoint;
        _tileContextMenu.PlacementTarget = anchor;
        _tileContextMenu.HorizontalOffset = 4;
        _tileContextMenu.VerticalOffset = 4;
        _tileContextMenu.Populate(items, _isDarkMode);
        _tileContextMenu.IsOpen = true;
    }

    private void PickCustomIconForEntry(LaunchpadEntry entry)
    {
        CancelActiveDrag();
        var dlg = new OpenFileDialog
        {
            Title = $"为“{entry.Title}”选择新图标",
            Filter = "图片与图标 (*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.webp;*.exe)|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.webp;*.exe|所有文件 (*.*)|*.*",
            Multiselect = false,
            DereferenceLinks = false
        };

        bool? ok;
        Topmost = false;
        try
        {
            ok = dlg.ShowDialog(this);
        }
        finally
        {
            Topmost = true;
            Activate();
        }

        if (ok != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

        SharedIconCache.Invalidate(dlg.FileName);
        _layout.SetCustomIcon(entry.Id, dlg.FileName);
        _layout.Save(_layoutPath);
        RenderCurrentView();
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressEntry is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelActiveDrag();
            return;
        }

        Point pos = e.GetPosition(RootGrid);
        if (!_isDragging)
        {
            bool canDrag = !_layout.IsLocked(_pressEntry.Id)
                && string.IsNullOrEmpty(SearchBox.Text)
                && _filterMode != LaunchpadFilterMode.Hidden;
            if (!canDrag || (pos - _pressStartPoint).Length < 8.0) return;

            StartVisualDrag(_pressEntry, _pressTile, pos);
        }

        if (_dragGhost is not null)
        {
            Canvas.SetLeft(_dragGhost, pos.X - 44);
            Canvas.SetTop(_dragGhost, pos.Y - 44);
        }

        CheckCrossPageEdgeHover(pos);
        UpdateDropTargetHighlight(pos);
    }

    private void CheckCrossPageEdgeHover(Point pos)
    {
        int pageCount = _layout.GetPageCount(_filterMode);
        int desiredDir = 0;

        if (pos.X <= 96 && _currentPage > 0)
        {
            desiredDir = -1;
        }
        else if (pos.X >= RootGrid.ActualWidth - 96 && _currentPage < pageCount - 1)
        {
            desiredDir = 1;
        }

        if (desiredDir != _pendingEdgeDirection)
        {
            _pendingEdgeDirection = desiredDir;
            _edgeFlipTimer.Stop();
            if (desiredDir != 0)
            {
                _edgeFlipTimer.Start();
            }
        }
    }

    private void OnEdgeFlipTick(object? sender, EventArgs e)
    {
        if (!_isDragging || _pendingEdgeDirection == 0)
        {
            _edgeFlipTimer.Stop();
            return;
        }

        int next = _layout.ClampPage(_currentPage + _pendingEdgeDirection, _filterMode);
        if (next != _currentPage)
        {
            _currentPage = next;
            RenderCurrentView(preserveDrag: true);
        }
        else
        {
            _edgeFlipTimer.Stop();
        }
    }

    private void UpdateEdgeHintsVisibility()
    {
        int pageCount = _layout.GetPageCount(_filterMode);
        PrevPageEdgeHint.Visibility = _isDragging && _currentPage > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextPageEdgeHint.Visibility = _isDragging && _currentPage < pageCount - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressEntry is null) return;
        e.Handled = true;

        var pressed = _pressEntry;
        var dropTarget = _hoveredDropEntry;
        int originPage = _dragOriginPage;
        int dropPage = _currentPage;
        bool wasDragging = _isDragging;

        CancelActiveDrag();

        if (wasDragging)
        {
            bool changed = false;
            if (dropTarget is not null && !string.Equals(pressed.Id, dropTarget.Id, StringComparison.OrdinalIgnoreCase))
            {
                bool ctrlFolder = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                changed = ctrlFolder
                    ? _layout.MergeIntoFolder(pressed.Id, dropTarget.Id)
                    : _layout.Reorder(pressed.Id, dropTarget.Id);
            }
            else if (dropPage != originPage)
            {
                // Dropped onto blank area after flipping to another page -> move to end of that page!
                changed = _layout.MoveToEndOfPage(pressed.Id, dropPage, _filterMode);
            }

            if (changed)
            {
                UpdateSortLabel();
                _layout.Save(_layoutPath);
                RenderCurrentView();
            }
            return;
        }

        // Normal click -> open folder or launch application
        if (pressed.IsFolder)
        {
            OpenFolderOverlay(pressed);
        }
        else if (!string.IsNullOrWhiteSpace(pressed.LaunchTarget))
        {
            DismissSmooth();
            LaunchApp(pressed.LaunchTarget);
        }
    }

    private void OnRootLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_pressEntry is not null || _isDragging)
        {
            CancelActiveDrag();
        }
    }

    private void StartVisualDrag(LaunchpadEntry entry, Border? sourceTile, Point cursorPos)
    {
        _isDragging = true;
        if (sourceTile is not null) sourceTile.Opacity = 0.22;

        UIElement ghostIcon = entry.IsFolder
            ? BuildFolderTileVisual(entry, asyncLoads: null)
            : BuildAppIconVisual(_layout.GetEffectiveIconPath(entry), size: 78, asyncLoads: null);

        _dragGhost = new Border
        {
            Width = 88,
            Height = 88,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            Effect = GhostShadow,
            Child = ghostIcon
        };

        DragOverlayCanvas.Children.Clear();
        DragOverlayCanvas.Children.Add(_dragGhost);
        Canvas.SetLeft(_dragGhost, cursorPos.X - 44);
        Canvas.SetTop(_dragGhost, cursorPos.Y - 44);
        UpdateEdgeHintsVisibility();
    }

    private void UpdateDropTargetHighlight(Point cursorInRoot)
    {
        (LaunchpadEntry Entry, Border Tile)? best = null;
        double bestDistSq = double.MaxValue;

        foreach (var item in _renderedTiles)
        {
            if (item.Entry.Id == _pressEntry?.Id) continue;
            if (!item.Tile.IsLoaded || item.Tile.ActualWidth <= 0) continue;

            try
            {
                var rect = item.Tile.TransformToAncestor(RootGrid)
                    .TransformBounds(new Rect(0, 0, item.Tile.ActualWidth, item.Tile.ActualHeight));
                rect.Inflate(28, 22);
                if (!rect.Contains(cursorInRoot)) continue;

                double cx = rect.Left + rect.Width * 0.5;
                double cy = rect.Top + rect.Height * 0.5;
                double d2 = (cx - cursorInRoot.X) * (cx - cursorInRoot.X) + (cy - cursorInRoot.Y) * (cy - cursorInRoot.Y);
                if (d2 < bestDistSq)
                {
                    bestDistSq = d2;
                    best = item;
                }
            }
            catch { }
        }

        if (_hoveredDropTile is not null && _hoveredDropTile != best?.Tile)
        {
            _hoveredDropTile.Background = Brushes.Transparent;
            _hoveredDropTile.BorderBrush = Brushes.Transparent;
        }

        _hoveredDropTile = best?.Tile;
        _hoveredDropEntry = best?.Entry;

        if (_hoveredDropTile is not null)
        {
            bool ctrlFolder = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            _hoveredDropTile.Background = new SolidColorBrush(ctrlFolder
                ? Color.FromArgb(0x40, 0x0A, 0x84, 0xFF)
                : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
            _hoveredDropTile.BorderBrush = new SolidColorBrush(ctrlFolder
                ? Color.FromArgb(0xCC, 0x5A, 0xC8, 0xFA)
                : Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        }
    }

    private void CancelActiveDrag()
    {
        _edgeFlipTimer.Stop();
        _pendingEdgeDirection = 0;
        PrevPageEdgeHint.Visibility = Visibility.Collapsed;
        NextPageEdgeHint.Visibility = Visibility.Collapsed;

        if (RootGrid.IsMouseCaptured)
        {
            RootGrid.ReleaseMouseCapture();
        }
        if (_pressTile is not null)
        {
            _pressTile.Opacity = 1.0;
        }
        if (_hoveredDropTile is not null)
        {
            _hoveredDropTile.Background = Brushes.Transparent;
            _hoveredDropTile.BorderBrush = Brushes.Transparent;
        }
        DragOverlayCanvas.Children.Clear();
        _dragGhost = null;
        _hoveredDropTile = null;
        _hoveredDropEntry = null;
        _pressTile = null;
        _pressEntry = null;
        _isDragging = false;
    }

    private UIElement BuildFolderTileVisual(LaunchpadEntry folder, List<(string Path, Image Target)>? asyncLoads)
    {
        var miniGrid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(7) };
        foreach (var child in folder.Children.Take(9))
        {
            miniGrid.Children.Add(BuildAppIconVisual(_layout.GetEffectiveIconPath(child), size: 17, asyncLoads));
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
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (SharedIconCache.TryGetCached(path, 128, out var cached))
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

    private static BitmapSource? PreloadIconBitmap(string path) => SharedIconCache.GetOrExtract(path, 128);

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

    private void OnFilterPrimaryClick(object sender, RoutedEventArgs e) => SetFilterMode(LaunchpadFilterMode.Primary);
    private void OnFilterAllClick(object sender, RoutedEventArgs e) => SetFilterMode(LaunchpadFilterMode.All);
    private void OnFilterHiddenClick(object sender, RoutedEventArgs e) => SetFilterMode(LaunchpadFilterMode.Hidden);

    private void OnCycleSortModeClick(object sender, RoutedEventArgs e)
    {
        _layout.SortMode = _layout.SortMode switch
        {
            LaunchpadSortMode.Custom => LaunchpadSortMode.NameAsc,
            LaunchpadSortMode.NameAsc => LaunchpadSortMode.NameDesc,
            LaunchpadSortMode.NameDesc => LaunchpadSortMode.Pinyin,
            _ => LaunchpadSortMode.Custom
        };
        UpdateSortLabel();
        _layout.Save(_layoutPath);
        RenderCurrentView();
    }

    private void OnAddManualAppClick(object sender, RoutedEventArgs e)
    {
        CancelActiveDrag();
        var dlg = new OpenFileDialog
        {
            Title = "选择要添加到启动台的应用程序",
            Filter = "应用程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            Multiselect = true,
            DereferenceLinks = false
        };

        bool? ok;
        Topmost = false;
        try
        {
            ok = dlg.ShowDialog(this);
        }
        finally
        {
            Topmost = true;
            Activate();
        }

        if (ok != true || dlg.FileNames.Length == 0) return;

        bool addedAny = false;
        foreach (string file in dlg.FileNames)
        {
            var entry = InstalledAppScanner.CreateManualEntry(file);
            if (entry is not null && _layout.AddManualApp(entry))
            {
                addedAny = true;
            }
        }

        if (addedAny)
        {
            _currentPage = 0;
            _layout.Save(_layoutPath);
            RenderCurrentView();
        }
    }

    private void OnRescanClick(object sender, RoutedEventArgs e)
    {
        CancelActiveDrag();
        RescanRequested?.Invoke();
    }

    private void UpdateSortLabel()
    {
        SortModeLabel.Text = _layout.SortMode switch
        {
            LaunchpadSortMode.NameAsc => "排序: A–Z",
            LaunchpadSortMode.NameDesc => "排序: Z–A",
            LaunchpadSortMode.Pinyin => "排序: 拼音",
            _ => "排序: 自定义"
        };
    }

    private void SetFilterMode(LaunchpadFilterMode mode)
    {
        _filterMode = mode;
        _currentPage = 0;

        var activeBg = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        var activeFg = Brushes.White;
        var idleFg = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        activeBg.Freeze();
        idleFg.Freeze();

        FilterPrimaryBtn.Background = mode == LaunchpadFilterMode.Primary ? activeBg : Brushes.Transparent;
        FilterPrimaryBtn.Foreground = mode == LaunchpadFilterMode.Primary ? activeFg : idleFg;
        FilterAllBtn.Background = mode == LaunchpadFilterMode.All ? activeBg : Brushes.Transparent;
        FilterAllBtn.Foreground = mode == LaunchpadFilterMode.All ? activeFg : idleFg;
        FilterHiddenBtn.Background = mode == LaunchpadFilterMode.Hidden ? activeBg : Brushes.Transparent;
        FilterHiddenBtn.Foreground = mode == LaunchpadFilterMode.Hidden ? activeFg : idleFg;

        RenderCurrentView();
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
            if (_isDragging)
            {
                CancelActiveDrag();
            }
            else if (FolderOverlay.Visibility == Visibility.Visible)
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
            _currentPage = _layout.ClampPage(_currentPage - 1, _filterMode);
            RenderCurrentView(preserveDrag: _isDragging);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && string.IsNullOrEmpty(SearchBox.Text))
        {
            _currentPage = _layout.ClampPage(_currentPage + 1, _filterMode);
            RenderCurrentView(preserveDrag: _isDragging);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            var first = _layout.Filter(SearchBox.Text.Trim(), _filterMode).FirstOrDefault(x => !x.IsFolder);
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
        int clamped = _layout.ClampPage(next, _filterMode);
        if (clamped != _currentPage)
        {
            _currentPage = clamped;
            // Allow flipping pages with the scroll wheel WHILE dragging an icon across pages!
            RenderCurrentView(preserveDrag: _isDragging);
        }
    }

    private static void LaunchApp(string target)
    {
        try
        {
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                return;
            }

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
