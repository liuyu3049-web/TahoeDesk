using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
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
    private readonly AudioControlService _audio = new();
    private readonly BrightnessControlService _brightness = new();
    private readonly RadioControlService _radio = new();
    private readonly MediaSessionService _media = new();
    private readonly MenuCommandDispatcher _dispatcher = new();
    private readonly OutsideClickDismissHook _dismissHook;

    private ThemeMode _currentTheme = ThemeMode.System;
    private RadioState _radioState = RadioState.Unavailable;
    private bool _suppressSliderEvents;

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
        Closed += (_, _) => _dismissHook.Stop();
    }

    public nint OwnerAnchorHwnd { get; set; }

    public event Action<ThemeMode>? ThemeChangeRequested;

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

        ThemeModeText.Text = settings.Theme switch
        {
            ThemeMode.Dark => "深色",
            ThemeMode.Light => "浅色",
            _ => "跟随系统"
        };
    }

    public async Task RefreshLiveStateAsync()
    {
        // Fast synchronous Audio & Brightness reads
        var audio = _audio.GetState();
        bool hasBrightness = _brightness.TryGetBrightness(out int brightPct);

        _suppressSliderEvents = true;
        try
        {
            VolumeSlider.Value = audio.VolumePercent;
            VolumeValueText.Text = audio.IsMuted ? "静音" : $"{audio.VolumePercent}%";
            MuteToggleButton.Content = audio.IsMuted || audio.VolumePercent == 0 ? "🔇" : (audio.VolumePercent < 45 ? "🔉" : "🔊");

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

        // Async WinRT Radio & Media reads
        var radioTask = _radio.GetStateAsync();
        var mediaTask = _media.GetCurrentMediaAsync();
        await Task.WhenAll(radioTask, mediaTask);

        _radioState = radioTask.Result;
        ApplyRadioUi(_radioState);
        ApplyMediaUi(mediaTask.Result);
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

        BtToggleBtn.IsEnabled = state.HasBluetooth;
        BtCircle.Background = state.HasBluetooth && state.BluetoothOn ? activeBlue : offGray;
        BtStatusText.Text = !state.HasBluetooth ? "不可用" : (state.BluetoothOn ? "已开启" : "已关闭");
    }

    private void ApplyMediaUi(MediaSessionInfo media)
    {
        if (!media.HasMedia)
        {
            MediaTitleText.Text = "未在播放";
            MediaArtistText.Text = "打开音乐或视频即可在此控制";
            PlayPauseBtn.Content = "▶";
            MediaArtworkImage.Source = null;
            MediaFallbackGlyph.Visibility = Visibility.Visible;
            return;
        }

        MediaTitleText.Text = media.Title;
        MediaArtistText.Text = media.FormattedSubtitle;
        PlayPauseBtn.Content = media.IsPlaying ? "⏸" : "▶";

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

    private async void OnWifiToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_radioState.HasWifi) return;
        bool next = !_radioState.WifiOn;
        await _radio.SetWifiEnabledAsync(next);
        await Task.Delay(250);
        await RefreshLiveStateAsync();
    }

    private async void OnBluetoothToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_radioState.HasBluetooth) return;
        bool next = !_radioState.BluetoothOn;
        await _radio.SetBluetoothEnabledAsync(next);
        await Task.Delay(250);
        await RefreshLiveStateAsync();
    }

    private void OnOpenWifiSettings(object sender, RoutedEventArgs e)
    {
        IsOpen = false;
        LaunchUri("ms-settings:network-wifi");
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

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        int pct = (int)Math.Round(e.NewValue);
        BrightnessValueText.Text = $"{pct}%";
        _brightness.TrySetBrightness(pct);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        int pct = (int)Math.Round(e.NewValue);
        VolumeValueText.Text = $"{pct}%";
        MuteToggleButton.Content = pct == 0 ? "🔇" : (pct < 45 ? "🔉" : "🔊");
        _audio.SetVolumePercent(pct);
    }

    private void OnMuteToggleClick(object sender, RoutedEventArgs e)
    {
        _audio.ToggleMute();
        var state = _audio.GetState();
        VolumeValueText.Text = state.IsMuted ? "静音" : $"{state.VolumePercent}%";
        MuteToggleButton.Content = state.IsMuted || state.VolumePercent == 0 ? "🔇" : (state.VolumePercent < 45 ? "🔉" : "🔊");
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
