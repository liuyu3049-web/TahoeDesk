using System;
using System.Collections.Generic;

namespace GlassShell.Core.SystemControl;

public sealed record AudioEndpointInfo(string Id, string FriendlyName, bool IsDefault);

public sealed record AudioState(int VolumePercent, bool IsMuted, bool IsAvailable, IReadOnlyList<AudioEndpointInfo> Devices)
{
    public static AudioState Unavailable => new(0, false, false, []);
}

public sealed record WifiNetworkInfo(
    string Ssid,
    string? ProfileName,
    int SignalQuality,
    bool IsConnected,
    bool IsSecured,
    bool HasSavedProfile);

public sealed record BluetoothDeviceInfo(
    string Name,
    ulong Address,
    bool IsConnected,
    bool IsRemembered);

public sealed record RadioState(
    bool HasWifi,
    bool WifiOn,
    string? WifiSsid,
    bool HasBluetooth,
    bool BluetoothOn)
{
    public static RadioState Unavailable => new(false, false, null, false, false);
}

public sealed record MediaSessionInfo(
    bool HasMedia,
    string Title,
    string Artist,
    bool IsPlaying,
    byte[]? ThumbnailBytes)
{
    public static MediaSessionInfo Empty => new(false, string.Empty, string.Empty, false, null);

    public string FormattedSubtitle => string.IsNullOrWhiteSpace(Artist)
        ? (HasMedia ? "正在播放" : "未在播放")
        : Artist.Trim();
}

public static class SystemControlHelpers
{
    public static int ClampPercent(int percent) => Math.Clamp(percent, 0, 100);

    public static float PercentToScalar(int percent) => ClampPercent(percent) / 100f;

    public static int ScalarToPercent(float scalar) => ClampPercent((int)Math.Round(scalar * 100f));
}
