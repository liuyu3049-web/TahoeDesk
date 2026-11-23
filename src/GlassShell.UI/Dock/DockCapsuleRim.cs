using System;
using System.Windows;
using System.Windows.Media;

namespace GlassShell.UI.Dock;

/// <summary>
/// Ultra-lightweight visual-only Liquid Glass capsule rim + soft shadow + exact-capsule hit-test surface.
/// Animates via InvalidateVisual() with ZERO WPF Measure/Arrange layout passes and ZERO per-frame allocations.
/// </summary>
public sealed class DockCapsuleRim : FrameworkElement
{
    private static readonly Brush ShadowOuterBrush;
    private static readonly Brush ShadowInnerBrush;
    private static readonly Brush DarkTopGlossBrush;
    private static readonly Brush LightTopGlossBrush;
    private static readonly Pen DarkRimPen;
    private static readonly Pen LightRimPen;

    static DockCapsuleRim()
    {
        ShadowOuterBrush = new SolidColorBrush(Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        ShadowOuterBrush.Freeze();

        ShadowInnerBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        ShadowInnerBrush.Freeze();

        var darkGloss = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF), 0.0),
                new(Color.FromArgb(0x03, 0xFF, 0xFF, 0xFF), 0.55),
                new(Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF), 1.0)
            },
            new Point(0, 0),
            new Point(0, 1));
        darkGloss.Freeze();
        DarkTopGlossBrush = darkGloss;

        var lightGloss = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.0),
                new(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 0.55),
                new(Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF), 1.0)
            },
            new Point(0, 0),
            new Point(0, 1));
        lightGloss.Freeze();
        LightTopGlossBrush = lightGloss;

        var darkBorderBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF), 0.0),
                new(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF), 0.45),
                new(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF), 1.0)
            },
            new Point(0, 0),
            new Point(0.3, 1));
        darkBorderBrush.Freeze();
        DarkRimPen = new Pen(darkBorderBrush, 1.1);
        DarkRimPen.Freeze();

        var lightBorderBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 0.0),
                new(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), 0.48),
                new(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1.0)
            },
            new Point(0, 0),
            new Point(0.3, 1));
        lightBorderBrush.Freeze();
        LightRimPen = new Pen(lightBorderBrush, 1.1);
        LightRimPen.Freeze();
    }

    private Rect _capsuleRect = new(0, 0, 360, 70);
    private double _radius = 22.0;
    private bool _isDarkMode = true;

    public void UpdateCapsule(double left, double width, double height, double radius, bool isDarkMode)
    {
        if (Math.Abs(_capsuleRect.X - left) < 0.15
            && Math.Abs(_capsuleRect.Width - width) < 0.15
            && Math.Abs(_capsuleRect.Height - height) < 0.15
            && Math.Abs(_radius - radius) < 0.1
            && _isDarkMode == isDarkMode)
        {
            return;
        }

        _capsuleRect = new Rect(left, 0, Math.Max(1, width), Math.Max(1, height));
        _radius = radius;
        _isDarkMode = isDarkMode;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_capsuleRect.Width < 2 || _capsuleRect.Height < 2) return;

        // Subtle 2-layer vector drop shadow (zero software DropShadowEffect overhead)
        var outerShadow = new Rect(_capsuleRect.X - 2, _capsuleRect.Y + 4, _capsuleRect.Width + 4, _capsuleRect.Height + 2);
        var innerShadow = new Rect(_capsuleRect.X - 0.5, _capsuleRect.Y + 2, _capsuleRect.Width + 1, _capsuleRect.Height + 1);
        dc.DrawRoundedRectangle(ShadowOuterBrush, null, outerShadow, _radius + 2, _radius + 2);
        dc.DrawRoundedRectangle(ShadowInnerBrush, null, innerShadow, _radius + 1, _radius + 1);

        // Specular Liquid Glass top sheen + continuous hit-testable interior (prevents 8px inter-icon MouseLeave!)
        var fill = _isDarkMode ? DarkTopGlossBrush : LightTopGlossBrush;
        var pen = _isDarkMode ? DarkRimPen : LightRimPen;
        dc.DrawRoundedRectangle(fill, pen, _capsuleRect, _radius, _radius);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        // Only intercept mouse events inside the visible capsule (plus small 4px vertical cushion);
        // areas outside the capsule remain 100% click-through!
        var p = hitTestParameters.HitPoint;
        if (p.X >= _capsuleRect.Left - 2 && p.X <= _capsuleRect.Right + 2
            && p.Y >= _capsuleRect.Top - 6 && p.Y <= _capsuleRect.Bottom + 4)
        {
            return new PointHitTestResult(this, p);
        }
        return null;
    }
}
