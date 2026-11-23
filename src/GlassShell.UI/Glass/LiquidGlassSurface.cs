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
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OpacityPresetProperty =
        DependencyProperty.Register(nameof(OpacityPreset), typeof(GlassOpacityPreset), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(GlassOpacityPreset.Clear, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkModeProperty =
        DependencyProperty.Register(nameof(IsDarkMode), typeof(bool), typeof(LiquidGlassSurface),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly D3DImage _d3dImage = new();
    private LiquidGlassRenderer? _renderer;
    private nint _boundSurfacePtr;
    private bool _hasValidFrame;

    public LiquidGlassSurface()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
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

    public void AttachSampler(DxgiBackdropSampler sampler)
    {
        _renderer?.Dispose();
        _renderer = new LiquidGlassRenderer(sampler);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || _renderer is null || ActualWidth < 4 || ActualHeight < 4) return;

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
        float shadowPadPx = (float)(ShadowPadding * dpiMatrix.M11);
        var renderParams = GlassRenderParams.FromPreset(OpacityPreset, IsDarkMode, radiusPx);

        if (_d3dImage.IsFrontBufferAvailable
            && _renderer.RenderSurface(rectPx, renderParams, shadowPadPx)
            && _renderer.SharedD3D9SurfacePtr != 0)
        {
            _d3dImage.Lock();
            try
            {
                nint ptr = _renderer.SharedD3D9SurfacePtr;
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
        else if (_hasValidFrame)
        {
            _hasValidFrame = false;
            _boundSurfacePtr = 0;
            InvalidateVisual();
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

        // Graceful translucent Squircle fallback when DXGI is degraded (e.g., lock screen/RDP)
        double pad = Math.Min(ShadowPadding, Math.Min(ActualWidth, ActualHeight) * 0.25);
        var inner = new Rect(pad, pad, Math.Max(1, ActualWidth - pad * 2), Math.Max(1, ActualHeight - pad * 2));
        var fillColor = IsDarkMode
            ? Color.FromArgb(148, 28, 30, 38)
            : Color.FromArgb(172, 245, 247, 252);
        var borderBrush = new LinearGradientBrush(
            Color.FromArgb(IsDarkMode ? (byte)130 : (byte)200, 255, 255, 255),
            Color.FromArgb(35, 255, 255, 255),
            new Point(0, 0),
            new Point(1, 1));

        dc.DrawRoundedRectangle(new SolidColorBrush(fillColor), new Pen(borderBrush, 1.0), inner, CornerRadius, CornerRadius);
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
    }
}
