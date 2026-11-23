using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GlassShell.Core.MenuEngine;

public sealed class MenuCommandDispatcher
{
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public bool Dispatch(nint targetHwnd, MenuInvokeAction? action)
    {
        if (action is null) return false;

        try
        {
            if (targetHwnd != 0 && IsWindow(targetHwnd))
            {
                SetForegroundWindow(targetHwnd);
            }

            return action.Kind switch
            {
                MenuActionKind.Win32Command => targetHwnd != 0 && PostMessageW(targetHwnd, WM_COMMAND, (nuint)action.CommandId, 0),
                MenuActionKind.UiaInvoke => StaticUiaMenuExtractor.TryInvokeUiaPattern(action.UiaElementRef),
                MenuActionKind.WindowCommand => ExecuteWindowOp(targetHwnd, action.WindowOp),
                MenuActionKind.KeyboardShortcut => SendShortcutChord(action.Shortcut),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool ExecuteWindowOp(nint hwnd, string? op)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || string.IsNullOrWhiteSpace(op)) return false;

        switch (op.ToLowerInvariant())
        {
            case "minimize":
                return ShowWindow(hwnd, SW_MINIMIZE);
            case "maximize":
                return ShowWindow(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
            case "close":
                return PostMessageW(hwnd, WM_CLOSE, 0, 0);
            case "snapleft":
                return SendShortcutChord("Win+Left");
            case "snapright":
                return SendShortcutChord("Win+Right");
            case "toggletopmost":
                bool isTopMost = (GetWindowLongW(hwnd, -20) & 0x00000008) != 0;
                return SetWindowPos(hwnd, isTopMost ? (nint)(-2) : (nint)(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
            default:
                return false;
        }
    }

    private static bool SendShortcutChord(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return false;
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keys = new List<ushort>();

        foreach (var part in parts)
        {
            if (TryMapVirtualKey(part, out ushort vk))
            {
                keys.Add(vk);
            }
            else
            {
                return false;
            }
        }

        if (keys.Count == 0) return false;

        var inputs = new INPUT[keys.Count * 2];
        int idx = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            inputs[idx++] = CreateKeyInput(keys[i], keyUp: false);
        }
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            inputs[idx++] = CreateKeyInput(keys[i], keyUp: true);
        }

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static INPUT CreateKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
            }
        }
    };

    private static bool TryMapVirtualKey(string token, out ushort vk)
    {
        vk = token.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" => 0x12,
            "WIN" => 0x5B,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "DELETE" or "DEL" => 0x2E,
            "BACKSPACE" => 0x08,
            "PLUS" or "=" => 0xBB,
            "MINUS" or "-" => 0xBD,
            "," => 0xBC,
            "." => 0xBE,
            "/" => 0xBF,
            "`" => 0xC0,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => 0
        };

        if (vk != 0) return true;
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                return true;
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
