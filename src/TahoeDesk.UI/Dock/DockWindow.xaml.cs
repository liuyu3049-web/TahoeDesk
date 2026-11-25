using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using TahoeDesk.Core.Config;
using TahoeDesk.Core.Discovery;
using TahoeDesk.Core.DockPhysics;
using TahoeDesk.Core.Shell;
using TahoeDesk.UI.Glass;
using TahoeDesk.UI.MenuBar;

namespace TahoeDesk.UI.Dock;

public partial class DockWindow : Window
{
    private sealed class DockSlotVisual
    {
        public required DockItemModel Model { get; set; }
        public required Grid Container { get; init; }
        public required ScaleTransform IconScale { get; init; }
        public required TranslateTransform SlotTranslate { get; init; }
        public required Ellipse RunningDot { get; init; }
        public required Ellipse FlashBadge { get; init; }
    }

    private const double IconGapDip = 8.0;
    private const double DividerWidthDip = 14.0;
    private const double CapsuleHorizontalPadDip = 14.0;

    private readonly MonitorContext _monitor;
    private readonly RecycleBinService _recycleBinService = new();
    private readonly SpringBounceAnimator _bounceAnimator = new();
    private readonly DockHoverAnimator _hoverAnimator = new();
    private readonly DockAutoHideController _autoHideController = new();
    private readonly DxgiBackdropSampler _sampler;
    private readonly WindowSnapshotCache<BitmapSource>? _snapshotCache;
    private readonly LiquidGlassMenuPopup _contextMenu = new();
    private readonly FolderStackPopup _folderStackPopup = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly List<DockSlotVisual> _slots = [];
    private readonly List<(Border Visual, TranslateTransform Translate)> _dividers = [];
    private readonly List<int> _dividerAfterIndices = [];

    private GlassSettings _settings;
    private bool _isDarkMode;
    private IReadOnlyList<DockItemModel> _lastItems = [];
    private double? _rawPointerX;
    private bool _pointerActive;
    private double _lastFrameSeconds;
    private double _lastCursorPollMs;
    private double? _bottomEdgeEnterMs;
    private double _currentSlideY;
    private double _currentCapsuleWidth = 360;
    private bool _yieldForTray;
    private bool _hasBottomOverlap;
    private bool _layoutDirty = true;
    private Point _dragStartPoint;
    private DockItemModel? _draggingItem;
    private string? _hoveredItemId;
    private string? _lastTooltipText;

    public DockWindow(
        MonitorContext monitor,
        DxgiBackdropSampler sampler,
        GlassSettings settings,
        bool isDarkMode,
        WindowSnapshotCache<BitmapSource>? snapshotCache = null)
    {
        InitializeComponent();
        _monitor = monitor;
        _sampler = sampler;
        _snapshotCache = snapshotCache;
        _settings = settings;
        _isDarkMode = isDarkMode;

        CapsuleGlass.AttachSampler(sampler);
        ApplySettings(settings, isDarkMode);
        UpdateCompactWindowBounds(360);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowInteropHelperEx.ApplyShellOverlayStyles(hwnd);
            _contextMenu.OwnerAnchorHwnd = hwnd;
            _folderStackPopup.OwnerAnchorHwnd = hwnd;
        };
        MouseMove += OnDockMouseMove;
        MouseLeave += OnDockMouseLeave;
        DragOver += OnDockDragOver;
        Drop += OnDockDrop;

        Loaded += (_, _) => CompositionTarget.Rendering += OnRenderFrame;
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            CapsuleGlass.Dispose();
        };
    }

    public event Action? LaunchpadRequested;
    public event Action<GlassSettings>? SettingsUpdated;
    public event Action<string>? RemoveRecentAppRequested;
    public event Action<string>? ActivateTrayAppRequested;

    public void ApplySettings(GlassSettings settings, bool isDarkMode)
    {
        bool rebuildNeeded = _settings.DockIconSize != settings.DockIconSize
            || _settings.HarmonizeDockIcons != settings.HarmonizeDockIcons;
        _settings = settings;
        _isDarkMode = isDarkMode;
        CapsuleGlass.OpacityPreset = settings.OpacityPreset;
        CapsuleGlass.IsDarkMode = isDarkMode;
        _contextMenu.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        _folderStackPopup.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        UpdateCapsuleHeight();
        if (rebuildNeeded && _lastItems.Count > 0)
        {
            RebuildVisualSlots(_lastItems);
        }
        _layoutDirty = true;
    }

    public void SetYieldForTray(bool yield) => _yieldForTray = yield;
    public void SetBottomOverlap(bool overlap) => _hasBottomOverlap = overlap;
    public void DismissPopups() => Dispatcher.InvokeAsync(() =>
    {
        _contextMenu.IsOpen = false;
        _folderStackPopup.IsOpen = false;
    });

    public void UpdateItems(IReadOnlyList<DockItemModel> items)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DockModelCoordinator.AreEquivalent(_lastItems, items))
            {
                return;
            }

            // Fast in-place update when only running/flashing/window-list metadata changed (same slot order):
            // ZERO visual teardown, ZERO DockWindow resize, ZERO flicker!
            if (TryUpdateSlotsInPlace(items))
            {
                _lastItems = items;
                return;
            }

            _lastItems = items;
            RebuildVisualSlots(items);
        });
    }

    private bool TryUpdateSlotsInPlace(IReadOnlyList<DockItemModel> items)
    {
        if (_slots.Count != items.Count || _slots.Count == 0) return false;
        for (int i = 0; i < items.Count; i++)
        {
            var prev = _slots[i].Model;
            var next = items[i];
            if (!string.Equals(prev.Id, next.Id, StringComparison.OrdinalIgnoreCase)
                || prev.Kind != next.Kind
                || prev.Section != next.Section
                || prev.IsRecycleBinEmpty != next.IsRecycleBinEmpty
                || !string.Equals(prev.TargetPath, next.TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            var slot = _slots[i];
            bool wasFlashing = slot.Model.IsFlashing;
            var next = items[i];
            slot.Model = next;

            slot.RunningDot.Width = next.IsFlashing ? 5.5 : 4.5;
            slot.RunningDot.Height = next.IsFlashing ? 5.5 : 4.5;
            slot.RunningDot.Fill = new SolidColorBrush(next.IsFlashing
                ? Color.FromRgb(0xFF, 0x45, 0x3A)
                : Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF));
            slot.RunningDot.Visibility = next.IsRunning || next.IsFlashing ? Visibility.Visible : Visibility.Hidden;
            slot.FlashBadge.Visibility = next.IsFlashing ? Visibility.Visible : Visibility.Collapsed;

            if (next.IsFlashing && !wasFlashing)
            {
                _bounceAnimator.TriggerBounce(next.Id, _clock.Elapsed.TotalSeconds, hops: 2);
                _layoutDirty = true;
            }
        }

        return true;
    }

    private void UpdateCompactWindowBounds(double maxMagnifiedCapsuleWidth)
    {
        // Hysteresis sizing: keep generous headroom so opening/closing 1-2 apps NEVER resizes DockWindow or recreates D3D11 textures!
        double minNeededW = Math.Min(_monitor.WidthDip, Math.Max(480, Math.Ceiling(maxMagnifiedCapsuleWidth + 160)));
        double targetW = Width;
        if (Width < minNeededW)
        {
            targetW = Math.Min(_monitor.WidthDip, Math.Ceiling(minNeededW + 140));
        }
        else if (Width > minNeededW + 260)
        {
            targetW = Math.Min(_monitor.WidthDip, Math.Ceiling(minNeededW + 100));
        }

        if (Math.Abs(Width - targetW) > 1.0)
        {
            Width = targetW;
        }
        Left = (_monitor.Bounds.Left / _monitor.DpiScale) + (_monitor.WidthDip - Width) * 0.5;
        Top = (_monitor.Bounds.Bottom / _monitor.DpiScale) - Height;
    }

    private void RebuildVisualSlots(IReadOnlyList<DockItemModel> items)
    {
        IconsCanvas.Children.Clear();
        _slots.Clear();
        _dividers.Clear();
        _dividerAfterIndices.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var model = items[i];
            if (i > 0 && items[i - 1].Section != model.Section)
            {
                _dividerAfterIndices.Add(i - 1);
                var divTranslate = new TranslateTransform();
                var divider = new Border
                {
                    Width = 1,
                    Height = _settings.DockIconSize * 0.72,
                    Background = new SolidColorBrush(_isDarkMode
                        ? Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0x3A, 0x00, 0x00, 0x00)),
                    IsHitTestVisible = false,
                    RenderTransform = divTranslate
                };
                _dividers.Add((divider, divTranslate));
                IconsCanvas.Children.Add(divider);
            }

            var visual = CreateSlotVisual(model);
            _slots.Add(visual);
            IconsCanvas.Children.Add(visual.Container);

            if (model.IsFlashing)
            {
                _bounceAnimator.TriggerBounce(model.Id, _clock.Elapsed.TotalSeconds, hops: 2);
            }
        }

        // Compute max possible magnified Dock width once so DockWindow stays compact and NEVER resizes on hover
        var maxLayout = DockMagnificationCalculator.ComputeCenteredFull(
            _slots.Count,
            _settings.DockIconSize,
            IconGapDip,
            _dividerAfterIndices,
            DividerWidthDip,
            pointerRelCenter: 0.0,
            magnificationEnabled: _settings.DockMagnificationEnabled,
            maxScale: _settings.DockMagnificationScale);
        UpdateCompactWindowBounds(maxLayout.TotalWidth + CapsuleHorizontalPadDip * 2);

        UpdateCapsuleHeight();
        _layoutDirty = true;
        LayoutFrame();
    }

    private DockSlotVisual CreateSlotVisual(DockItemModel model)
    {
        UIElement iconContent = BuildIconVisual(model);
        double baseSize = _settings.DockIconSize;

        var iconScale = new ScaleTransform(1.0, 1.0);
        var slotTranslate = new TranslateTransform(0.0, 0.0);

        var iconGrid = new Grid();
        iconGrid.Children.Add(iconContent);

        var flashBadge = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 1, 0),
            IsHitTestVisible = false,
            Visibility = model.IsFlashing ? Visibility.Visible : Visibility.Collapsed
        };
        iconGrid.Children.Add(flashBadge);

        var iconFrame = new Border
        {
            Width = baseSize,
            Height = baseSize,
            Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 1.0), // Anchor at bottom-center -> scales cleanly UPWARD with zero layout pass!
            RenderTransform = iconScale,
            VerticalAlignment = VerticalAlignment.Top,
            Child = iconGrid
        };

        var dot = new Ellipse
        {
            Width = model.IsFlashing ? 5.5 : 4.5,
            Height = model.IsFlashing ? 5.5 : 4.5,
            Fill = new SolidColorBrush(model.IsFlashing
                ? Color.FromRgb(0xFF, 0x45, 0x3A)
                : Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
            Visibility = model.IsRunning || model.IsFlashing ? Visibility.Visible : Visibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var container = new Grid
        {
            Width = baseSize,
            Height = baseSize + 10,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            AllowDrop = true,
            RenderTransform = slotTranslate
        };
        container.Children.Add(iconFrame);
        container.Children.Add(dot);

        var slot = new DockSlotVisual
        {
            Model = model,
            Container = container,
            IconScale = iconScale,
            SlotTranslate = slotTranslate,
            RunningDot = dot,
            FlashBadge = flashBadge
        };

        container.MouseLeftButtonDown += (_, e) =>
        {
            _dragStartPoint = e.GetPosition(this);
            _draggingItem = slot.Model;
        };

        container.MouseMove += (_, e) =>
        {
            var cur = slot.Model;
            if (e.LeftButton == MouseButtonState.Pressed && _draggingItem?.Id == cur.Id && cur.IsPinned && cur.Kind == DockItemKind.Application)
            {
                var pos = e.GetPosition(this);
                if (Math.Abs(pos.Y - _dragStartPoint.Y) > 78 && pos.Y < _dragStartPoint.Y)
                {
                    UnpinApp(cur.TargetPath);
                    _draggingItem = null;
                }
            }
        };

        container.MouseLeftButtonUp += (_, e) =>
        {
            _draggingItem = null;
            e.Handled = true;
            HandleItemClick(slot.Model, container);
        };

        container.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var cur = slot.Model;
            if (cur.Kind == DockItemKind.Launchpad)
            {
                HideInlineTooltip();
                OpenNativeWindowsStartMenu();
                return;
            }
            ShowContextMenu(container, cur);
        };

        container.Drop += (_, e) =>
        {
            var cur = slot.Model;
            if (cur.Kind == DockItemKind.Application && !string.IsNullOrWhiteSpace(cur.TargetPath)
                && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files is { Length: > 0 })
                {
                    e.Handled = true;
                    LaunchAppWithFiles(cur.TargetPath, files);
                }
            }
        };

        return slot;
    }

    private UIElement BuildIconVisual(DockItemModel model)
    {
        if (model.Kind == DockItemKind.Launchpad) return BuildLaunchpadVectorIcon();
        if (model.Kind == DockItemKind.Explorer) return BuildExplorerVectorIcon();
        if (model.Kind == DockItemKind.RecycleBin) return BuildRecycleBinVectorIcon(model.IsRecycleBinEmpty);
        if (model.Kind == DockItemKind.MinimizedWindow) return BuildMinimizedWindowVisual(model);

        if (!string.IsNullOrWhiteSpace(model.TargetPath))
        {
            var iconEntry = SharedIconCache.GetOrExtractWithShape(model.TargetPath, 128);
            if (iconEntry is not null)
            {
                var img = new Image
                {
                    Source = iconEntry.Bitmap,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(_settings.HarmonizeDockIcons && iconEntry.HasTransparentCorners ? 6 : 3)
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);

                if (_settings.HarmonizeDockIcons && iconEntry.HasTransparentCorners)
                {
                    var tileBg = new LinearGradientBrush(
                        _isDarkMode ? Color.FromArgb(0xDC, 0x3C, 0x42, 0x52) : Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF),
                        _isDarkMode ? Color.FromArgb(0xD4, 0x24, 0x28, 0x34) : Color.FromArgb(0xE0, 0xE6, 0xEC, 0xF5),
                        90);
                    var tileBorder = new SolidColorBrush(_isDarkMode ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0x00, 0x00, 0x00));
                    tileBg.Freeze();
                    tileBorder.Freeze();

                    return new Border
                    {
                        Margin = new Thickness(2.5),
                        CornerRadius = new CornerRadius(Math.Max(10, _settings.DockIconSize * 0.225)),
                        Background = tileBg,
                        BorderBrush = tileBorder,
                        BorderThickness = new Thickness(0.8),
                        Child = img
                    };
                }

                return img;
            }
        }

        return new Ellipse
        {
            Fill = new LinearGradientBrush(Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0x0A, 0x84, 0xFF), 45),
            Margin = new Thickness(5)
        };
    }

    private UIElement BuildMinimizedWindowVisual(DockItemModel model)
    {
        nint hwnd = model.OpenWindowHandles.Count > 0 ? model.OpenWindowHandles[0] : 0;
        var root = new Grid { Margin = new Thickness(3) };

        if (hwnd != 0 && _snapshotCache is not null && _snapshotCache.TryGet(hwnd, out var thumb) && thumb is not null)
        {
            var img = new Image
            {
                Source = thumb,
                Stretch = Stretch.UniformToFill
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);

            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = img
            });
        }
        else
        {
            // Fallback glass mini-card with app icon if minimized before TahoeDesk started
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(_isDarkMode ? Color.FromArgb(0x99, 0x26, 0x2B, 0x38) : Color.FromArgb(0xBB, 0xE8, 0xEE, 0xF7)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            });
        }

        if (!string.IsNullOrWhiteSpace(model.TargetPath))
        {
            var bmp = SharedIconCache.GetOrExtract(model.TargetPath, 48);
            if (bmp is not null)
            {
                var badge = new Image
                {
                    Source = bmp,
                    Width = 18,
                    Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, -2, -2)
                };
                RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.Linear);
                root.Children.Add(badge);
            }
        }

        return root;
    }

    private static UIElement BuildExplorerVectorIcon()
    {
        var grid = new Grid { Margin = new Thickness(4) };
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,6.5 C2,4.8 3.3,3.5 5,3.5 H10.2 C11.1,3.5 11.9,3.9 12.5,4.6 L13.8,6.2 H21 C22.7,6.2 24,7.5 24,9.2 V19.2 C24,20.9 22.7,22.2 21,22.2 H5 C3.3,22.2 2,20.9 2,19.2 Z"),
            Fill = new LinearGradientBrush(Color.FromRgb(0x70, 0xD6, 0xFF), Color.FromRgb(0x0A, 0x84, 0xFF), 90),
            Stretch = Stretch.Uniform
        });
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M2,9.5 C2,8.4 2.9,7.5 4,7.5 H22 C23.1,7.5 24,8.4 24,9.5 V19.2 C24,20.9 22.7,22.2 21,22.2 H5 C3.3,22.2 2,20.9 2,19.2 Z"),
            Fill = new LinearGradientBrush(Color.FromRgb(0x52, 0xC4, 0xFF), Color.FromRgb(0x00, 0x6E, 0xF5), 90),
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 0.6,
            Stretch = Stretch.Uniform
        });
        return grid;
    }

    private static UIElement BuildLaunchpadVectorIcon()
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(4) };
        Color[] palette =
        [
            Color.FromRgb(0xFF, 0x5E, 0x57), Color.FromRgb(0xFF, 0xBD, 0x2E), Color.FromRgb(0x28, 0xCD, 0x41),
            Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x58, 0x56, 0xD6),
            Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0xFF, 0x2D, 0x55), Color.FromRgb(0xFF, 0x9F, 0x0A)
        ];
        foreach (var c in palette)
        {
            grid.Children.Add(new Ellipse { Margin = new Thickness(2.2), Fill = new SolidColorBrush(c) });
        }
        return grid;
    }

    private static UIElement BuildRecycleBinVectorIcon(bool isEmpty)
    {
        var grid = new Grid { Margin = new Thickness(4) };
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M5.5,6.5 H18.5 L17.1,20.5 C17,21.6 16.1,22.5 15,22.5 H9 C7.9,22.5 7,21.6 6.9,20.5 Z"),
            Fill = new LinearGradientBrush(
                isEmpty ? Color.FromArgb(0x88, 0xE6, 0xEC, 0xF5) : Color.FromArgb(0xBB, 0xD8, 0xE2, 0xF0),
                Color.FromArgb(0xAA, 0x98, 0xA4, 0xB8),
                90),
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.2,
            Stretch = Stretch.Uniform
        });
        grid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M4.2,5.5 H19.8 M9.2,5.5 V3.6 C9.2,3.1 9.6,2.8 10.1,2.8 H13.9 C14.4,2.8 14.8,3.1 14.8,3.6 V5.5 M9.8,9.5 L10.2,19 M12,9.5 V19 M14.2,9.5 L13.8,19"),
            Stroke = new SolidColorBrush(Color.FromArgb(0xDD, 0x38, 0x42, 0x52)),
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform
        });
        return grid;
    }

    private void OnDockMouseMove(object sender, MouseEventArgs e)
    {
        var pt = e.GetPosition(RootContainer);
        double relCenter = pt.X - RootContainer.ActualWidth * 0.5;
        bool insideCapsuleX = Math.Abs(relCenter) <= (_currentCapsuleWidth * 0.5 + 8.0);
        if (insideCapsuleX)
        {
            _rawPointerX = relCenter;
            _pointerActive = true;
            _layoutDirty = true;
        }
        else if (_pointerActive)
        {
            _rawPointerX = null;
            _pointerActive = false;
            _hoveredItemId = null;
            HideInlineTooltip();
            _layoutDirty = true;
        }
    }

    private void OnDockMouseLeave(object sender, MouseEventArgs e)
    {
        _rawPointerX = null;
        _pointerActive = false;
        _hoveredItemId = null;
        HideInlineTooltip();
        _layoutDirty = true;
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        double nowSec = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrameSeconds > 0 ? nowSec - _lastFrameSeconds : 0.016;
        _lastFrameSeconds = nowSec;

        double prevHover = _hoverAnimator.HoverAmount;
        _hoverAnimator.Step(_pointerActive, _rawPointerX, dt);

        bool animating = _layoutDirty
            || _pointerActive
            || _hoverAnimator.HoverAmount > 0.003
            || Math.Abs(_hoverAnimator.HoverAmount - prevHover) > 0.0005
            || _bounceAnimator.HasActiveAnimations;

        if (animating)
        {
            _layoutDirty = false;
            LayoutFrame();
        }

        // Skip GetCursorPos polling completely when Dock is stationary and visible with no auto-hide condition active!
        bool autoHideMayEngage = _yieldForTray
            || _currentSlideY > 0.15
            || _settings.DockAutoHide == DockAutoHideMode.Always
            || (_settings.DockAutoHide == DockAutoHideMode.Smart && _hasBottomOverlap);

        if (!autoHideMayEngage)
        {
            _bottomEdgeEnterMs = null;
            return;
        }

        double nowMs = nowSec * 1000.0;
        // Throttle idle cursor checks to ~30Hz when Dock is tucked away; full refresh rate while sliding!
        if (_currentSlideY > 100.0 && (nowMs - _lastCursorPollMs) < 33.0)
        {
            return;
        }
        _lastCursorPollMs = nowMs;

        // Auto-hide check: reveal on bottom-edge hover, and hide IMMEDIATELY once cursor leaves the Dock capsule!
        bool isAtBottomEdge = false;
        double dwellMs = 0;
        if (GetCursorPos(out POINT cursorPx))
        {
            bool withinMonitorX = cursorPx.X >= _monitor.Bounds.Left && cursorPx.X <= _monitor.Bounds.Right;
            bool nearBottom = cursorPx.Y >= _monitor.Bounds.Bottom - 5 && cursorPx.Y <= _monitor.Bounds.Bottom;

            double dockLeftPx = Left * _monitor.DpiScale;
            double dockRightPx = (Left + Width) * _monitor.DpiScale;
            double dockTopZonePx = _monitor.Bounds.Bottom - (_settings.DockIconSize + 36) * _monitor.DpiScale;
            bool insideRevealedDock = _currentSlideY < 10.0
                && cursorPx.X >= dockLeftPx && cursorPx.X <= dockRightPx
                && cursorPx.Y >= dockTopZonePx && cursorPx.Y <= _monitor.Bounds.Bottom;

            if (withinMonitorX && (nearBottom || insideRevealedDock))
            {
                isAtBottomEdge = true;
                _bottomEdgeEnterMs ??= insideRevealedDock ? nowMs - DockAutoHideController.DefaultTriggerDelayMs : nowMs;
                dwellMs = nowMs - _bottomEdgeEnterMs.Value;
            }
            else
            {
                _bottomEdgeEnterMs = null;
            }
        }

        bool shouldHide = _yieldForTray || _autoHideController.ShouldHide(
            _settings.DockAutoHide,
            _hasBottomOverlap,
            isPointerAtBottomEdge: isAtBottomEdge || _pointerActive,
            bottomEdgeDwellMs: _pointerActive ? DockAutoHideController.DefaultTriggerDelayMs : dwellMs,
            isMenuOrDragActive: _contextMenu.IsOpen || _folderStackPopup.IsOpen || _draggingItem is not null);

        double targetSlideY = shouldHide ? 104.0 : 0.0;
        if (Math.Abs(targetSlideY - _currentSlideY) > 0.15)
        {
            double slideAlpha = 1.0 - Math.Exp(-14.0 * Math.Clamp(dt, 0.001, 0.05));
            _currentSlideY += (targetSlideY - _currentSlideY) * slideAlpha;
            if (Math.Abs(targetSlideY - _currentSlideY) < 0.2) _currentSlideY = targetSlideY;
            SlideTransform.Y = _currentSlideY;
        }
    }

    private void LayoutFrame()
    {
        if (_slots.Count == 0) return;

        double baseSize = _settings.DockIconSize;
        double? pointerRelCenter = _hoverAnimator.EffectivePointerX;
        double effectiveScale = 1.0 + (_settings.DockMagnificationScale - 1.0) * _hoverAnimator.HoverAmount;

        var layout = DockMagnificationCalculator.ComputeCenteredFull(
            _slots.Count,
            baseSize,
            IconGapDip,
            _dividerAfterIndices,
            DividerWidthDip,
            pointerRelCenter,
            _settings.DockMagnificationEnabled && _hoverAnimator.HoverAmount > 0.003,
            effectiveScale);

        double nowSec = _clock.Elapsed.TotalSeconds;
        double canvasW = IconsCanvas.ActualWidth > 10 ? IconsCanvas.ActualWidth : Width;
        double halfCanvas = canvasW * 0.5;
        double bottomBaseline = IconsCanvas.Height - 10;

        // Animate Squircle capsule clip + zero-layout vector rim at 120fps with ZERO D3D11 redraws & ZERO WPF layout passes!
        _currentCapsuleWidth = Math.Min(canvasW - 4, layout.TotalWidth + CapsuleHorizontalPadDip * 2);
        double capsuleLeft = halfCanvas - _currentCapsuleWidth * 0.5;
        double capsuleH = CapsuleHost.Height;
        double radius = Math.Clamp(baseSize * 0.40, 16, 26);

        CapsuleClipGeo.Rect = new Rect(capsuleLeft, 0, _currentCapsuleWidth, capsuleH);
        CapsuleClipGeo.RadiusX = radius;
        CapsuleClipGeo.RadiusY = radius;
        CapsuleRim.UpdateCapsule(capsuleLeft, _currentCapsuleWidth, capsuleH, radius, _isDarkMode);

        DockSlotVisual? hoveredVisual = null;
        double hoveredIconCenterX = 0;
        double hoveredIconTopY = 0;
        double bestHoverDist = double.MaxValue;
        double? activePointerCanvasX = _pointerActive && _rawPointerX.HasValue
            ? halfCanvas + _rawPointerX.Value
            : null;

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var l = layout.Icons[i];
            double bounceY = _bounceAnimator.SampleOffsetY(slot.Model.Id, nowSec);

            double scale = l.RenderedSize / baseSize;
            double centerCanvasX = halfCanvas + l.CenterX;

            // PURE RenderTransform updates -> ZERO WPF Measure/Arrange layout passes at 120fps!
            slot.IconScale.ScaleX = scale;
            slot.IconScale.ScaleY = scale;
            slot.SlotTranslate.X = centerCanvasX - baseSize * 0.5;
            slot.SlotTranslate.Y = bottomBaseline - baseSize - bounceY;

            if (activePointerCanvasX.HasValue)
            {
                double dist = Math.Abs(centerCanvasX - activePointerCanvasX.Value);
                if (dist <= l.RenderedSize * 0.52 && dist < bestHoverDist)
                {
                    bestHoverDist = dist;
                    hoveredVisual = slot;
                    hoveredIconCenterX = centerCanvasX;
                    hoveredIconTopY = bottomBaseline - l.RenderedSize - bounceY;
                }
            }
        }

        double divTop = bottomBaseline - baseSize * 0.86;
        for (int d = 0; d < _dividers.Count && d < layout.DividerCentersX.Count; d++)
        {
            var (_, translate) = _dividers[d];
            translate.X = halfCanvas + layout.DividerCentersX[d] - 0.5;
            translate.Y = divTop;
        }

        if (_pointerActive && hoveredVisual is not null && !_contextMenu.IsOpen)
        {
            _hoveredItemId = hoveredVisual.Model.Id;
            ShowOrUpdateInlineTooltip(hoveredVisual.Model.Title, hoveredIconCenterX, hoveredIconTopY, canvasW);
        }
        else if (_hoveredItemId is not null || InlineTooltipAnchor.Opacity > 0.01)
        {
            _hoveredItemId = null;
            HideInlineTooltip();
        }
    }

    private void ShowOrUpdateInlineTooltip(string text, double iconCenterX, double iconTopY, double canvasW)
    {
        if (!string.Equals(_lastTooltipText, text, StringComparison.Ordinal))
        {
            _lastTooltipText = text;
            InlineTooltipText.Text = text;
        }

        // Fixed 320-DIP centering wrapper -> tooltip center & downward arrow are mathematically guaranteed at iconCenterX!
        const double wrapperHalfW = 160.0;
        double targetX = iconCenterX - wrapperHalfW;
        double targetY = Math.Max(2, iconTopY - 36.0);
        InlineTooltipTranslate.X = Math.Round(targetX);
        InlineTooltipTranslate.Y = Math.Round(targetY);

        if (InlineTooltipAnchor.Opacity < 0.99)
        {
            InlineTooltipAnchor.Opacity = 1.0;
        }
    }

    private void HideInlineTooltip()
    {
        if (InlineTooltipAnchor.Opacity > 0.01)
        {
            InlineTooltipAnchor.Opacity = 0.0;
        }
    }

    private void UpdateCapsuleHeight()
    {
        CapsuleHost.Height = _settings.DockIconSize + 18;
    }

    private void HandleItemClick(DockItemModel model, UIElement? anchor = null)
    {
        _bounceAnimator.TriggerBounce(model.Id, _clock.Elapsed.TotalSeconds, hops: model.IsRunning ? 1 : 2);
        _layoutDirty = true;

        switch (model.Kind)
        {
            case DockItemKind.Launchpad:
                LaunchpadRequested?.Invoke();
                return;
            case DockItemKind.RecycleBin:
                _recycleBinService.Open();
                return;
            case DockItemKind.Explorer:
                if (model.OpenWindowHandles.Count > 0) ActivateOrAnimateRestore(model.OpenWindowHandles[0], anchor);
                else StartProcess("explorer.exe", "shell:MyComputerFolder");
                return;
            case DockItemKind.Folder:
                if (!string.IsNullOrWhiteSpace(model.TargetPath))
                {
                    if (anchor is not null)
                    {
                        HideInlineTooltip();
                        _folderStackPopup.ShowForFolder(anchor, model.Title, model.TargetPath);
                    }
                    else
                    {
                        StartProcess("explorer.exe", model.TargetPath);
                    }
                }
                return;
            case DockItemKind.MinimizedWindow:
                if (model.OpenWindowHandles.Count > 0) ActivateOrAnimateRestore(model.OpenWindowHandles[0], anchor);
                return;
            case DockItemKind.Application:
                if (model.OpenWindowHandles.Count > 0)
                {
                    ActivateOrAnimateRestore(model.OpenWindowHandles[0], anchor);
                }
                else if (model.IsRunning && !string.IsNullOrWhiteSpace(model.TargetPath) && ActivateTrayAppRequested is not null)
                {
                    // App is running in system tray (e.g. WeChat closed with X): restore existing tray instance instead of spawning a new window!
                    ActivateTrayAppRequested.Invoke(model.TargetPath);
                }
                else if (!string.IsNullOrWhiteSpace(model.TargetPath))
                {
                    StartProcess(model.TargetPath, null);
                }
                return;
        }
    }

    private static void ActivateOrAnimateRestore(nint hwnd, UIElement? anchor)
    {
        _ = anchor;
        WindowTracker.ActivateWindow(hwnd);
    }

    private void ShowContextMenu(UIElement anchor, DockItemModel model)
    {
        HideInlineTooltip();
        var entries = new List<PopupMenuEntry>();

        if (model.Kind == DockItemKind.RecycleBin)
        {
            entries.Add(new("打开回收站", OnClick: () => _recycleBinService.Open()));
            entries.Add(new("清倒回收站", IsEnabled: !model.IsRecycleBinEmpty, OnClick: () => _recycleBinService.Empty(new WindowInteropHelper(this).Handle)));
        }
        else if (model.Kind == DockItemKind.Application)
        {
            if (model.OpenWindowHandles.Count > 0)
            {
                for (int i = 0; i < model.OpenWindowHandles.Count; i++)
                {
                    nint h = model.OpenWindowHandles[i];
                    entries.Add(new($"{model.Title} — 窗口 {i + 1}", OnClick: () => WindowTracker.ActivateWindow(h)));
                }
                entries.Add(new(string.Empty, IsSeparator: true));
            }

            if (!string.IsNullOrWhiteSpace(model.TargetPath))
            {
                entries.Add(new("新建窗口", OnClick: () => StartProcess(model.TargetPath, null)));
                entries.Add(model.IsPinned
                    ? new("从程序坞中移除", OnClick: () => UnpinApp(model.TargetPath))
                    : new("在程序坞中保留", OnClick: () => PinApp(model.TargetPath)));
                if (!model.IsPinned && !model.IsRunning)
                {
                    entries.Add(new("从最近使用中移除", OnClick: () => RemoveRecentAppRequested?.Invoke(model.TargetPath)));
                }
                entries.Add(new("在资源管理器中显示", OnClick: () => StartProcess("explorer.exe", $"/select,\"{model.TargetPath}\"")));
            }

            if (model.OpenWindowHandles.Count > 0)
            {
                entries.Add(new(string.Empty, IsSeparator: true));
                entries.Add(new("退出", "Alt+F4", OnClick: () => CloseAppWindows(model.OpenWindowHandles)));
            }
        }
        else if (model.Kind == DockItemKind.Folder && !string.IsNullOrWhiteSpace(model.TargetPath))
        {
            entries.Add(new("在资源管理器中打开", OnClick: () => StartProcess("explorer.exe", model.TargetPath)));
            entries.Add(new("从程序坞中移除", OnClick: () => UnpinFolder(model.TargetPath)));
        }
        else
        {
            entries.Add(new(model.Title, OnClick: () => HandleItemClick(model)));
        }

        _contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        _contextMenu.PlacementTarget = anchor;
        _contextMenu.VerticalOffset = -8;
        _contextMenu.Populate(entries, _isDarkMode);
        _contextMenu.IsOpen = true;
    }

    private void OnDockDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDockDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (dropped is null) return;

        foreach (var path in dropped)
        {
            if (Directory.Exists(path)) PinFolder(path);
            else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) PinApp(path);
        }
    }

    private void PinApp(string path)
    {
        if (_settings.PinnedAppPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        SettingsUpdated?.Invoke(_settings with { PinnedAppPaths = [.. _settings.PinnedAppPaths, path] });
    }

    private void UnpinApp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        SettingsUpdated?.Invoke(_settings with { PinnedAppPaths = _settings.PinnedAppPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() });
    }

    private void PinFolder(string path)
    {
        if (_settings.PinnedFolderPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        SettingsUpdated?.Invoke(_settings with { PinnedFolderPaths = [.. _settings.PinnedFolderPaths, path] });
    }

    private void UnpinFolder(string path)
    {
        SettingsUpdated?.Invoke(_settings with { PinnedFolderPaths = _settings.PinnedFolderPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() });
    }

    private static void CloseAppWindows(IReadOnlyList<nint> handles)
    {
        var dispatcher = new TahoeDesk.Core.MenuEngine.MenuCommandDispatcher();
        foreach (var h in handles)
        {
            dispatcher.Dispatch(h, new TahoeDesk.Core.MenuEngine.MenuInvokeAction(TahoeDesk.Core.MenuEngine.MenuActionKind.WindowCommand, WindowOp: "close"));
        }
    }

    private static void LaunchAppWithFiles(string appPath, string[] files)
    {
        string args = string.Join(" ", files.Select(f => $"\"{f}\""));
        StartProcess(appPath, args);
    }

    private static void StartProcess(string fileName, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments ?? string.Empty) { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenNativeWindowsStartMenu()
    {
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_LWIN = 0x5B;

        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_LWIN;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_LWIN;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
