using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TahoeDesk.Core.MenuEngine;

public sealed class JsonProfileMenuProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, IReadOnlyList<MenuNode>> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MenuNode> _genericMenus = [];

    private JsonProfileMenuProvider() { }

    public static JsonProfileMenuProvider CreateBuiltIn(string? userProfileDir = null)
    {
        var provider = new JsonProfileMenuProvider();
        var asm = typeof(JsonProfileMenuProvider).Assembly;
        const string marker = ".MenuProfiles.";

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            int idx = resourceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            string key = Path.GetFileNameWithoutExtension(resourceName[(idx + marker.Length)..]);
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            provider.LoadFromStream(key, stream);
        }

        string overrideDir = userProfileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TahoeDesk",
            "MenuProfiles");

        if (Directory.Exists(overrideDir))
        {
            foreach (var file in Directory.EnumerateFiles(overrideDir, "*.json"))
            {
                try
                {
                    using var fs = File.OpenRead(file);
                    provider.LoadFromStream(Path.GetFileNameWithoutExtension(file), fs);
                }
                catch { }
            }
        }

        return provider;
    }

    public (IReadOnlyList<MenuNode> Menus, MenuSource Source) Resolve(string processName)
    {
        if (!string.IsNullOrWhiteSpace(processName) && _profiles.TryGetValue(processName, out var specific))
        {
            return (specific, MenuSource.AppProfile);
        }

        return (_genericMenus, MenuSource.GenericFallback);
    }

    private void LoadFromStream(string key, Stream stream)
    {
        var dto = JsonSerializer.Deserialize<ProfileDocument>(stream, JsonOptions);
        if (dto?.Menus is null) return;

        var nodes = ConvertNodes(dto.Menus, key);
        if (string.Equals(key, "_generic", StringComparison.OrdinalIgnoreCase))
        {
            _genericMenus = nodes;
        }
        else
        {
            _profiles[key] = nodes;
        }
    }

    private static List<MenuNode> ConvertNodes(List<MenuItemDto> items, string prefix)
    {
        var list = new List<MenuNode>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Separator)
            {
                list.Add(new MenuNode($"{prefix}_{i}_sep", string.Empty, null, false, false, true, null, []));
                continue;
            }

            var children = item.Children is { Count: > 0 }
                ? ConvertNodes(item.Children, $"{prefix}_{i}")
                : [];

            MenuInvokeAction? action = null;
            if (!string.IsNullOrWhiteSpace(item.WindowOp))
            {
                action = new MenuInvokeAction(MenuActionKind.WindowCommand, WindowOp: item.WindowOp);
            }
            else if (!string.IsNullOrWhiteSpace(item.Shortcut))
            {
                action = new MenuInvokeAction(MenuActionKind.KeyboardShortcut, Shortcut: item.Shortcut);
            }

            list.Add(new MenuNode(
                Id: $"{prefix}_{i}",
                Title: item.Title ?? string.Empty,
                ShortcutText: item.Shortcut,
                IsEnabled: item.Enabled ?? true,
                IsChecked: false,
                IsSeparator: false,
                Action: action,
                Children: children));
        }

        return list;
    }

    private sealed class ProfileDocument
    {
        public List<MenuItemDto>? Menus { get; set; }
    }

    private sealed class MenuItemDto
    {
        public string? Title { get; set; }
        public string? Shortcut { get; set; }
        public string? WindowOp { get; set; }
        public bool? Enabled { get; set; }
        public bool Separator { get; set; }
        public List<MenuItemDto>? Children { get; set; }
    }
}
