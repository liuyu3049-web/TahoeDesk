using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using TahoeDesk.Core.Config;
using TahoeDesk.Core.Discovery;
using TahoeDesk.UI.Glass;

namespace TahoeDesk.UI.MenuBar;

public partial class ForceQuitWindow : Window
{
    private sealed record AppEntry(uint ProcessId, string DisplayText);

    private readonly IWindowTracker _tracker;

    public ForceQuitWindow(IWindowTracker tracker, DxgiBackdropSampler? sampler, GlassOpacityPreset preset, bool isDarkMode)
    {
        InitializeComponent();
        _tracker = tracker;
        GlassBackdrop.OpacityPreset = preset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null) GlassBackdrop.AttachSampler(sampler);

        RefreshList();
        SourceInitialized += (_, _) => WindowInteropHelperEx.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Closed += (_, _) => GlassBackdrop.Dispose();
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    private void RefreshList()
    {
        var items = _tracker.GetTopLevelWindows()
            .GroupBy(w => w.ProcessId)
            .Select(g => new AppEntry(g.Key, $"{g.First().AppDisplayName} (PID {g.Key})"))
            .OrderBy(a => a.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppsList.ItemsSource = items;
    }

    private void OnForceQuitClick(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppEntry entry) return;
        try
        {
            using var proc = Process.GetProcessById((int)entry.ProcessId);
            proc.Kill(entireProcessTree: false);
        }
        catch { }

        RefreshList();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
