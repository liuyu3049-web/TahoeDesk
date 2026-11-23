using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Dock;

public partial class DockTooltipPopup : Popup
{
    private string? _lastText;
    private double _cachedW = 64;
    private double _cachedH = 28;

    public DockTooltipPopup()
    {
        InitializeComponent();
        Placement = PlacementMode.Relative;
        Opened += (_, _) => WindowInteropHelperEx.ExcludeVisualFromCapture(Child);
    }

    public void ShowOrUpdate(UIElement canvasTarget, string text, double iconCenterX, double iconTopY)
    {
        if (!string.Equals(_lastText, text, StringComparison.Ordinal))
        {
            _lastText = text;
            LabelText.Text = text;
            if (Child is FrameworkElement fe)
            {
                fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _cachedW = fe.DesiredSize.Width > 0 ? fe.DesiredSize.Width : 64;
                _cachedH = fe.DesiredSize.Height > 0 ? fe.DesiredSize.Height : 28;
            }
        }

        if (!ReferenceEquals(PlacementTarget, canvasTarget))
        {
            PlacementTarget = canvasTarget;
        }

        double targetX = Math.Round(iconCenterX - _cachedW * 0.5);
        double targetY = Math.Round(iconTopY - _cachedH - 8.0);

        if (Math.Abs(HorizontalOffset - targetX) > 0.5) HorizontalOffset = targetX;
        if (Math.Abs(VerticalOffset - targetY) > 0.5) VerticalOffset = targetY;

        if (!IsOpen)
        {
            IsOpen = true;
        }
    }

    public void HideTip() => IsOpen = false;
}
