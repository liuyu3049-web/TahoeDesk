using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlassShell.Core.Search;

/// <summary>
/// Fast multi-source Spotlight search ranker combining Instant Calculator, Installed Apps
/// (fuzzy + offline Pinyin initials), System Commands, and Local User Files.
/// </summary>
public sealed class SpotlightSearchEngine
{
    private sealed record SystemCommandDef(string Id, string Title, string Subtitle, string Keywords);

    private static readonly SystemCommandDef[] BuiltInCommands =
    [
        new("cmd:lock", "锁定屏幕", "系统命令 · Win+L", "lock suoping sp 锁屏 锁定屏幕"),
        new("cmd:sleep", "睡眠", "系统命令 · 挂起电脑", "sleep shuimian sm 睡眠 休眠"),
        new("cmd:restart", "重新启动", "系统命令 · 重启 Windows", "restart reboot chongqi cq 重启 重新启动"),
        new("cmd:shutdown", "关机", "系统命令 · 关闭电脑", "shutdown guanji gj 关机"),
        new("cmd:theme-dark", "切换深色外观", "TahoeDesk 外观", "dark shense ss 深色 暗色 夜间模式"),
        new("cmd:theme-light", "切换浅色外观", "TahoeDesk 外观", "light qianse qs 浅色 亮色 日间模式"),
        new("cmd:theme-system", "外观跟随系统", "TahoeDesk 外观", "auto system gensui 自动 跟随系统"),
        new("cmd:empty-bin", "清倒回收站", "系统命令 · 清空回收站", "trash recycle bin qingkong hsz 清空回收站 清倒回收站"),
        new("cmd:settings", "打开 TahoeDesk 设置", "系统偏好设置", "settings shezhi sz 设置 偏好设置"),
        new("cmd:win-settings", "打开 Windows 设置", "系统命令 · ms-settings", "windows settings xitongshezhi 系统设置"),
        new("cmd:restore-taskbar", "退出 TahoeDesk", "退出并恢复系统任务栏", "exit quit tuichu restore taskbar 退出 恢复原生任务栏")
    ];

    public IReadOnlyList<SpotlightResult> Search(
        string query,
        IReadOnlyList<InstalledAppEntry> apps,
        IReadOnlyList<IndexedFileEntry> files,
        int maxResults = 14)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        string q = query.Trim();
        var results = new List<SpotlightResult>();

        if (InstantCalculator.TryEvaluate(q, out _, out string formatted))
        {
            results.Add(new SpotlightResult(
                SpotlightResultKind.Calculator,
                Id: "calc:result",
                Title: formatted,
                Subtitle: $"{q} = {formatted}（按 Enter 复制结果）",
                Payload: formatted,
                IconPath: null,
                Score: 10000));
        }

        foreach (var app in apps)
        {
            int score = ScoreMatch(q, app.Name, app.PinyinInitials);
            if (score > 0)
            {
                results.Add(new SpotlightResult(
                    SpotlightResultKind.Application,
                    Id: app.Id,
                    Title: app.Name,
                    Subtitle: "应用程序",
                    Payload: app.LaunchTarget,
                    IconPath: app.IconPath,
                    Score: score + 500));
            }
        }

        foreach (var cmd in BuiltInCommands)
        {
            int score = ScoreMatch(q, cmd.Title, cmd.Keywords);
            if (score > 0)
            {
                results.Add(new SpotlightResult(
                    SpotlightResultKind.SystemCommand,
                    Id: cmd.Id,
                    Title: cmd.Title,
                    Subtitle: cmd.Subtitle,
                    Payload: cmd.Id,
                    IconPath: null,
                    Score: score + 320));
            }
        }

        foreach (var file in files)
        {
            int score = ScoreMatch(q, file.Name, file.PinyinInitials);
            if (score > 0)
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(file.FullPath)) ?? string.Empty;
                results.Add(new SpotlightResult(
                    SpotlightResultKind.File,
                    Id: $"file:{file.FullPath}",
                    Title: file.Name,
                    Subtitle: string.IsNullOrEmpty(parent) ? file.FullPath : $"{parent} · {file.FullPath}",
                    Payload: file.FullPath,
                    IconPath: file.FullPath,
                    Score: score + 100));
            }
        }

        var ranked = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(Math.Max(1, maxResults - 1))
            .ToList();

        // Always provide a browser search fallback so pressing Enter never silently does nothing
        ranked.Add(new SpotlightResult(
            SpotlightResultKind.WebSearch,
            Id: "web:search",
            Title: $"搜索 “{q}”",
            Subtitle: "使用默认浏览器进行网页搜索",
            Payload: q,
            IconPath: null,
            Score: 1));

        return ranked;
    }

    public static int ScoreMatch(string query, string targetName, string secondaryKey)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        string q = query.Trim();
        if (string.Equals(targetName, q, StringComparison.OrdinalIgnoreCase)) return 400;
        if (targetName.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 320;

        int idx = targetName.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return 240 - Math.Min(idx * 4, 80);

        if (!string.IsNullOrEmpty(secondaryKey))
        {
            if (string.Equals(secondaryKey, q, StringComparison.OrdinalIgnoreCase)) return 290;
            if (secondaryKey.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 260;
            int pIdx = secondaryKey.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (pIdx >= 0) return 190 - Math.Min(pIdx * 3, 60);
        }

        // Ordered subsequence fuzzy match (e.g. "vsc" -> "Visual Studio Code")
        int qi = 0;
        int bonus = 0;
        for (int ti = 0; ti < targetName.Length && qi < q.Length; ti++)
        {
            if (char.ToLowerInvariant(targetName[ti]) == char.ToLowerInvariant(q[qi]))
            {
                if (ti == 0 || !char.IsLetterOrDigit(targetName[ti - 1]) || char.IsUpper(targetName[ti]))
                {
                    bonus += 12;
                }
                qi++;
            }
        }

        return qi == q.Length ? 100 + bonus : 0;
    }
}
