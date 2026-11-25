using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TahoeDesk.UI.Glass;

public static class WindowInteropHelperEx
{
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const nint WS_EX_NOACTIVATE = 0x08000000;

    public static void ApplyShellOverlayStyles(nint hwnd)
    {
        if (hwnd == 0) return;

        nint style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style);
    }

    // Intentionally kept as a no-op: WDA_EXCLUDEFROMCAPTURE hides TahoeDesk's MenuBar/Dock/Popups
    // from Windows Snipping Tool, WeChat/Snipaste screenshots, and remote desktop apps (ToDesk/Sunlogin/RustDesk/RDP).
    // Self-capture glass feedback is prevented directly in LiquidGlass.hlsl (adjacent reflection / frozen pre-popup frame).
    public static void ExcludeFromCapture(nint hwnd)
    {
    }

    public static void ExcludeVisualFromCapture(Visual? visual)
    {
    }

    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch { }

        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
