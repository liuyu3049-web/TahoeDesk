using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TahoeDesk.Core.MenuEngine;

public sealed class Win32GetMenuExtractor : IMenuExtractor
{
    private const uint MIIM_STATE = 0x00000001;
    private const uint MIIM_ID = 0x00000002;
    private const uint MIIM_SUBMENU = 0x00000004;
    private const uint MIIM_FTYPE = 0x00000100;
    private const uint MIIM_STRING = 0x00000040;
    private const uint MFT_SEPARATOR = 0x00000800;
    private const uint MFS_DISABLED = 0x00000003;
    private const uint MFS_CHECKED = 0x00000008;

    public MenuSource Source => MenuSource.Win32GetMenu;

    public Task<IReadOnlyList<MenuNode>?> TryExtractAsync(nint hwnd, string processName, CancellationToken ct)
    {
        if (hwnd == 0 || ct.IsCancellationRequested)
        {
            return Task.FromResult<IReadOnlyList<MenuNode>?>(null);
        }

        return Task.Run<IReadOnlyList<MenuNode>?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            nint hMenu = GetMenu(hwnd);
            if (hMenu == 0) return null;

            var top = ReadMenuLevel(hMenu, depth: 0, prefix: "w32", ct);
            return top.Count > 0 ? top : null;
        }, ct);
    }

    private static List<MenuNode> ReadMenuLevel(nint hMenu, int depth, string prefix, CancellationToken ct)
    {
        var result = new List<MenuNode>();
        if (depth > 3) return result;

        int count = GetMenuItemCount(hMenu);
        if (count <= 0 || count > 64) return result;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_FTYPE | MIIM_STRING,
                dwTypeData = 0,
                cch = 0
            };

            if (!GetMenuItemInfoW(hMenu, (uint)i, true, ref info)) continue;

            if ((info.fType & MFT_SEPARATOR) != 0)
            {
                result.Add(new MenuNode($"{prefix}_{i}_sep", string.Empty, null, false, false, true, null, []));
                continue;
            }

            string rawText = ReadItemString(hMenu, (uint)i, info.cch);
            if (string.IsNullOrWhiteSpace(rawText)) continue;

            var (title, shortcut) = ParseMenuLabel(rawText);
            bool isEnabled = (info.fState & MFS_DISABLED) == 0;
            bool isChecked = (info.fState & MFS_CHECKED) != 0;

            var children = info.hSubMenu != 0
                ? ReadMenuLevel(info.hSubMenu, depth + 1, $"{prefix}_{i}", ct)
                : [];

            var action = info.hSubMenu == 0 && info.wID != 0
                ? new MenuInvokeAction(MenuActionKind.Win32Command, CommandId: info.wID)
                : null;

            result.Add(new MenuNode($"{prefix}_{i}", title, shortcut, isEnabled, isChecked, false, action, children));
        }

        return result;
    }

    private static string ReadItemString(nint hMenu, uint pos, uint cch)
    {
        if (cch == 0 || cch > 256) return string.Empty;
        nint buffer = Marshal.AllocHGlobal((int)((cch + 1) * sizeof(char)));
        try
        {
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_STRING,
                dwTypeData = buffer,
                cch = cch + 1
            };
            if (!GetMenuItemInfoW(hMenu, pos, true, ref info)) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static (string Title, string? Shortcut) ParseMenuLabel(string raw)
    {
        string cleaned = raw.Replace("&&", "\0").Replace("&", string.Empty).Replace("\0", "&");
        int tabIdx = cleaned.IndexOf('\t');
        if (tabIdx >= 0)
        {
            string title = cleaned[..tabIdx].Trim();
            string shortcut = cleaned[(tabIdx + 1)..].Trim();
            return (title, string.IsNullOrEmpty(shortcut) ? null : shortcut);
        }

        return (cleaned.Trim(), null);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public nint hSubMenu;
        public nint hbmpChecked;
        public nint hbmpUnchecked;
        public nuint dwItemData;
        public nint dwTypeData;
        public uint cch;
        public nint hbmpItem;
    }

    [DllImport("user32.dll")]
    private static extern nint GetMenu(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(nint hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfoW(nint hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFOW lpmii);
}
