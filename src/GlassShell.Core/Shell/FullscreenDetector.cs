using System;
using System.Collections.Generic;

namespace GlassShell.Core.Shell;

public static class FullscreenDetector
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow"
    };

    public static bool IsFullscreen(ForegroundWindowInfo window, MonitorContext monitor, bool isD3DFullscreen)
    {
        if (window.Hwnd == 0 || window.IsMinimized || window.IsCloaked)
        {
            return false;
        }

        if (IgnoredClasses.Contains(window.ClassName))
        {
            return false;
        }

        if (isD3DFullscreen)
        {
            return true;
        }

        const int tolerancePx = 2;
        return window.Bounds.Left <= monitor.Bounds.Left + tolerancePx
            && window.Bounds.Top <= monitor.Bounds.Top + tolerancePx
            && window.Bounds.Right >= monitor.Bounds.Right - tolerancePx
            && window.Bounds.Bottom >= monitor.Bounds.Bottom - tolerancePx;
    }
}
