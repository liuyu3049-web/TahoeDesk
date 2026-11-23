using System.Windows;
using System.Windows.Controls.Primitives;

namespace GlassShell.UI.Dock;

public partial class DockTooltipPopup : Popup
{
    public DockTooltipPopup()
    {
        InitializeComponent();
    }

    public void ShowFor(UIElement target, string text)
    {
        LabelText.Text = text;
        PlacementTarget = target;
        HorizontalOffset = 0;
        VerticalOffset = -8;
        IsOpen = true;
    }

    public void HideTip() => IsOpen = false;
}
