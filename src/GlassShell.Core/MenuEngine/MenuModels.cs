using System.Collections.Generic;

namespace GlassShell.Core.MenuEngine;

public enum MenuSource
{
    Win32GetMenu,
    StaticUia,
    AppProfile,
    GenericFallback
}

public enum MenuActionKind
{
    Win32Command,
    UiaInvoke,
    KeyboardShortcut,
    WindowCommand
}

public sealed record MenuInvokeAction(
    MenuActionKind Kind,
    uint CommandId = 0,
    string? Shortcut = null,
    string? WindowOp = null,
    object? UiaElementRef = null);

public sealed record MenuNode(
    string Id,
    string Title,
    string? ShortcutText,
    bool IsEnabled,
    bool IsChecked,
    bool IsSeparator,
    MenuInvokeAction? Action,
    IReadOnlyList<MenuNode> Children);

public sealed record MenuBarSnapshot(
    nint TargetHwnd,
    string ProcessName,
    string AppDisplayName,
    IReadOnlyList<MenuNode> TopLevelMenus,
    MenuSource Source);
