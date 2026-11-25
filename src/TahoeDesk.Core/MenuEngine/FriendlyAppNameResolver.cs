using System;
using System.Collections.Generic;

namespace TahoeDesk.Core.MenuEngine;

public static class FriendlyAppNameResolver
{
    private static readonly Dictionary<string, string> KnownProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["code"] = "VS Code",
        ["notepad"] = "记事本",
        ["windowsterminal"] = "终端",
        ["wechat"] = "微信",
        ["weixin"] = "微信",
        ["qq"] = "QQ",
        ["taskmgr"] = "任务管理器",
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint"
    };

    public static string Resolve(string? processName, string? className, string? windowTitle)
    {
        string proc = (processName ?? string.Empty).Trim();
        string cls = (className ?? string.Empty).Trim();
        string title = (windowTitle ?? string.Empty).Trim();

        if (string.Equals(proc, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (cls is "Progman" or "WorkerW")
            {
                return "桌面";
            }
            return "文件资源管理器";
        }

        if (string.Equals(proc, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(title) ? "系统应用" : title;
        }

        if (!string.IsNullOrEmpty(proc) && KnownProcessNames.TryGetValue(proc, out var friendly))
        {
            return friendly;
        }

        if (!string.IsNullOrWhiteSpace(proc))
        {
            return proc;
        }

        return string.IsNullOrWhiteSpace(title) ? "桌面" : title;
    }
}
