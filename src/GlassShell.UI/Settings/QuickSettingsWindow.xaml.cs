using System;
using System.Windows;
using System.Windows.Controls;
using GlassShell.Core.Config;
using GlassShell.UI.Glass;

namespace GlassShell.UI.Settings;

public partial class QuickSettingsWindow : Window
{
    private readonly Action<GlassSettings> _onUpdated;
    private readonly Action _onRestoreAndExit;
    private GlassSettings _current;
    private bool _loading = true;

    public QuickSettingsWindow(
        GlassSettings settings,
        DxgiBackdropSampler sampler,
        bool isDarkMode,
        Action<GlassSettings> onUpdated,
        Action onRestoreAndExit)
    {
        InitializeComponent();
        _current = settings;
        _onUpdated = onUpdated;
        _onRestoreAndExit = onRestoreAndExit;

        GlassBackdrop.AttachSampler(sampler);
        GlassBackdrop.OpacityPreset = settings.OpacityPreset;
        GlassBackdrop.IsDarkMode = isDarkMode;

        SelectComboByTag(ThemeCombo, settings.Theme.ToString());
        SelectComboByTag(OpacityCombo, settings.OpacityPreset.ToString());
        SelectComboByTag(AutoHideCombo, settings.DockAutoHide.ToString());
        MenuBarBgCheck.IsChecked = settings.ShowMenuBarBackground;
        IconSizeSlider.Value = settings.DockIconSize;
        MagnificationCheck.IsChecked = settings.DockMagnificationEnabled;
        MagScaleSlider.Value = settings.DockMagnificationScale;
        RecentAppsCheck.IsChecked = settings.ShowRecentApps;
        _loading = false;

        SourceInitialized += (_, _) => WindowInteropHelperEx.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Closed += (_, _) => GlassBackdrop.Dispose();
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
        };
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

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var theme = Enum.TryParse<ThemeMode>((ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var t) ? t : _current.Theme;
        var opacity = Enum.TryParse<GlassOpacityPreset>((OpacityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var o) ? o : _current.OpacityPreset;
        var autoHide = Enum.TryParse<DockAutoHideMode>((AutoHideCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var a) ? a : _current.DockAutoHide;

        _current = _current with
        {
            Theme = theme,
            OpacityPreset = opacity,
            ShowMenuBarBackground = MenuBarBgCheck.IsChecked == true,
            DockIconSize = (int)Math.Round(IconSizeSlider.Value),
            DockMagnificationEnabled = MagnificationCheck.IsChecked == true,
            DockMagnificationScale = Math.Round(MagScaleSlider.Value, 2),
            DockAutoHide = autoHide,
            ShowRecentApps = RecentAppsCheck.IsChecked == true
        };

        GlassBackdrop.OpacityPreset = _current.OpacityPreset;
        _onUpdated(_current);
    }

    private void OnRestoreAndExitClick(object sender, RoutedEventArgs e)
    {
        Close();
        _onRestoreAndExit();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
