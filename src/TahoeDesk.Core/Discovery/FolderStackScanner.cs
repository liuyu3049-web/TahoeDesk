using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TahoeDesk.Core.Discovery;

public sealed record FolderStackItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    DateTime LastModifiedUtc);

/// <summary>
/// Scans a pinned Dock folder (e.g. Downloads) for recent files & subfolders sorted newest-first.
/// </summary>
public static class FolderStackScanner
{
    public static IReadOnlyList<FolderStackItem> Scan(string folderPath, int maxItems = 20)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath) || maxItems <= 0)
        {
            return [];
        }

        var items = new List<FolderStackItem>();
        try
        {
            var dirInfo = new DirectoryInfo(folderPath);
            foreach (var entry in dirInfo.EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.') || string.Equals(entry.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((entry.Attributes & FileAttributes.Hidden) != 0 || (entry.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                items.Add(new FolderStackItem(
                    Name: entry.Name,
                    FullPath: entry.FullName,
                    IsDirectory: (entry.Attributes & FileAttributes.Directory) != 0,
                    LastModifiedUtc: entry.LastWriteTimeUtc));
            }
        }
        catch
        {
            return [];
        }

        return items
            .OrderByDescending(i => i.LastModifiedUtc)
            .Take(maxItems)
            .ToList();
    }
}
