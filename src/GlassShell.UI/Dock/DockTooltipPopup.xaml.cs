using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Dock;

public partial class DockTooltipPopup : Popup
{
    public DockTooltipPopup()
    {
        InitializeComponent();
        Placement = PlacementMode.Relative;
        Opened += (_, _) => WindowInteropHelperEx.ExcludeVisualFromCapture(Child);
    }

    public void ShowOrUpdate(UIElement canvasTarget, string text, double iconCenterX, double iconTopY)
    {
        LabelText.Text = text;
        PlacementTarget = canvasTarget;

        // Measure desired size so tooltip stays centered horizontally above the growing icon top
        if (Child is FrameworkElement fe)
        {
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tipW = fe.DesiredSize.Width > 0 ? fe.DesiredSize.Width : 64;
            double tipH = fe.DesiredSize.Height > 0 ? fe.DesiredSize.Height : 28;
            HorizontalOffset = Math.Round(iconCenterX - tipW * 0.5);
            VerticalOffset = Math.Round(iconTopY - tipH - 8.0);
        }

        if (!IsOpen)
        {
            IsOpen = true;
        }
    }

    public void HideTip() => IsOpen = false;
}
