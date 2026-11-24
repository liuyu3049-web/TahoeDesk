using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using GlassShell.Core.Search;

namespace GlassShell.Core.Discovery;

/// <summary>
/// Scans Windows Start Menu shortcuts (.lnk) and common system tools to build a clean, deduplicated
/// installed application catalog for Launchpad and Spotlight.
/// </summary>
public static class InstalledAppScanner
{
    private static readonly string[] ExcludedNameKeywords =
    [
        "uninstall", "卸载", "readme", "帮助", "help", "documentation", "manual", "license", "website", "主页"
    ];

    public static IReadOnlyList<InstalledAppEntry> ScanInstalledApps()
    {
        var results = new List<InstalledAppEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        ];

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string lnk in EnumerateShortcutsSafe(root, maxDepth: 3))
            {
                string displayName = Path.GetFileNameWithoutExtension(lnk).Trim();
                if (string.IsNullOrWhiteSpace(displayName) || IsExcludedName(displayName))
                {
                    continue;
                }

                string? resolvedTarget = TryResolveShortcutTarget(lnk, out string? iconLoc);
                string launchTarget = !string.IsNullOrWhiteSpace(resolvedTarget) && File.Exists(resolvedTarget)
                    ? resolvedTarget!
                    : lnk;

                if (launchTarget.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                    || launchTarget.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)
                    || launchTarget.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    || launchTarget.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    || launchTarget.Contains("unins", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string dedupeKey = !string.IsNullOrWhiteSpace(resolvedTarget)
                    ? resolvedTarget!
                    : lnk;

                if (!seenTargets.Add(dedupeKey) || !seenNames.Add(displayName))
                {
                    continue;
                }

                string iconPath = !string.IsNullOrWhiteSpace(iconLoc) && File.Exists(iconLoc)
                    ? iconLoc!
                    : launchTarget;

                results.Add(new InstalledAppEntry(
                    Id: $"app:{dedupeKey.ToLowerInvariant()}",
                    Name: displayName,
                    LaunchTarget: lnk, // launch via .lnk preserves working directory & arguments
                    IconPath: iconPath,
                    IsUwp: false,
                    PinyinInitials: PinyinInitials.GetInitials(displayName)));
            }
        }

        // Ensure core Windows apps are always present even if omitted from classic Start Menu folders
        AddBuiltInIfMissing(results, seenTargets, seenNames, "文件资源管理器", @"C:\Windows\explorer.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "记事本", @"C:\Windows\System32\notepad.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "计算器", @"C:\Windows\System32\calc.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "画图", @"C:\Windows\System32\mspaint.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "命令提示符", @"C:\Windows\System32\cmd.exe");

        return results.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static IReadOnlyList<IndexedFileEntry> ScanLocalUserFiles(int maxFiles = 300)
    {
        var files = new List<IndexedFileEntry>();
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        ];

        foreach (string folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            CollectFilesSafe(folder, depth: 0, maxDepth: 2, files, maxFiles);
            if (files.Count >= maxFiles) break;
        }

        return files;
    }

    private static void CollectFilesSafe(string dir, int depth, int maxDepth, List<IndexedFileEntry> acc, int maxFiles)
    {
        if (acc.Count >= maxFiles) return;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (acc.Count >= maxFiles) return;
                string name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isDir = Directory.Exists(entry);
                acc.Add(new IndexedFileEntry(name, entry, isDir, PinyinInitials.GetInitials(name)));

                if (isDir && depth < maxDepth)
                {
                    CollectFilesSafe(entry, depth + 1, maxDepth, acc, maxFiles);
                }
            }
        }
        catch { }
    }

    private static void AddBuiltInIfMissing(
        List<InstalledAppEntry> list,
        HashSet<string> seenTargets,
        HashSet<string> seenNames,
        string name,
        string exePath)
    {
        if (!File.Exists(exePath)) return;
        if (!seenTargets.Add(exePath) || !seenNames.Add(name)) return;
        list.Add(new InstalledAppEntry(
            Id: $"app:{exePath.ToLowerInvariant()}",
            Name: name,
            LaunchTarget: exePath,
            IconPath: exePath,
            IsUwp: false,
            PinyinInitials: PinyinInitials.GetInitials(name)));
    }

    private static IEnumerable<string> EnumerateShortcutsSafe(string dir, int maxDepth)
    {
        if (maxDepth < 0) yield break;

        string[] files;
        try { files = Directory.GetFiles(dir, "*.lnk"); }
        catch { yield break; }

        foreach (string f in files) yield return f;

        if (maxDepth == 0) yield break;
        string[] subdirs;
        try { subdirs = Directory.GetDirectories(dir); }
        catch { yield break; }

        foreach (string sub in subdirs)
        {
            foreach (string nested in EnumerateShortcutsSafe(sub, maxDepth - 1))
            {
                yield return nested;
            }
        }
    }

    private static bool IsExcludedName(string name)
    {
        foreach (string kw in ExcludedNameKeywords)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? TryResolveShortcutTarget(string lnkPath, out string? iconLocation)
    {
        iconLocation = null;
        try
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var targetSb = new StringBuilder(520);
                link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);

                var iconSb = new StringBuilder(520);
                link.GetIconLocation(iconSb, iconSb.Capacity, out _);
                string rawIcon = iconSb.ToString().Trim();
                if (!string.IsNullOrEmpty(rawIcon))
                {
                    iconLocation = Environment.ExpandEnvironmentVariables(rawIcon);
                }

                string target = targetSb.ToString().Trim();
                return string.IsNullOrEmpty(target) ? null : Environment.ExpandEnvironmentVariables(target);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
