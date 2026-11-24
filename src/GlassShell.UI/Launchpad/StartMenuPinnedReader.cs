using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace GlassShell.UI.Launchpad;

/// <summary>
/// Reads the user's exact Windows 11 Start Menu "Pinned" (已固定) app order via official Windows UI Automation
/// without touching encrypted proprietary binary files.
/// </summary>
public static class StartMenuPinnedReader
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_ESCAPE = 0x1B;

    public static async Task<IReadOnlyList<string>> CapturePinnedAppNamesAsync()
    {
        // Open native Windows 11 Start Menu (injected VK_LWIN bypasses our LowLevelWinKeyHook cleanly)
        SendKeyTap(VK_LWIN);

        var names = new List<string>();
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(attempt == 0 ? 360 : 150);
                names = await Task.Run(ReadPinnedItemsFromStartUia);
                if (names.Count > 0)
                {
                    break;
                }
            }
        }
        finally
        {
            // Dismiss native Start Menu cleanly
            SendKeyTap(VK_ESCAPE);
            await Task.Delay(120);
        }

        return names;
    }

    private static List<string> ReadPinnedItemsFromStartUia()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var startPids = Process.GetProcessesByName("StartMenuExperienceHost")
                .Select(p => p.Id)
                .ToArray();
            if (startPids.Length == 0) return results;

            var root = AutomationElement.RootElement;
            AutomationElement? startWindow = null;

            foreach (int pid in startPids)
            {
                var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
                var wins = root.FindAll(TreeScope.Children, cond);
                foreach (AutomationElement w in wins)
                {
                    var rect = w.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 200 && rect.Height > 200)
                    {
                        startWindow = w;
                        break;
                    }
                }
                if (startWindow is not null) break;
            }

            if (startWindow is null) return results;

            // Locate the Pinned grid list container specifically ("PinnedList" / "已固定" / "Pinned")
            AutomationElement? pinnedContainer = null;
            var listCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List);
            var lists = startWindow.FindAll(TreeScope.Descendants, listCond);
            foreach (AutomationElement list in lists)
            {
                string autoId = list.Current.AutomationId ?? string.Empty;
                string name = list.Current.Name ?? string.Empty;
                if (autoId.Contains("Pinned", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("已固定", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Pinned", StringComparison.OrdinalIgnoreCase))
                {
                    pinnedContainer = list;
                    break;
                }
            }

            // Fallback: use first substantial List inside Start Menu (Windows 11 always places Pinned grid first)
            if (pinnedContainer is null && lists.Count > 0)
            {
                pinnedContainer = lists[0];
            }

            if (pinnedContainer is null) return results;

            var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            var items = pinnedContainer.FindAll(TreeScope.Descendants, itemCond);
            foreach (AutomationElement item in items)
            {
                string rawName = (item.Current.Name ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(rawName)) continue;

                // Strip common Start Menu accessibility suffixes like "，文件夹" / ", folder" or newline descriptions
                int nl = rawName.IndexOfAny(['\r', '\n']);
                if (nl > 0) rawName = rawName[..nl].Trim();
                if (rawName.Length < 1) continue;

                if (seen.Add(rawName))
                {
                    results.Add(rawName);
                }
            }
        }
        catch { }

        return results;
    }

    private static void SendKeyTap(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
