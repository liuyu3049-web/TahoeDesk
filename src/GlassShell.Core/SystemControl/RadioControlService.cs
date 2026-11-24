using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace GlassShell.Core.SystemControl;

/// <summary>
/// WinRT Radio + Native Wifi (wlanapi.dll) controller for Wi-Fi SSID/power and Bluetooth power.
/// </summary>
public sealed class RadioControlService
{
    private const uint WlanIntfOpcodeCurrentConnection = 7;

    public async Task<RadioState> GetStateAsync()
    {
        bool hasWifi = false;
        bool wifiOn = false;
        bool hasBt = false;
        bool btOn = false;

        try
        {
            var radios = await Radio.GetRadiosAsync();
            foreach (var radio in radios)
            {
                if (radio.Kind == RadioKind.WiFi)
                {
                    hasWifi = true;
                    wifiOn = radio.State == RadioStateKindToBool(radio.State);
                }
                else if (radio.Kind == RadioKind.Bluetooth)
                {
                    hasBt = true;
                    btOn = radio.State == RadioStateKindToBool(radio.State);
                }
            }
        }
        catch
        {
            // Fallback gracefully if WinRT Radio API is unavailable in sandbox/headless environment
        }

        string? ssid = wifiOn ? TryGetCurrentWifiSsid() : null;
        return new RadioState(hasWifi, wifiOn, ssid, hasBt, btOn);
    }

    public Task<bool> SetWifiEnabledAsync(bool enabled) => SetRadioKindAsync(RadioKind.WiFi, enabled);
    public Task<bool> SetBluetoothEnabledAsync(bool enabled) => SetRadioKindAsync(RadioKind.Bluetooth, enabled);

    private static Windows.Devices.Radios.RadioState RadioStateKindToBool(Windows.Devices.Radios.RadioState _)
        => Windows.Devices.Radios.RadioState.On;

    private static async Task<bool> SetRadioKindAsync(RadioKind kind, bool enabled)
    {
        try
        {
            await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            bool any = false;
            var target = enabled ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off;
            foreach (var radio in radios)
            {
                if (radio.Kind == kind)
                {
                    await radio.SetStateAsync(target);
                    any = true;
                }
            }
            return any;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetCurrentWifiSsid()
    {
        nint clientHandle = 0;
        nint ifaceListPtr = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out clientHandle) != 0) return null;
            if (WlanEnumInterfaces(clientHandle, 0, out ifaceListPtr) != 0 || ifaceListPtr == 0) return null;

            int count = Marshal.ReadInt32(ifaceListPtr, 0);
            if (count <= 0) return null;

            // WLAN_INTERFACE_INFO starts at offset 8; first 16 bytes are InterfaceGuid
            var guidBytes = new byte[16];
            Marshal.Copy(ifaceListPtr + 8, guidBytes, 0, 16);
            var ifaceGuid = new Guid(guidBytes);

            if (WlanQueryInterface(clientHandle, ref ifaceGuid, WlanIntfOpcodeCurrentConnection, 0, out int dataSize, out nint dataPtr, out _) != 0
                || dataPtr == 0 || dataSize < 600)
            {
                return null;
            }

            try
            {
                // WLAN_CONNECTION_ATTRIBUTES: isState (4) + wlanConnectionMode (4) + strProfileName (512 bytes UTF-16) = 520,
                // then wlanAssociationAttributes.dot11Ssid: uSSIDLength (4 at +520) + ucSSID[32] (at +524)
                int ssidLen = Marshal.ReadInt32(dataPtr, 520);
                if (ssidLen <= 0 || ssidLen > 32) return null;
                byte[] ssidBytes = new byte[ssidLen];
                Marshal.Copy(dataPtr + 524, ssidBytes, 0, ssidLen);
                return Encoding.UTF8.GetString(ssidBytes).Trim();
            }
            finally
            {
                WlanFreeMemory(dataPtr);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ifaceListPtr != 0) WlanFreeMemory(ifaceListPtr);
            if (clientHandle != 0) WlanCloseHandle(clientHandle, 0);
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, nint pReserved, out uint pdwNegotiatedVersion, out nint phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint hClientHandle, nint pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint hClientHandle, nint pReserved, out nint ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(nint hClientHandle, ref Guid pInterfaceGuid, uint opCode, nint pReserved, out int pdwDataSize, out nint ppData, out uint pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint pMemory);
}
