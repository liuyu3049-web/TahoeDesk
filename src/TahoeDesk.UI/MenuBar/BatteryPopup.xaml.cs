using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using TahoeDesk.Core.Config;
using TahoeDesk.Core.SystemControl;
using TahoeDesk.UI.Glass;

namespace TahoeDesk.UI.MenuBar;

public partial class BatteryPopup : Popup
{
    private readonly PowerModeService _powerService = new();
    private readonly OutsideClickDismissHook _dismissHook;

    public BatteryPopup()
    {
        InitializeComponent();
        _dismissHook = new OutsideClickDismissHook(
            CollectOpenHwnds,
            () => Dispatcher.BeginInvoke(() => IsOpen = false));

        Opened += (_, _) =>
        {
            WindowInteropHelperEx.ExcludeVisualFromCapture(Child);
            _dismissHook.Start();
            RefreshState();
        };
        Closed += (_, _) => _dismissHook.Stop();
    }

    public nint OwnerAnchorHwnd { get; set; }

    private IEnumerable<nint> CollectOpenHwnds()
    {
        if (OwnerAnchorHwnd != 0) yield return OwnerAnchorHwnd;
        if (Child is not null && PresentationSource.FromVisual(Child) is HwndSource src && src.Handle != 0)
        {
            yield return src.Handle;
        }
    }

    public void ConfigureVisuals(DxgiBackdropSampler? sampler, GlassOpacityPreset preset, bool isDarkMode)
    {
        GlassBackdrop.OpacityPreset = preset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null)
        {
            GlassBackdrop.AttachSampler(sampler);
        }

        Resources["BatFgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x12, 0x13, 0x18));
        Resources["BatSubtleBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x92, 0x12, 0x13, 0x18));
        Resources["BatDividerBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        Resources["BatBtnBgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        Resources["BatHoverBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
    }

    private void RefreshState()
    {
        var detail = _powerService.GetBatteryDetail();
        PercentHeaderText.Text = $"{detail.Percent}%";

        if (detail.IsCharging)
        {
            StatusSubText.Text = detail.Percent >= 100 ? "已充满 · 电源适配器供电" : "正在充电 · 电源适配器供电";
        }
        else if (detail.RemainingMinutes > 0)
        {
            int h = detail.RemainingMinutes / 60;
            int m = detail.RemainingMinutes % 60;
            StatusSubText.Text = h > 0 ? $"使用电池 · 预计剩余 {h} 小时 {m} 分钟" : $"使用电池 · 预计剩余 {m} 分钟";
        }
        else
        {
            StatusSubText.Text = "使用电池供电";
        }

        ApplySelectedMode(detail.PowerMode);
    }

    private void ApplySelectedMode(WindowsPowerMode mode)
    {
        EfficiencyCheck.Visibility = mode == WindowsPowerMode.BestPowerEfficiency ? Visibility.Visible : Visibility.Collapsed;
        BalancedCheck.Visibility = mode == WindowsPowerMode.Balanced ? Visibility.Visible : Visibility.Collapsed;
        PerformanceCheck.Visibility = mode == WindowsPowerMode.BestPerformance ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEfficiencyClick(object sender, RoutedEventArgs e) => SelectMode(WindowsPowerMode.BestPowerEfficiency);
    private void OnBalancedClick(object sender, RoutedEventArgs e) => SelectMode(WindowsPowerMode.Balanced);
    private void OnPerformanceClick(object sender, RoutedEventArgs e) => SelectMode(WindowsPowerMode.BestPerformance);

    private void SelectMode(WindowsPowerMode mode)
    {
        _powerService.SetPowerMode(mode);
        ApplySelectedMode(_powerService.GetCurrentPowerMode());
    }

    private void OnOpenPowerSettingsClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); } catch { }
    }
}
