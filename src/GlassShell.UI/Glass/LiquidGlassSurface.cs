using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GlassShell.Core.Config;
using GlassShell.Core.Glass;
using GlassShell.Core.Shell;

namespace GlassShell.UI.Glass;

public sealed class LiquidGlassSurface : FrameworkElement, IDisposable
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShadowPaddingProperty =
        DependencyProperty.Register(nameof(ShadowPadding), typeof(double), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OpacityPresetProperty =
        DependencyProperty.Register(nameof(OpacityPreset), typeof(GlassOpacityPreset), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(GlassOpacityPreset.Clear, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkModeProperty =
        DependencyProperty.Register(nameof(IsDarkMode), typeof(bool), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsOverlayCardProperty =
        DependencyProperty.Register(nameof(IsOverlayCard), typeof(bool), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveBoxWidthProperty =
        DependencyProperty.Register(nameof(ActiveBoxWidth), typeof(double), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty IsClippedBackdropProperty =
        DependencyProperty.Register(nameof(IsClippedBackdrop), typeof(bool), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly SolidColorBrush DarkOverlayFill;
    private static readonly SolidColorBrush LightOverlayFill;
    private static readonly SolidColorBrush DarkBarFill;
    private static readonly SolidColorBrush LightBarFill;
    private static readonly Pen DarkFallbackBorder;
    private static readonly Pen LightFallbackBorder;

    static LiquidGlassSurface()
    {
        DarkOverlayFill = new SolidColorBrush(Color.FromArgb(215, 28, 30, 38));
        LightOverlayFill = new SolidColorBrush(Color.FromArgb(228, 246, 248, 253));
        DarkBarFill = new SolidColorBrush(Color.FromArgb(156, 28, 30, 38));
        LightBarFill = new SolidColorBrush(Color.FromArgb(178, 246, 248, 253));
        DarkOverlayFill.Freeze();
        LightOverlayFill.Freeze();
        DarkBarFill.Freeze();
        LightBarFill.Freeze();

        var darkGrad = new LinearGradientBrush(Color.FromArgb(110, 255, 255, 255), Color.FromArgb(30, 255, 255, 255), new Point(0, 0), new Point(1, 1));
        var lightGrad = new LinearGradientBrush(Color.FromArgb(190, 255, 255, 255), Color.FromArgb(30, 255, 255, 255), new Point(0, 0), new Point(1, 1));
        darkGrad.Freeze();
        lightGrad.Freeze();

        DarkFallbackBorder = new Pen(darkGrad, 1.0);
        LightFallbackBorder = new Pen(lightGrad, 1.0);
        DarkFallbackBorder.Freeze();
        LightFallbackBorder.Freeze();
    }

    private static int _surfaceStaggerCounter;

    private readonly D3DImage _d3dImage = new();
    private readonly System.Diagnostics.Stopwatch _throttleClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly long _staggerOffsetMs;
    private DxgiBackdropSampler? _attachedSampler;
    private LiquidGlassRenderer? _renderer;
    private nint _boundSurfacePtr;
    private bool _hasValidFrame;
    private bool _isHooked;
    private long _lastRenderMs = -200;
    private long _firstMissMs;
    private const long MinBackdropRefreshIntervalMs = 66; // ~15 Hz staggered cap prevents AllowsTransparency PCIe readback stutter
    private const long TransientMissGraceMs = 1500; // Keep last valid glass frame across brief DXGI misses (prevents Dock flicker!)

    public LiquidGlassSurface()
    {
        _staggerOffsetMs = (System.Threading.Interlocked.Increment(ref _surfaceStaggerCounter) % 4) * 17L;
        IsHitTestVisible = false;
        Loaded += (_, _) => SyncRenderingHook();
        Unloaded += (_, _) =>
        {
            UnhookRendering();
            if (IsOverlayCard)
            {
                DetachBackBuffer();
                _renderer?.ReleaseRenderTargets();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            SyncRenderingHook();
            if (!IsVisible && IsOverlayCard)
            {
                // Release VRAM immediately when popup/card closes (0 bytes GPU memory when idle!)
                DetachBackBuffer();
                _renderer?.ReleaseRenderTargets();
            }
        };
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double ShadowPadding
    {
        get => (double)GetValue(ShadowPaddingProperty);
        set => SetValue(ShadowPaddingProperty, value);
    }

    public GlassOpacityPreset OpacityPreset
    {
        get => (GlassOpacityPreset)GetValue(OpacityPresetProperty);
        set => SetValue(OpacityPresetProperty, value);
    }

    public bool IsDarkMode
    {
        get => (bool)GetValue(IsDarkModeProperty);
        set => SetValue(IsDarkModeProperty, value);
    }

    public bool IsOverlayCard
    {
        get => (bool)GetValue(IsOverlayCardProperty);
        set => SetValue(IsOverlayCardProperty, value);
    }

    public double ActiveBoxWidth
    {
        get => (double)GetValue(ActiveBoxWidthProperty);
        set => SetValue(ActiveBoxWidthProperty, value);
    }

    public bool IsClippedBackdrop
    {
        get => (bool)GetValue(IsClippedBackdropProperty);
        set => SetValue(IsClippedBackdropProperty, value);
    }

    public void AttachSampler(DxgiBackdropSampler sampler)
    {
        if (ReferenceEquals(_attachedSampler, sampler) && _renderer is not null)
        {
            SyncRenderingHook();
            return;
        }

        DetachBackBuffer();
        _renderer?.Dispose();
        _attachedSampler = sampler;
        _renderer = new LiquidGlassRenderer(sampler);
        SyncRenderingHook();
    }

    private void SyncRenderingHook()
    {
        bool shouldHook = IsLoaded && IsVisible && _renderer is not null;
        if (shouldHook && !_isHooked)
        {
            _isHooked = true;
            _lastRenderMs = -200;
            CompositionTarget.Rendering += OnRendering;
        }
        else if (!shouldHook && _isHooked)
        {
            UnhookRendering();
        }
    }

    private void UnhookRendering()
    {
        if (_isHooked)
        {
            _isHooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void DetachBackBuffer()
    {
        if (_boundSurfacePtr != 0 || _hasValidFrame)
        {
            _d3dImage.Lock();
            try
            {
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, nint.Zero);
            }
            catch { }
            finally
            {
                _d3dImage.Unlock();
            }

            _boundSurfacePtr = 0;
            _hasValidFrame = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || _renderer is null || ActualWidth < 4 || ActualHeight < 4) return;
        if (!_d3dImage.IsFrontBufferAvailable) return;

        long nowMs = _throttleClock.ElapsedMilliseconds + _staggerOffsetMs;
        if (_hasValidFrame && (nowMs - _lastRenderMs) < MinBackdropRefreshIntervalMs)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;

        var dpiMatrix = source.CompositionTarget.TransformToDevice;
        Point topLeftPx = PointToScreen(new Point(0, 0));
        int widthPx = Math.Max(4, (int)Math.Round(ActualWidth * dpiMatrix.M11));
        int heightPx = Math.Max(4, (int)Math.Round(ActualHeight * dpiMatrix.M22));

        var rectPx = new ScreenRect(
            (int)Math.Round(topLeftPx.X),
            (int)Math.Round(topLeftPx.Y),
            (int)Math.Round(topLeftPx.X) + widthPx,
            (int)Math.Round(topLeftPx.Y) + heightPx);

        float radiusPx = (float)(CornerRadius * dpiMatrix.M11);
        float shadowPadPx = IsClippedBackdrop ? -1f : (float)(ShadowPadding * dpiMatrix.M11);
        float activeBoxWidthPx = ActiveBoxWidth > 1.0 ? (float)(ActiveBoxWidth * dpiMatrix.M11) : 0f;
        var renderParams = GlassRenderParams.FromPreset(OpacityPreset, IsDarkMode, radiusPx, IsOverlayCard);

        if (_renderer.RenderSurface(rectPx, renderParams, shadowPadPx, activeBoxWidthPx, DetachBackBuffer, out bool frameUpdated)
            && _renderer.SharedD3D9SurfacePtr != 0)
        {
            _lastRenderMs = nowMs;
            _firstMissMs = 0;
            nint ptr = _renderer.SharedD3D9SurfacePtr;
            if (frameUpdated || _boundSurfacePtr != ptr || !_hasValidFrame)
            {
                _d3dImage.Lock();
                try
                {
                    if (_boundSurfacePtr != ptr)
                    {
                        _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, ptr, enableSoftwareFallback: true);
                        _boundSurfacePtr = ptr;
                    }
                    if (_d3dImage.PixelWidth > 0 && _d3dImage.PixelHeight > 0)
                    {
                        _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _d3dImage.PixelWidth, _d3dImage.PixelHeight));
                        _hasValidFrame = true;
                    }
                }
                finally
                {
                    _d3dImage.Unlock();
                }

                InvalidateVisual();
            }
        }
        else if (_hasValidFrame)
        {
            // Retain the last valid Liquid Glass frame across transient DXGI timeouts/AccessLost
            // so the Dock NEVER flashes to a gray fallback rectangle!
            if (_firstMissMs == 0)
            {
                _firstMissMs = nowMs;
            }
            else if ((nowMs - _firstMissMs) >= TransientMissGraceMs)
            {
                DetachBackBuffer();
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 2 || ActualHeight < 2) return;

        var fullRect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_hasValidFrame && _d3dImage.PixelWidth > 0)
        {
            dc.DrawImage(_d3dImage, fullRect);
            return;
        }

        double pad = Math.Min(ShadowPadding, Math.Min(ActualWidth, ActualHeight) * 0.25);
        var inner = new Rect(pad, pad, Math.Max(1, ActualWidth - pad * 2), Math.Max(1, ActualHeight - pad * 2));
        var fill = IsOverlayCard
            ? (IsDarkMode ? DarkOverlayFill : LightOverlayFill)
            : (IsDarkMode ? DarkBarFill : LightBarFill);
        var border = IsDarkMode ? DarkFallbackBorder : LightFallbackBorder;

        dc.DrawRoundedRectangle(fill, border, inner, CornerRadius, CornerRadius);
    }

    public void Dispose()
    {
        UnhookRendering();
        DetachBackBuffer();
        _renderer?.Dispose();
        _renderer = null;
        _attachedSampler = null;
    }
}
