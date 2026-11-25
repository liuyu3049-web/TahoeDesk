using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TahoeDesk.UI.Glass;

/// <summary>
/// On-demand global low-level mouse & Escape hook installed ONLY while a TahoeDesk
/// popup menu is open. Required because WS_EX_NOACTIVATE parent windows do not reliably
/// receive WPF mouse-capture dismissal when clicking the desktop or other applications.
/// </summary>
public sealed class OutsideClickDismissHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    private readonly Func<IEnumerable<nint>> _getAllowedHwnds;
    private readonly Action _onDismiss;
    private HookProc? _mouseProc;
    private HookProc? _keyProc;
    private nint _mouseHook;
    private nint _keyHook;

    public OutsideClickDismissHook(Func<IEnumerable<nint>> getAllowedHwnds, Action onDismiss)
    {
        _getAllowedHwnds = getAllowedHwnds;
        _onDismiss = onDismiss;
    }

    public void Start()
    {
        if (_mouseHook != 0) return;
        _mouseProc = MouseHookCallback;
        _keyProc = KeyHookCallback;
        nint mod = GetModuleHandleW(null);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyProc, mod, 0);
    }

    public void Stop()
    {
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
        if (_keyHook != 0)
        {
            UnhookWindowsHookEx(_keyHook);
            _keyHook = 0;
        }
        _mouseProc = null;
        _keyProc = null;
    }

    public void Dispose() => Stop();

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!IsPointInsideAnyAllowedHwnd(info.pt.X, info.pt.Y))
                {
                    _onDismiss();
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private nint KeyHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_KEYDOWN)
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk == VK_ESCAPE)
            {
                _onDismiss();
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideAnyAllowedHwnd(int x, int y)
    {
        foreach (var hwnd in _getAllowedHwnds())
        {
            if (hwnd == 0) continue;
            if (GetWindowRect(hwnd, out RECT rc)
                && x >= rc.Left && x <= rc.Right
                && y >= rc.Top && y <= rc.Bottom)
            {
                return true;
            }
        }
        return false;
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
}
