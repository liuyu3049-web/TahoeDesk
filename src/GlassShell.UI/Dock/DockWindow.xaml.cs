using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GlassShell.Core.Config;
using GlassShell.Core.Discovery;
using GlassShell.Core.DockPhysics;
using GlassShell.Core.Shell;
using GlassShell.UI.Glass;
using GlassShell.UI.MenuBar;

namespace GlassShell.UI.Dock;

public partial class DockWindow : Window
{
    private sealed class DockSlotVisual
    {
        public required DockItemModel Model { get; init; }
        public required Grid Container { get; init; }
        public required Border IconFrame { get; init; }
        public required Ellipse RunningDot { get; init; }
    }

    private const double IconGapDip = 8.0;
    private const double CapsuleHorizontalPadDip = 14.0;
    private const double CapsuleShadowPadDip = 16.0;

    private readonly MonitorContext _monitor;
    private readonly IconExtractor _iconExtractor = new();
    private readonly RecycleBinService _recycleBinService = new();
    private readonly SpringBounceAnimator _bounceAnimator = new();
    private readonly DockHoverAnimator _hoverAnimator = new();
    private readonly DockAutoHideController _autoHideController = new();
    private readonly DxgiBackdropSampler _sampler;
    private readonly DockTooltipPopup _tooltip = new();
    private readonly LiquidGlassMenuPopup _contextMenu = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly List<DockSlotVisual> _slots = [];
    private readonly List<Border> _dividers = [];
    private readonly List<int> _dividerAfterIndices = [];

    private GlassSettings _settings;
    private bool _isDarkMode;
    private IReadOnlyList<DockItemModel> _lastItems = [];
    private double? _rawPointerX;
    private bool _pointerActive;
    private double _lastFrameSeconds;
    private double? _bottomEdgeEnterMs;
    private double _currentSlideY;
    private bool _yieldForTray;
    private bool _hasBottomOverlap;
    private Point _dragStartPoint;
    private DockItemModel? _draggingItem;
    private string? _hoveredItemId;

    public DockWindow(
        MonitorContext monitor,
        DxgiBackdropSampler sampler,
        GlassSettings settings,
        bool isDarkMode)
    {
        InitializeComponent();
        _monitor = monitor;
        _sampler = sampler;
        _settings = settings;
        _isDarkMode = isDarkMode;

        CapsuleGlass.AttachSampler(sampler);
        ApplySettings(settings, isDarkMode);
        PositionToMonitor(monitor);

        SourceInitialized += (_, _) => WindowInteropHelperEx.ApplyShellOverlayStyles(new WindowInteropHelper(this).Handle);
        IconsCanvas.MouseMove += OnIconsMouseMove;
        IconsCanvas.MouseLeave += OnIconsMouseLeave;
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

    public void ApplySettings(GlassSettings settings, bool isDarkMode)
    {
        _settings = settings;
        _isDarkMode = isDarkMode;
        CapsuleGlass.OpacityPreset = settings.OpacityPreset;
        CapsuleGlass.IsDarkMode = isDarkMode;
        _contextMenu.ConfigureVisuals(_sampler, settings.OpacityPreset, isDarkMode);
        UpdateCapsuleHeight();
    }

    public void SetYieldForTray(bool yield) => _yieldForTray = yield;
    public void SetBottomOverlap(bool overlap) => _hasBottomOverlap = overlap;
    public void DismissPopups() => Dispatcher.InvokeAsync(() => _contextMenu.IsOpen = false);

    public void PositionToMonitor(MonitorContext monitor)
    {
        Width = monitor.WidthDip;
        Left = monitor.Bounds.Left / monitor.DpiScale;
        Top = (monitor.Bounds.Bottom / monitor.DpiScale) - Height;
    }

    public void UpdateItems(IReadOnlyList<DockItemModel> items)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DockModelCoordinator.AreEquivalent(_lastItems, items))
            {
                return;
            }
            _lastItems = items;
            RebuildVisualSlots(items);
        });
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
                var divider = new Border
                {
                    Width = 1,
                    Height = _settings.DockIconSize * 0.72,
                    Background = new SolidColorBrush(_isDarkMode
                        ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0x38, 0x00, 0x00, 0x00)),
                    IsHitTestVisible = false
                };
                _dividers.Add(divider);
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

        UpdateCapsuleHeight();
        LayoutFrame();
    }

    private DockSlotVisual CreateSlotVisual(DockItemModel model)
    {
        UIElement iconContent = BuildIconVisual(model);

        var iconFrame = new Border
        {
            Width = _settings.DockIconSize,
            Height = _settings.DockIconSize,
            Background = Brushes.Transparent,
            Child = iconContent
        };

        var dot = new Ellipse
        {
            Width = 4.5,
            Height = 4.5,
            Fill = new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
            Visibility = model.IsRunning ? Visibility.Visible : Visibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var container = new Grid
        {
            Width = _settings.DockIconSize,
            Height = _settings.DockIconSize + 10,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            AllowDrop = true
        };
        iconFrame.VerticalAlignment = VerticalAlignment.Top;
        container.Children.Add(iconFrame);
        container.Children.Add(dot);

        container.MouseLeftButtonDown += (_, e) =>
        {
            _dragStartPoint = e.GetPosition(this);
            _draggingItem = model;
        };

        container.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggingItem?.Id == model.Id && model.IsPinned && model.Kind == DockItemKind.Application)
            {
                var pos = e.GetPosition(this);
                if (Math.Abs(pos.Y - _dragStartPoint.Y) > 78 && pos.Y < _dragStartPoint.Y)
                {
                    // Dragged out above Dock -> unpin like macOS puff removal
                    UnpinApp(model.TargetPath);
                    _draggingItem = null;
                }
            }
        };

        container.MouseLeftButtonUp += (_, e) =>
        {
            _draggingItem = null;
            e.Handled = true;
            HandleItemClick(model);
        };

        container.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowContextMenu(container, model);
        };

        container.Drop += (_, e) =>
        {
            if (model.Kind == DockItemKind.Application && !string.IsNullOrWhiteSpace(model.TargetPath)
                && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files is { Length: > 0 })
                {
                    e.Handled = true;
                    LaunchAppWithFiles(model.TargetPath, files);
                }
            }
        };

        return new DockSlotVisual
        {
            Model = model,
            Container = container,
            IconFrame = iconFrame,
            RunningDot = dot
        };
    }

    private UIElement BuildIconVisual(DockItemModel model)
    {
        if (model.Kind == DockItemKind.Launchpad)
        {
            return BuildLaunchpadVectorIcon();
        }

        if (model.Kind == DockItemKind.Explorer)
        {
            return BuildExplorerVectorIcon();
        }

        if (model.Kind == DockItemKind.RecycleBin)
        {
            return BuildRecycleBinVectorIcon(model.IsRecycleBinEmpty);
        }

        if (!string.IsNullOrWhiteSpace(model.TargetPath))
        {
            var raw = _iconExtractor.ExtractHighResIcon(model.TargetPath, 128);
            if (raw is not null)
            {
                var bmp = BitmapSource.Create(
                    raw.Width, raw.Height, 96, 96,
                    PixelFormats.Bgra32, null, raw.BgraPixels, raw.Width * 4);
                bmp.Freeze();

                // Pure silhouette icon without square backing plate (per user preference)
                return new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(1)
                };
            }
        }

        return new System.Windows.Shapes.Ellipse
        {
            Fill = new LinearGradientBrush(Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0x0A, 0x84, 0xFF), 45),
            Margin = new Thickness(4)
        };
    }

    private static UIElement BuildExplorerVectorIcon()
    {
        // Transparent-background macOS-style layered blue folder silhouette (no square box)
        var grid = new Grid { Margin = new Thickness(2) };
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
        // Transparent-background floating 3x3 colorful macOS Launchpad dots silhouette (no square box)
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(4) };
        Color[] palette =
        [
            Color.FromRgb(0xFF, 0x5E, 0x57), Color.FromRgb(0xFF, 0xBD, 0x2E), Color.FromRgb(0x28, 0xCD, 0x41),
            Color.FromRgb(0x5A, 0xC8, 0xFA), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x58, 0x56, 0xD6),
            Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0xFF, 0x2D, 0x55), Color.FromRgb(0xFF, 0x9F, 0x0A)
        ];
        foreach (var c in palette)
        {
            grid.Children.Add(new Ellipse
            {
                Margin = new Thickness(2.2),
                Fill = new SolidColorBrush(c)
            });
        }
        return grid;
    }

    private static UIElement BuildRecycleBinVectorIcon(bool isEmpty)
    {
        // Transparent-background translucent metallic mesh trash bin silhouette (no square box)
        var grid = new Grid { Margin = new Thickness(3) };
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

    private void OnIconsMouseMove(object sender, MouseEventArgs e)
    {
        var pt = e.GetPosition(IconsCanvas);
        _rawPointerX = pt.X;
        _pointerActive = true;
    }

    private void OnIconsMouseLeave(object sender, MouseEventArgs e)
    {
        _rawPointerX = null;
        _pointerActive = false;
        _hoveredItemId = null;
        _tooltip.HideTip();
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        double nowSec = _clock.Elapsed.TotalSeconds;
        double dt = _lastFrameSeconds > 0 ? nowSec - _lastFrameSeconds : 0.016;
        _lastFrameSeconds = nowSec;

        _hoverAnimator.Step(_pointerActive, _rawPointerX, dt);
        LayoutFrame();

        bool isAtBottomEdge = false;
        double dwellMs = 0;
        if (GetCursorPos(out POINT cursorPx))
        {
            bool withinMonitorX = cursorPx.X >= _monitor.Bounds.Left && cursorPx.X <= _monitor.Bounds.Right;
            bool nearBottom = cursorPx.Y >= _monitor.Bounds.Bottom - 4 && cursorPx.Y <= _monitor.Bounds.Bottom;
            bool insideRevealedDock = _currentSlideY < 10.0 && cursorPx.Y >= _monitor.Bounds.Bottom - (int)(Height * _monitor.DpiScale);

            if (withinMonitorX && (nearBottom || insideRevealedDock))
            {
                isAtBottomEdge = true;
                double nowMs = _clock.Elapsed.TotalMilliseconds;
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
            isMenuOrDragActive: _contextMenu.IsOpen || _draggingItem is not null);

        double targetSlideY = shouldHide ? 104.0 : 0.0;
        _currentSlideY += (targetSlideY - _currentSlideY) * 0.22;
        if (Math.Abs(targetSlideY - _currentSlideY) < 0.2) _currentSlideY = targetSlideY;
        SlideTransform.Y = _currentSlideY;
    }

    private void LayoutFrame()
    {
        if (_slots.Count == 0) return;

        double baseSize = _settings.DockIconSize;
        double? pointer = _hoverAnimator.EffectivePointerX;
        double effectiveScale = 1.0 + (_settings.DockMagnificationScale - 1.0) * _hoverAnimator.HoverAmount;
        var layouts = DockMagnificationCalculator.Compute(
            _slots.Count,
            baseSize,
            IconGapDip,
            pointer,
            _settings.DockMagnificationEnabled && _hoverAnimator.HoverAmount > 0.003,
            effectiveScale);

        double nowSec = _clock.Elapsed.TotalSeconds;
        double dividerExtraWidth = _dividers.Count * 14.0;
        double totalIconsWidth = DockMagnificationCalculator.ComputeTotalWidth(layouts, IconGapDip) + dividerExtraWidth;

        IconsCanvas.Width = Math.Max(64, totalIconsWidth);
        CapsuleGlass.Width = IconsCanvas.Width + CapsuleHorizontalPadDip * 2 + CapsuleShadowPadDip * 2;

        double xShift = 0;
        int divPtr = 0;
        DockSlotVisual? hoveredVisual = null;
        double bestHoverDist = double.MaxValue;

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var l = layouts[i];
            double bounceY = _bounceAnimator.SampleOffsetY(slot.Model.Id, nowSec);

            double left = (l.CenterX - l.RenderedSize * 0.5) + xShift;
            double bottomBaseline = IconsCanvas.Height - 10;
            double top = bottomBaseline - l.RenderedSize - bounceY;

            slot.IconFrame.Width = l.RenderedSize;
            slot.IconFrame.Height = l.RenderedSize;
            slot.Container.Width = l.RenderedSize;
            slot.Container.Height = l.RenderedSize + 10;

            Canvas.SetLeft(slot.Container, left);
            Canvas.SetTop(slot.Container, top);

            if (pointer.HasValue)
            {
                double dist = Math.Abs((left + l.RenderedSize * 0.5) - pointer.Value);
                if (dist < l.RenderedSize * 0.55 && dist < bestHoverDist)
                {
                    bestHoverDist = dist;
                    hoveredVisual = slot;
                }
            }

            if (divPtr < _dividerAfterIndices.Count && _dividerAfterIndices[divPtr] == i)
            {
                var div = _dividers[divPtr];
                Canvas.SetLeft(div, left + l.RenderedSize + IconGapDip + 4);
                Canvas.SetTop(div, IconsCanvas.Height - 10 - baseSize * 0.86);
                xShift += 14.0;
                divPtr++;
            }
        }

        if (hoveredVisual is not null && !_contextMenu.IsOpen)
        {
            if (_hoveredItemId != hoveredVisual.Model.Id)
            {
                _hoveredItemId = hoveredVisual.Model.Id;
                _tooltip.ShowFor(hoveredVisual.Container, hoveredVisual.Model.Title);
            }
        }
        else if (_hoveredItemId is not null)
        {
            _hoveredItemId = null;
            _tooltip.HideTip();
        }
    }

    private void UpdateCapsuleHeight()
    {
        CapsuleGlass.Height = _settings.DockIconSize + 18 + CapsuleShadowPadDip * 2;
        CapsuleGlass.CornerRadius = Math.Clamp(_settings.DockIconSize * 0.40, 16, 26);
    }

    private void HandleItemClick(DockItemModel model)
    {
        _bounceAnimator.TriggerBounce(model.Id, _clock.Elapsed.TotalSeconds, hops: model.IsRunning ? 1 : 2);

        switch (model.Kind)
        {
            case DockItemKind.Launchpad:
                LaunchpadRequested?.Invoke();
                return;
            case DockItemKind.RecycleBin:
                _recycleBinService.Open();
                return;
            case DockItemKind.Explorer:
                if (model.OpenWindowHandles.Count > 0)
                {
                    WindowTracker.ActivateWindow(model.OpenWindowHandles[0]);
                }
                else
                {
                    StartProcess("explorer.exe", "shell:MyComputerFolder");
                }
                return;
            case DockItemKind.Folder:
                if (!string.IsNullOrWhiteSpace(model.TargetPath))
                {
                    StartProcess("explorer.exe", model.TargetPath);
                }
                return;
            case DockItemKind.Application:
                if (model.OpenWindowHandles.Count > 0)
                {
                    WindowTracker.ActivateWindow(model.OpenWindowHandles[0]);
                }
                else if (!string.IsNullOrWhiteSpace(model.TargetPath))
                {
                    StartProcess(model.TargetPath, null);
                }
                return;
        }
    }

    private void ShowContextMenu(UIElement anchor, DockItemModel model)
    {
        _tooltip.HideTip();
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
            if (Directory.Exists(path))
            {
                PinFolder(path);
            }
            else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                PinApp(path);
            }
        }
    }

    private void PinApp(string path)
    {
        if (_settings.PinnedAppPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        var next = _settings with { PinnedAppPaths = [.. _settings.PinnedAppPaths, path] };
        SettingsUpdated?.Invoke(next);
    }

    private void UnpinApp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var next = _settings with { PinnedAppPaths = _settings.PinnedAppPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() };
        SettingsUpdated?.Invoke(next);
    }

    private void PinFolder(string path)
    {
        if (_settings.PinnedFolderPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        var next = _settings with { PinnedFolderPaths = [.. _settings.PinnedFolderPaths, path] };
        SettingsUpdated?.Invoke(next);
    }

    private void UnpinFolder(string path)
    {
        var next = _settings with { PinnedFolderPaths = _settings.PinnedFolderPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() };
        SettingsUpdated?.Invoke(next);
    }

    private static void CloseAppWindows(IReadOnlyList<nint> handles)
    {
        var dispatcher = new GlassShell.Core.MenuEngine.MenuCommandDispatcher();
        foreach (var h in handles)
        {
            dispatcher.Dispatch(h, new GlassShell.Core.MenuEngine.MenuInvokeAction(GlassShell.Core.MenuEngine.MenuActionKind.WindowCommand, WindowOp: "close"));
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
