using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GlassShell.Core.Shell;

/// <summary>
/// Manages Win32 RegisterHotKey registrations (e.g. Alt+Space for Spotlight) with clean disposal.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    private readonly Dictionary<int, (nint Hwnd, Action Callback)> _registrations = new();
    private bool _disposed;

    public static bool TryParseChord(string chord, out uint modifiers, out uint virtualKey)
    {
        modifiers = MOD_NOREPEAT;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(chord)) return false;

        foreach (var rawPart in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "WIN":
                case "META":
                    modifiers |= MOD_WIN;
                    break;
                case "SPACE":
                    virtualKey = VK_SPACE;
                    break;
                default:
                    if (rawPart.Length == 1 && char.IsLetterOrDigit(rawPart[0]))
                    {
                        virtualKey = char.ToUpperInvariant(rawPart[0]);
                    }
                    else
                    {
                        return false;
                    }
                    break;
            }
        }

        return virtualKey != 0 && (modifiers & ~MOD_NOREPEAT) != 0;
    }

    public bool Register(nint hwnd, int id, string chord, Action callback)
    {
        if (_disposed || hwnd == 0) return false;
        if (!TryParseChord(chord, out uint mods, out uint vk))
        {
            mods = MOD_ALT | MOD_NOREPEAT;
            vk = VK_SPACE;
        }

        Unregister(id);
        if (RegisterHotKey(hwnd, id, mods, vk))
        {
            _registrations[id] = (hwnd, callback);
            return true;
        }
        return false;
    }

    public void Unregister(int id)
    {
        if (_registrations.Remove(id, out var entry))
        {
            UnregisterHotKey(entry.Hwnd, id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var (id, entry) in _registrations)
        {
            UnregisterHotKey(entry.Hwnd, id);
        }
        _registrations.Clear();
    }

    public bool ProcessWndProc(int msg, nint wParam)
    {
        if (msg != WM_HOTKEY) return false;
        int id = unchecked((int)wParam);
        if (_registrations.TryGetValue(id, out var entry))
        {
            entry.Callback();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (id, entry) in _registrations)
        {
            UnregisterHotKey(entry.Hwnd, id);
        }
        _registrations.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
