using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GlassShell.Core.MenuEngine;

/// <summary>
/// Strictly silent COM UI Automation menu extractor.
/// NEVER calls ExpandCollapsePattern.Expand() on target windows.
/// Only adopts UIA menu bars whose submenus are already statically populated.
/// </summary>
public sealed class StaticUiaMenuExtractor : IMenuExtractor
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_MenuBarControlTypeId = 50010;
    private const int UIA_MenuItemControlTypeId = 50011;
    private const int UIA_SeparatorControlTypeId = 50038;
    private const int UIA_InvokePatternId = 10000;
    private const int TreeScope_Children = 2;

    public MenuSource Source => MenuSource.StaticUia;

    public Task<IReadOnlyList<MenuNode>?> TryExtractAsync(nint hwnd, string processName, CancellationToken ct)
    {
        if (hwnd == 0 || ct.IsCancellationRequested)
        {
            return Task.FromResult<IReadOnlyList<MenuNode>?>(null);
        }

        // Run on a dedicated MTA thread-pool worker; UIA COM calls are safe on MTA.
        return Task.Run<IReadOnlyList<MenuNode>?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            IUIAutomation? uia = null;
            IUIAutomationElement? root = null;
            IUIAutomationCondition? menuBarCond = null;
            IUIAutomationElement? menuBar = null;

            try
            {
                var uiaType = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"));
                if (uiaType is null) return null;
                uia = Activator.CreateInstance(uiaType) as IUIAutomation;
                if (uia is null) return null;

                if (uia.ElementFromHandle(hwnd, out root) != 0 || root is null) return null;
                if (uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_MenuBarControlTypeId, out menuBarCond) != 0 || menuBarCond is null)
                {
                    return null;
                }

                // Bounded child search (depth 1) to prevent full-tree stalls on Chromium/Electron apps.
                if (root.FindFirst(TreeScope_Children, menuBarCond, out menuBar) != 0 || menuBar is null)
                {
                    return null;
                }

                var topItems = ReadStaticChildren(uia, menuBar, depth: 0, prefix: "uia", ct);
                // Only accept if at least one top-level item already exposes static sub-items (silent guarantee).
                bool hasStaticSubmenus = false;
                foreach (var item in topItems)
                {
                    if (item.Children.Count > 0)
                    {
                        hasStaticSubmenus = true;
                        break;
                    }
                }

                return hasStaticSubmenus ? topItems : null;
            }
            catch (UnauthorizedAccessException)
            {
                throw; // Let coordinator log/handle UIPI cleanly
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                ReleaseCom(menuBar);
                ReleaseCom(menuBarCond);
                ReleaseCom(root);
                ReleaseCom(uia);
            }
        }, ct);
    }

    private static List<MenuNode> ReadStaticChildren(IUIAutomation uia, IUIAutomationElement parent, int depth, string prefix, CancellationToken ct)
    {
        var list = new List<MenuNode>();
        if (depth > 2) return list;

        IUIAutomationCondition? trueCond = null;
        IUIAutomationElementArray? children = null;
        try
        {
            if (uia.CreateTrueCondition(out trueCond) != 0 || trueCond is null) return list;
            if (parent.FindAll(TreeScope_Children, trueCond, out children) != 0 || children is null) return list;
            if (children.get_Length(out int count) != 0 || count <= 0 || count > 48) return list;

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (children.GetElement(i, out var child) != 0 || child is null) continue;

                try
                {
                    child.get_CurrentControlType(out int ctrlType);
                    if (ctrlType == UIA_SeparatorControlTypeId)
                    {
                        list.Add(new MenuNode($"{prefix}_{i}_sep", string.Empty, null, false, false, true, null, []));
                        continue;
                    }

                    if (ctrlType != UIA_MenuItemControlTypeId) continue;

                    child.get_CurrentName(out string? name);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    child.get_CurrentIsEnabled(out int enabledInt);
                    child.get_CurrentAcceleratorKey(out string? accel);

                    // Strictly silent: inspect only already-materialized children, NEVER Expand().
                    var subItems = ReadStaticChildren(uia, child, depth + 1, $"{prefix}_{i}", ct);
                    MenuInvokeAction? action = null;
                    if (subItems.Count == 0 && child.GetCurrentPattern(UIA_InvokePatternId, out object? patternObj) == 0 && patternObj is not null)
                    {
                        action = new MenuInvokeAction(MenuActionKind.UiaInvoke, UiaElementRef: patternObj);
                    }

                    list.Add(new MenuNode(
                        $"{prefix}_{i}",
                        name.Trim(),
                        string.IsNullOrWhiteSpace(accel) ? null : accel.Trim(),
                        enabledInt != 0,
                        false,
                        false,
                        action,
                        subItems));
                }
                finally
                {
                    ReleaseCom(child);
                }
            }
        }
        finally
        {
            ReleaseCom(children);
            ReleaseCom(trueCond);
        }

        return list;
    }

    internal static bool TryInvokeUiaPattern(object? patternObj)
    {
        if (patternObj is IUIAutomationInvokePattern invoke)
        {
            try
            {
                return invoke.Invoke() == 0;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static void ReleaseCom(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            try { Marshal.ReleaseComObject(comObj); } catch { }
        }
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);
        [PreserveSig] int CompareRuntimeIds(nint runtimeId1, nint runtimeId2, out int areSame);
        [PreserveSig] int GetRootElement(out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandle(nint hwnd, out IUIAutomationElement? element);
        [PreserveSig] int ElementFromPoint(long pt, out IUIAutomationElement? element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
        [PreserveSig] int GetRootElementBuildCache(nint cacheRequest, out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandleBuildCache(nint hwnd, nint cacheRequest, out IUIAutomationElement? element);
        [PreserveSig] int ElementFromPointBuildCache(long pt, nint cacheRequest, out IUIAutomationElement? element);
        [PreserveSig] int GetFocusedElementBuildCache(nint cacheRequest, out IUIAutomationElement? element);
        [PreserveSig] int CreateTreeWalker(IUIAutomationCondition pCondition, out nint walker);
        [PreserveSig] int get_ControlViewWalker(out nint walker);
        [PreserveSig] int get_ContentViewWalker(out nint walker);
        [PreserveSig] int get_RawViewWalker(out nint walker);
        [PreserveSig] int get_RawViewCondition(out IUIAutomationCondition? condition);
        [PreserveSig] int get_ControlViewCondition(out IUIAutomationCondition? condition);
        [PreserveSig] int get_ContentViewCondition(out IUIAutomationCondition? condition);
        [PreserveSig] int CreateCacheRequest(out nint cacheRequest);
        [PreserveSig] int CreateTrueCondition(out IUIAutomationCondition? newCondition);
        [PreserveSig] int CreateFalseCondition(out IUIAutomationCondition? newCondition);
        [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition? newCondition);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        [PreserveSig] int get_Length(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out nint runtimeId);
        [PreserveSig] int FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray? found);
        [PreserveSig] int FindFirstBuildCache(int scope, IUIAutomationCondition condition, nint cacheRequest, out IUIAutomationElement? found);
        [PreserveSig] int FindAllBuildCache(int scope, IUIAutomationCondition condition, nint cacheRequest, out IUIAutomationElementArray? found);
        [PreserveSig] int BuildUpdatedCache(nint cacheRequest, out IUIAutomationElement? updatedElement);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out nint patternObject);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out nint patternObject);
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
        [PreserveSig] int GetCachedPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
        [PreserveSig] int GetCachedParent(out IUIAutomationElement? parent);
        [PreserveSig] int GetCachedChildren(out IUIAutomationElementArray? children);
        [PreserveSig] int get_CurrentProcessId(out int retVal);
        [PreserveSig] int get_CurrentControlType(out int retVal);
        [PreserveSig] int get_CurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string? retVal);
        [PreserveSig] int get_CurrentHasKeyboardFocus(out int retVal);
        [PreserveSig] int get_CurrentIsKeyboardFocusable(out int retVal);
        [PreserveSig] int get_CurrentIsEnabled(out int retVal);
    }

    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationInvokePattern
    {
        [PreserveSig] int Invoke();
    }
}
