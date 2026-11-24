using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassShell.Core.Config;
using GlassShell.Core.MenuEngine;
using GlassShell.Core.SystemControl;
using GlassShell.UI.Glass;

namespace GlassShell.UI.ControlCenter;

public partial class ControlCenterPopup : Popup
{
    public enum SubPage
    {
        Main,
        Wifi,
        Bluetooth
    }

    private readonly AudioControlService _audio = new();
    private readonly BrightnessControlService _brightness = new();
    private readonly RadioControlService _radio = new();
    private readonly MediaSessionService _media = new();
    private readonly MenuCommandDispatcher _dispatcher = new();
    private readonly OutsideClickDismissHook _dismissHook;

    private ThemeMode _currentTheme = ThemeMode.System;
    private RadioState _radioState = RadioState.Unavailable;
    private bool _stageManagerEnabled;
    private bool _suppressSliderEvents;
    private SubPage _activePage = SubPage.Main;
    private int _pendingBrightness = -1;
    private int _brightnessWorkerActive;

    public ControlCenterPopup()
    {
        InitializeComponent();
        _dismissHook = new OutsideClickDismissHook(
            CollectOpenHwnds,
            () => Dispatcher.BeginInvoke(() => IsOpen = false));

        Opened += (_, _) =>
        {
            WindowInteropHelperEx.ExcludeVisualFromCapture(Child);
            _dismissHook.Start();
            _ = RefreshLiveStateAsync();
        };
        Closed += (_, _) =>
        {
            _dismissHook.Stop();
            SwitchPage(SubPage.Main);
        };
    }

    public nint OwnerAnchorHwnd { get; set; }

    public event Action<ThemeMode>? ThemeChangeRequested;
    public event Action<bool>? StageManagerToggled;

    public void SwitchPage(SubPage page)
    {
        _activePage = page;
        MainBentoView.Visibility = page == SubPage.Main ? Visibility.Visible : Visibility.Collapsed;
        WifiSubPage.Visibility = page == SubPage.Wifi ? Visibility.Visible : Visibility.Collapsed;
        BluetoothSubPage.Visibility = page == SubPage.Bluetooth ? Visibility.Visible : Visibility.Collapsed;

        if (IsOpen)
        {
            if (page == SubPage.Wifi) _ = RefreshWifiListAsync();
            else if (page == SubPage.Bluetooth) _ = RefreshBluetoothListAsync();
        }
    }

    public void SetStageManagerState(bool enabled)
    {
        _stageManagerEnabled = enabled;
        StageManagerCircle.Background = enabled
            ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x45, 0x8E, 0x8E, 0x93));
        StageManagerStatusText.Text = enabled ? "单应用聚焦" : "已关闭";
    }

    private IEnumerable<nint> CollectOpenHwnds()
    {
        if (OwnerAnchorHwnd != 0) yield return OwnerAnchorHwnd;
        if (Child is not null && PresentationSource.FromVisual(Child) is HwndSource src && src.Handle != 0)
        {
            yield return src.Handle;
        }
    }

    public void ConfigureVisuals(DxgiBackdropSampler? sampler, GlassSettings settings, bool isDarkMode)
    {
        _currentTheme = settings.Theme;
        GlassBackdrop.OpacityPreset = settings.OpacityPreset;
        GlassBackdrop.IsDarkMode = isDarkMode;
        if (sampler is not null)
        {
            GlassBackdrop.AttachSampler(sampler);
        }

        Resources["CcFgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x12, 0x13, 0x18));
        Resources["CcSubtleBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x92, 0x12, 0x13, 0x18));
        Resources["CcCardBgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF));
        Resources["CcCardBorderBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        Resources["CcBtnBgBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        Resources["CcHoverPillBrush"] = new SolidColorBrush(isDarkMode ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));

        ThemeModeText.Text = settings.Theme switch
        {
            ThemeMode.Dark => "深色",
            ThemeMode.Light => "浅色",
            _ => "跟随系统"
        };
    }

    public async Task RefreshLiveStateAsync()
    {
        var audio = _audio.GetState();
        bool hasBrightness = await Task.Run(() => _brightness.TryGetBrightness(out int b) ? b : -1) is int brightVal && brightVal >= 0;
        int brightPct = hasBrightness ? brightVal : 80;

        _suppressSliderEvents = true;
        try
        {
            VolumeSlider.Value = audio.VolumePercent;
            VolumeValueText.Text = audio.IsMuted ? "静音" : $"{audio.VolumePercent}%";
            MuteGlyph.Text = audio.IsMuted || audio.VolumePercent == 0 ? "🔇" : (audio.VolumePercent < 45 ? "🔉" : "🔊");

            BrightnessCard.Visibility = hasBrightness ? Visibility.Visible : Visibility.Collapsed;
            if (hasBrightness)
            {
                BrightnessSlider.Value = brightPct;
                BrightnessValueText.Text = $"{brightPct}%";
            }
        }
        finally
        {
            _suppressSliderEvents = false;
        }

        var radioTask = _radio.GetStateAsync();
        var mediaTask = _media.GetCurrentMediaAsync();
        await Task.WhenAll(radioTask, mediaTask);

        _radioState = radioTask.Result;
        ApplyRadioUi(_radioState);
        ApplyMediaUi(mediaTask.Result);

        if (_activePage == SubPage.Wifi) await RefreshWifiListAsync();
        else if (_activePage == SubPage.Bluetooth) await RefreshBluetoothListAsync();
    }

    private void ApplyRadioUi(RadioState state)
    {
        var activeBlue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var offGray = new SolidColorBrush(Color.FromArgb(0x45, 0x8E, 0x8E, 0x93));

        WifiToggleBtn.IsEnabled = state.HasWifi;
        WifiCircle.Background = state.HasWifi && state.WifiOn ? activeBlue : offGray;
        WifiStatusText.Text = !state.HasWifi
            ? "不可用"
            : (state.WifiOn ? (string.IsNullOrWhiteSpace(state.WifiSsid) ? "已开启" : state.WifiSsid) : "已关闭");
        WifiSubToggleText.Text = state.WifiOn ? "已开启" : "已关闭";

        BtToggleBtn.IsEnabled = state.HasBluetooth;
        BtCircle.Background = state.HasBluetooth && state.BluetoothOn ? activeBlue : offGray;
        BtStatusText.Text = !state.HasBluetooth ? "不可用" : (state.BluetoothOn ? "已开启" : "已关闭");
        BtSubToggleText.Text = state.BluetoothOn ? "已开启" : "已关闭";
    }

    private async Task RefreshWifiListAsync()
    {
        var networks = await Task.Run(() => _radio.GetAvailableWifiNetworks());
        WifiNetworksList.Children.Clear();

        var fgBrush = Resources["CcFgBrush"] as Brush ?? Brushes.White;
        var subBrush = Resources["CcSubtleBrush"] as Brush ?? Brushes.Gray;
        var hoverBrush = Resources["CcHoverPillBrush"] as Brush ?? Brushes.Transparent;

        if (networks.Count == 0)
        {
            WifiNetworksList.Children.Add(new TextBlock
            {
                Text = _radioState.WifiOn ? "未扫描到附近 Wi-Fi" : "Wi-Fi 已关闭，请点击右上角开启",
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = subBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 22, 8, 22)
            });
            return;
        }

        foreach (var net in networks)
        {
            var dock = new DockPanel { LastChildFill = true };
            var status = new TextBlock
            {
                Text = net.IsConnected ? "✓ 已连接" : (net.HasSavedProfile ? $"{net.SignalQuality}%" : "需密码"),
                FontSize = 11,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = net.IsConnected ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)) : subBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(status, System.Windows.Controls.Dock.Right);
            dock.Children.Add(status);

            var badge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = net.IsConnected
                    ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x38, 0x8E, 0x8E, 0x93)),
                Margin = new Thickness(0, 0, 9, 0),
                Child = new TextBlock
                {
                    Text = net.IsSecured ? "🔒" : "Wi",
                    FontSize = 10,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(badge, System.Windows.Controls.Dock.Left);
            dock.Children.Add(badge);

            dock.Children.Add(new TextBlock
            {
                Text = net.Ssid,
                FontSize = 12.5,
                FontWeight = net.IsConnected ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = fgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var row = new Border
            {
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(9),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = dock
            };
            row.MouseEnter += (_, _) => row.Background = hoverBrush;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                if (net.IsConnected) return;

                if (net.HasSavedProfile && !string.IsNullOrWhiteSpace(net.ProfileName))
                {
                    status.Text = "连接中…";
                    await Task.Run(() => _radio.ConnectToSavedWifiProfile(net.ProfileName));
                    await Task.Delay(900);
                    await RefreshLiveStateAsync();
                }
                else
                {
                    IsOpen = false;
                    LaunchUri("ms-availablenetworks:");
                }
            };

            WifiNetworksList.Children.Add(row);
        }
    }

    private async Task RefreshBluetoothListAsync()
    {
        var devices = await Task.Run(() => _radio.GetPairedBluetoothDevices());
        BluetoothDevicesList.Children.Clear();

        var fgBrush = Resources["CcFgBrush"] as Brush ?? Brushes.White;
        var subBrush = Resources["CcSubtleBrush"] as Brush ?? Brushes.Gray;
        var hoverBrush = Resources["CcHoverPillBrush"] as Brush ?? Brushes.Transparent;

        if (devices.Count == 0)
        {
            BluetoothDevicesList.Children.Add(new TextBlock
            {
                Text = _radioState.BluetoothOn ? "暂无已配对蓝牙设备" : "蓝牙已关闭，请点击右上角开启",
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = subBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 22, 8, 22)
            });
            return;
        }

        foreach (var dev in devices)
        {
            var dock = new DockPanel { LastChildFill = true };
            var status = new TextBlock
            {
                Text = dev.IsConnected ? "已连接" : "已配对",
                FontSize = 11,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = dev.IsConnected ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)) : subBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(status, System.Windows.Controls.Dock.Right);
            dock.Children.Add(status);

            var badge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = dev.IsConnected
                    ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x38, 0x8E, 0x8E, 0x93)),
                Margin = new Thickness(0, 0, 9, 0),
                Child = new TextBlock
                {
                    Text = "BT",
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(badge, System.Windows.Controls.Dock.Left);
            dock.Children.Add(badge);

            dock.Children.Add(new TextBlock
            {
                Text = dev.Name,
                FontSize = 12.5,
                FontWeight = dev.IsConnected ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = fgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            var row = new Border
            {
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(9),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = dock
            };
            row.MouseEnter += (_, _) => row.Background = hoverBrush;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                IsOpen = false;
                LaunchUri("ms-settings:bluetooth");
            };

            BluetoothDevicesList.Children.Add(row);
        }
    }

    private void ApplyMediaUi(MediaSessionInfo media)
    {
        if (!media.HasMedia)
        {
            MediaTitleText.Text = "未在播放";
            MediaArtistText.Text = "打开音乐或视频即可在此控制";
            PlayPauseGlyph.Text = "▶";
            MediaArtworkImage.Source = null;
            MediaFallbackGlyph.Visibility = Visibility.Visible;
            return;
        }

        MediaTitleText.Text = media.Title;
        MediaArtistText.Text = media.FormattedSubtitle;
        PlayPauseGlyph.Text = media.IsPlaying ? "⏸" : "▶";

        if (media.ThumbnailBytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(media.ThumbnailBytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                MediaArtworkImage.Source = bmp;
                MediaFallbackGlyph.Visibility = Visibility.Collapsed;
                return;
            }
            catch { }
        }

        MediaArtworkImage.Source = null;
        MediaFallbackGlyph.Visibility = Visibility.Visible;
    }

    private void OnBackToMainClick(object sender, RoutedEventArgs e) => SwitchPage(SubPage.Main);
    private void OnOpenWifiSubPage(object sender, RoutedEventArgs e) => SwitchPage(SubPage.Wifi);
    private void OnOpenBluetoothSubPage(object sender, RoutedEventArgs e) => SwitchPage(SubPage.Bluetooth);

    private async void OnWifiToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_radioState.HasWifi) return;
        await _radio.SetWifiEnabledAsync(!_radioState.WifiOn);
        await Task.Delay(260);
        await RefreshLiveStateAsync();
    }

    private async void OnBluetoothToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_radioState.HasBluetooth) return;
        await _radio.SetBluetoothEnabledAsync(!_radioState.BluetoothOn);
        await Task.Delay(260);
        await RefreshLiveStateAsync();
    }

    private void OnOpenAvailableNetworksFlyout(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        LaunchUri("ms-availablenetworks:");
    }

    private void OnOpenBluetoothSettings(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        LaunchUri("ms-settings:bluetooth");
    }

    private void OnClipboardClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        _dispatcher.Dispatch(0, new MenuInvokeAction(MenuActionKind.KeyboardShortcut, Shortcut: "Win+V"));
    }

    private void OnFocusClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        LaunchUri("ms-settings:quiethours");
    }

    private void OnThemeCycleClick(object sender, RoutedEventArgs e)
    {
        var next = _currentTheme switch
        {
            ThemeMode.System => ThemeMode.Dark,
            ThemeMode.Dark => ThemeMode.Light,
            _ => ThemeMode.System
        };
        _currentTheme = next;
        ThemeChangeRequested?.Invoke(next);
    }

    private void OnStageManagerClick(object sender, RoutedEventArgs e)
    {
        SetStageManagerState(!_stageManagerEnabled);
        StageManagerToggled?.Invoke(_stageManagerEnabled);
    }

    private void OnMissionControlClick(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        _dispatcher.Dispatch(0, new MenuInvokeAction(MenuActionKind.KeyboardShortcut, Shortcut: "Win+Tab"));
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        int pct = (int)Math.Round(e.NewValue);
        BrightnessValueText.Text = $"{pct}%";

        // Coalesce slow DDC/CI hardware I2C commands on a single background worker -> 120fps buttery slider!
        Interlocked.Exchange(ref _pendingBrightness, pct);
        if (Interlocked.CompareExchange(ref _brightnessWorkerActive, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        int target = Interlocked.Exchange(ref _pendingBrightness, -1);
                        if (target < 0) break;
                        _brightness.TrySetBrightness(target);
                        await Task.Delay(35);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _brightnessWorkerActive, 0);
                    if (Volatile.Read(ref _pendingBrightness) >= 0 && Interlocked.CompareExchange(ref _brightnessWorkerActive, 1, 0) == 0)
                    {
                        int finalVal = Interlocked.Exchange(ref _pendingBrightness, -1);
                        if (finalVal >= 0) _brightness.TrySetBrightness(finalVal);
                        Interlocked.Exchange(ref _brightnessWorkerActive, 0);
                    }
                }
            });
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        int pct = (int)Math.Round(e.NewValue);
        VolumeValueText.Text = $"{pct}%";
        MuteGlyph.Text = pct == 0 ? "🔇" : (pct < 45 ? "🔉" : "🔊");
        _audio.SetVolumePercent(pct);
    }

    private void OnMuteToggleClick(object sender, RoutedEventArgs e)
    {
        _audio.ToggleMute();
        var state = _audio.GetState();
        VolumeValueText.Text = state.IsMuted ? "静音" : $"{state.VolumePercent}%";
        MuteGlyph.Text = state.IsMuted || state.VolumePercent == 0 ? "🔇" : (state.VolumePercent < 45 ? "🔉" : "🔊");
    }

    private async void OnPrevTrackClick(object sender, RoutedEventArgs e)
    {
        await _media.PreviousTrackAsync();
        await Task.Delay(250);
        ApplyMediaUi(await _media.GetCurrentMediaAsync());
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        await _media.TogglePlayPauseAsync();
        await Task.Delay(200);
        ApplyMediaUi(await _media.GetCurrentMediaAsync());
    }

    private async void OnNextTrackClick(object sender, RoutedEventArgs e)
    {
        await _media.NextTrackAsync();
        await Task.Delay(250);
        ApplyMediaUi(await _media.GetCurrentMediaAsync());
    }

    private static void LaunchUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }
}
