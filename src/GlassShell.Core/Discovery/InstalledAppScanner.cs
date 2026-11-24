using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using GlassShell.Core.Search;

namespace GlassShell.Core.Discovery;

/// <summary>
/// Scans Windows 11 shell:AppsFolder (all Store/UWP/MSIX + registered Win32 apps) plus Start Menu
/// and Desktop shortcuts (.lnk) to build a complete, deduplicated application catalog for Launchpad and Spotlight.
/// </summary>
public static class InstalledAppScanner
{
    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private static readonly string[] HardExcludedNameKeywords =
    [
        "uninstall", "卸载"
    ];

    private static readonly string[] ExcludedTargetExtensions =
    [
        ".url", ".chm", ".txt", ".pdf", ".html", ".htm", ".md", ".vbs", ".rtf", ".doc", ".docx"
    ];

    private static readonly string[] ExcludedTargetExecutables =
    [
        "hh.exe", "msiexec.exe", "wscript.exe", "cscript.exe"
    ];

    public static IReadOnlyList<InstalledAppEntry> ScanInstalledApps()
    {
        var results = new List<InstalledAppEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Classic Start Menu & Desktop .lnk shortcuts (exact .lnk working dirs, arguments, portable apps)
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
            foreach (string lnk in EnumerateShortcutsSafe(root, maxDepth: 4))
            {
                string displayName = Path.GetFileNameWithoutExtension(lnk).Trim();
                if (string.IsNullOrWhiteSpace(displayName) || IsHardExcludedName(displayName))
                {
                    continue;
                }

                string? resolvedTarget = TryResolveShortcutTarget(lnk, out string? iconLoc);
                string launchTarget = !string.IsNullOrWhiteSpace(resolvedTarget) && File.Exists(resolvedTarget)
                    ? resolvedTarget!
                    : lnk;

                if (IsExcludedTarget(launchTarget))
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

        // 2. Canonical Windows 11 shell:AppsFolder enumeration (catches ALL Store/UWP/MSIX + registered Win32 apps!)
        ScanAppsFolder(results, seenTargets, seenNames);

        // 3. Ensure core Windows apps are always present even if omitted
        AddBuiltInIfMissing(results, seenTargets, seenNames, "文件资源管理器", @"C:\Windows\explorer.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "记事本", @"C:\Windows\System32\notepad.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "计算器", @"C:\Windows\System32\calc.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "画图", @"C:\Windows\System32\mspaint.exe");
        AddBuiltInIfMissing(results, seenTargets, seenNames, "命令提示符", @"C:\Windows\System32\cmd.exe");

        return results.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static InstalledAppEntry? CreateManualEntry(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        string fullPath = Path.GetFullPath(filePath);
        string displayName = Path.GetFileNameWithoutExtension(fullPath).Trim();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = Path.GetFileName(fullPath);

        string iconPath = fullPath;
        if (fullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string? resolved = TryResolveShortcutTarget(fullPath, out string? iconLoc);
            if (!string.IsNullOrWhiteSpace(iconLoc) && File.Exists(iconLoc))
            {
                iconPath = iconLoc!;
            }
            else if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                iconPath = resolved!;
            }
        }

        return new InstalledAppEntry(
            Id: $"manual:{fullPath.ToLowerInvariant()}",
            Name: displayName,
            LaunchTarget: fullPath,
            IconPath: iconPath,
            IsUwp: false,
            PinyinInitials: PinyinInitials.GetInitials(displayName));
    }

    private static void ScanAppsFolder(
        List<InstalledAppEntry> results,
        HashSet<string> seenTargets,
        HashSet<string> seenNames)
    {
        IShellItem? appsFolder = null;
        IEnumShellItems? enumerator = null;
        try
        {
            Guid folderId = FolderIdAppsFolder;
            Guid shellItemIid = typeof(IShellItem).GUID;
            int hr = SHGetKnownFolderItem(ref folderId, 0, 0, ref shellItemIid, out appsFolder);
            if (hr != 0 || appsFolder is null) return;

            Guid bhid = BhidEnumItems;
            Guid enumIid = typeof(IEnumShellItems).GUID;
            hr = appsFolder.BindToHandler(0, ref bhid, ref enumIid, out nint enumPtr);
            if (hr != 0 || enumPtr == 0) return;

            enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            Marshal.Release(enumPtr);

            while (enumerator.Next(1, out IShellItem? item, out uint fetched) == 0 && fetched == 1 && item is not null)
            {
                try
                {
                    if (item.GetDisplayName(SigdnNormalDisplay, out string? rawName) != 0 || string.IsNullOrWhiteSpace(rawName))
                    {
                        continue;
                    }

                    string displayName = rawName.Trim();
                    if (IsHardExcludedName(displayName)) continue;

                    if (item.GetDisplayName(SigdnDesktopAbsoluteParsing, out string? parsingName) != 0 || string.IsNullOrWhiteSpace(parsingName))
                    {
                        continue;
                    }

                    parsingName = parsingName.Trim();
                    // Strip leading AppsFolder prefix if Windows returns the full parsing path
                    string aumid = parsingName;
                    int bangSep = parsingName.IndexOf(@"AppsFolder\", StringComparison.OrdinalIgnoreCase);
                    if (bangSep >= 0)
                    {
                        aumid = parsingName[(bangSep + @"AppsFolder\".Length)..];
                    }

                    bool isDirectFile = File.Exists(parsingName);
                    if (isDirectFile && IsExcludedTarget(parsingName)) continue;
                    if (!isDirectFile)
                    {
                        if (aumid.Contains("unins", StringComparison.OrdinalIgnoreCase)
                            || aumid.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || aumid.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    string? expandedKnownPath = TryExpandKnownFolderExePath(aumid);
                    if (expandedKnownPath is not null && seenTargets.Contains(expandedKnownPath))
                    {
                        continue;
                    }

                    string shellPath = isDirectFile ? parsingName : $"shell:AppsFolder\\{aumid}";
                    string dedupeKey = isDirectFile ? parsingName : (expandedKnownPath ?? aumid);

                    if (!seenNames.Add(displayName) || !seenTargets.Add(dedupeKey))
                    {
                        continue;
                    }

                    results.Add(new InstalledAppEntry(
                        Id: $"uwp:{dedupeKey.ToLowerInvariant()}",
                        Name: displayName,
                        LaunchTarget: shellPath,
                        IconPath: shellPath,
                        IsUwp: !isDirectFile,
                        PinyinInitials: PinyinInitials.GetInitials(displayName)));
                }
                catch { }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        catch { }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (appsFolder is not null) Marshal.ReleaseComObject(appsFolder);
        }
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

    private static bool IsExcludedTarget(string path)
    {
        if (path.Contains("unins", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string ext in ExcludedTargetExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }

        string fileName = Path.GetFileName(path);
        foreach (string exe in ExcludedTargetExecutables)
        {
            if (string.Equals(fileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string? TryExpandKnownFolderExePath(string aumid)
    {
        if (aumid.StartsWith("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), aumid["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\".Length..]);
        }
        if (aumid.StartsWith("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), aumid["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}\\".Length..]);
        }
        if (aumid.StartsWith("{F38BF404-1D43-42F2-9305-67DE0B28FC23}\\", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), aumid["{F38BF404-1D43-42F2-9305-67DE0B28FC23}\\".Length..]);
        }
        return null;
    }

    private static bool IsHardExcludedName(string name)
    {
        foreach (string kw in HardExcludedNameKeywords)
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

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(
        ref Guid rfid,
        uint dwFlags,
        nint hToken,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);

        [PreserveSig]
        int GetParent(out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? rgelt, out uint pceltFetched);

        [PreserveSig]
        int Skip(uint celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumShellItems ppenum);
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
