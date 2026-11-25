using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GlassShell.Core.Config;
using GlassShell.Core.Diagnostics;
using GlassShell.Core.Shell;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Settings;

public partial class QuickSettingsWindow : Window
{
    private readonly Action<GlassSettings> _onUpdated;
    private readonly Action _onRestoreAndExit;
    private readonly Func<string?>? _onExportDiagnostics;
    private GlassSettings _current;
    private bool _loading = true;

    public QuickSettingsWindow(
        GlassSettings settings,
        DxgiBackdropSampler sampler,
        bool isDarkMode,
        Action<GlassSettings> onUpdated,
        Action onRestoreAndExit,
        Func<string?>? onExportDiagnostics = null)
    {
        InitializeComponent();
        _current = settings;
        _onUpdated = onUpdated;
        _onRestoreAndExit = onRestoreAndExit;
        _onExportDiagnostics = onExportDiagnostics;

        GlassBackdrop.AttachSampler(sampler);
        GlassBackdrop.OpacityPreset = settings.OpacityPreset;
        ApplyWindowTheme(isDarkMode);

        SelectComboByTag(ThemeCombo, settings.Theme.ToString());
        SelectComboByTag(OpacityCombo, settings.OpacityPreset.ToString());
        SelectComboByTag(AutoHideCombo, settings.DockAutoHide.ToString());
        MenuBarBgCheck.IsChecked = settings.ShowMenuBarBackground;
        IconSizeSlider.Value = settings.DockIconSize;
        MagnificationCheck.IsChecked = settings.DockMagnificationEnabled;
        MagScaleSlider.Value = settings.DockMagnificationScale;
        HarmonizeIconsCheck.IsChecked = settings.HarmonizeDockIcons;
        RecentAppsCheck.IsChecked = settings.ShowRecentApps;
        WinKeyLaunchpadCheck.IsChecked = settings.InterceptWinKeyForLaunchpad;
        SelectComboByTag(LaunchpadColsCombo, settings.LaunchpadColumns.ToString());
        SelectComboByTag(LaunchpadRowsCombo, settings.LaunchpadRows.ToString());
        AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
        VersionFooterText.Text = $"TahoeDesk v{DiagnosticsReportBuilder.AppVersion} · Liquid Glass Desktop Shell";
        _loading = false;

        SourceInitialized += (_, _) => WindowInteropHelperEx.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Closed += (_, _) => GlassBackdrop.Dispose();
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };
    }

    private void ApplyWindowTheme(bool isDarkMode)
    {
        GlassBackdrop.IsDarkMode = isDarkMode;

        static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        Resources["SettingsFgBrush"] = Frozen(
            isDarkMode ? Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x12, 0x13, 0x18));
        Resources["SettingsSubtleBrush"] = Frozen(
            isDarkMode ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0x12, 0x13, 0x18));
        Resources["SettingsCardBgBrush"] = Frozen(
            isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Resources["SettingsCardBorderBrush"] = Frozen(
            isDarkMode ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        Resources["SettingsCloseBgBrush"] = Frozen(
            isDarkMode ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void OnComboDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo && combo.Template?.FindName("PART_Popup", combo) is Popup popup)
        {
            WindowInteropHelperEx.ExcludeVisualFromCapture(popup.Child);
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var theme = Enum.TryParse<ThemeMode>((ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var t) ? t : _current.Theme;
        var opacity = Enum.TryParse<GlassOpacityPreset>((OpacityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var o) ? o : _current.OpacityPreset;
        var autoHide = Enum.TryParse<DockAutoHideMode>((AutoHideCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var a) ? a : _current.DockAutoHide;
        int lpCols = int.TryParse((LaunchpadColsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int c) ? c : _current.LaunchpadColumns;
        int lpRows = int.TryParse((LaunchpadRowsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int r) ? r : _current.LaunchpadRows;

        _current = _current with
        {
            Theme = theme,
            OpacityPreset = opacity,
            ShowMenuBarBackground = MenuBarBgCheck.IsChecked == true,
            DockIconSize = (int)Math.Round(IconSizeSlider.Value),
            DockMagnificationEnabled = MagnificationCheck.IsChecked == true,
            DockMagnificationScale = Math.Round(MagScaleSlider.Value, 2),
            DockAutoHide = autoHide,
            HarmonizeDockIcons = HarmonizeIconsCheck.IsChecked == true,
            ShowRecentApps = RecentAppsCheck.IsChecked == true,
            InterceptWinKeyForLaunchpad = WinKeyLaunchpadCheck.IsChecked == true,
            LaunchpadColumns = lpCols,
            LaunchpadRows = lpRows
        };

        bool resolvedDark = theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => WindowInteropHelperEx.IsSystemDarkTheme()
        };

        GlassBackdrop.OpacityPreset = _current.OpacityPreset;
        ApplyWindowTheme(resolvedDark);
        _onUpdated(_current);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            AutoStartService.SetEnabled(AutoStartCheck.IsChecked == true, exePath);
        }
    }

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        string? savedPath = _onExportDiagnostics?.Invoke();
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            DiagStatusText.Text = $"已导出至桌面：{System.IO.Path.GetFileName(savedPath)}";
        }
        else
        {
            DiagStatusText.Text = "导出失败，请重试";
        }
    }

    private void OnRestoreAndExitClick(object sender, RoutedEventArgs e)
    {
        Close();
        _onRestoreAndExit();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
