using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace TahoeDesk.Core.SystemControl;

/// <summary>
/// WinRT Radio + Native Wifi (wlanapi.dll) controller for Wi-Fi SSID/list/direct-connect and Bluetooth power.
/// </summary>
public sealed class RadioControlService
{
    private const uint WlanIntfOpcodeCurrentConnection = 7;
    private const uint WlanAvailableNetworkIncludeAllProfileTypes = 0x00000002;
    private const uint WlanAvailableNetworkConnected = 0x00000001;
    private const uint WlanAvailableNetworkHasProfile = 0x00000002;
    private const int WlanConnectionModeProfile = 0;
    private const int Dot11BssTypeAny = 3;

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

    public IReadOnlyList<WifiNetworkInfo> GetAvailableWifiNetworks()
    {
        nint clientHandle = 0;
        nint ifaceListPtr = 0;
        nint netListPtr = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out clientHandle) != 0) return [];
            if (WlanEnumInterfaces(clientHandle, 0, out ifaceListPtr) != 0 || ifaceListPtr == 0) return [];

            int ifaceCount = Marshal.ReadInt32(ifaceListPtr, 0);
            if (ifaceCount <= 0) return [];

            var guidBytes = new byte[16];
            Marshal.Copy(ifaceListPtr + 8, guidBytes, 0, 16);
            var ifaceGuid = new Guid(guidBytes);

            if (WlanGetAvailableNetworkList(clientHandle, ref ifaceGuid, WlanAvailableNetworkIncludeAllProfileTypes, 0, out netListPtr) != 0
                || netListPtr == 0)
            {
                return [];
            }

            int count = Marshal.ReadInt32(netListPtr, 0);
            var bySsid = new Dictionary<string, WifiNetworkInfo>(StringComparer.OrdinalIgnoreCase);

            // WLAN_AVAILABLE_NETWORK is 628 bytes each, starting at offset 8 of WLAN_AVAILABLE_NETWORK_LIST
            const int entrySize = 628;
            for (int i = 0; i < count; i++)
            {
                nint entryPtr = netListPtr + 8 + i * entrySize;
                int ssidLen = Marshal.ReadInt32(entryPtr, 512);
                if (ssidLen <= 0 || ssidLen > 32) continue;

                byte[] ssidBytes = new byte[ssidLen];
                Marshal.Copy(entryPtr + 516, ssidBytes, 0, ssidLen);
                string ssid = Encoding.UTF8.GetString(ssidBytes).Trim();
                if (string.IsNullOrWhiteSpace(ssid)) continue;

                string profile = (Marshal.PtrToStringUni(entryPtr, 256) ?? string.Empty).Split('\0')[0].Trim();
                int signal = Math.Clamp(Marshal.ReadInt32(entryPtr, 604), 0, 100);
                bool secured = Marshal.ReadInt32(entryPtr, 608) != 0;
                uint flags = unchecked((uint)Marshal.ReadInt32(entryPtr, 620));
                bool connected = (flags & WlanAvailableNetworkConnected) != 0;
                bool hasProfile = (flags & WlanAvailableNetworkHasProfile) != 0 && !string.IsNullOrEmpty(profile);

                var info = new WifiNetworkInfo(ssid, hasProfile ? profile : null, signal, connected, secured, hasProfile);
                if (!bySsid.TryGetValue(ssid, out var existing)
                    || info.IsConnected
                    || (!existing.IsConnected && info.HasSavedProfile && !existing.HasSavedProfile)
                    || (!existing.IsConnected && info.HasSavedProfile == existing.HasSavedProfile && info.SignalQuality > existing.SignalQuality))
                {
                    bySsid[ssid] = info;
                }
            }

            return bySsid.Values
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.HasSavedProfile)
                .ThenByDescending(n => n.SignalQuality)
                .Take(10)
                .ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            if (netListPtr != 0) WlanFreeMemory(netListPtr);
            if (ifaceListPtr != 0) WlanFreeMemory(ifaceListPtr);
            if (clientHandle != 0) WlanCloseHandle(clientHandle, 0);
        }
    }

    public bool ConnectToSavedWifiProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;
        nint clientHandle = 0;
        nint ifaceListPtr = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out clientHandle) != 0) return false;
            if (WlanEnumInterfaces(clientHandle, 0, out ifaceListPtr) != 0 || ifaceListPtr == 0) return false;

            int ifaceCount = Marshal.ReadInt32(ifaceListPtr, 0);
            if (ifaceCount <= 0) return false;

            var guidBytes = new byte[16];
            Marshal.Copy(ifaceListPtr + 8, guidBytes, 0, 16);
            var ifaceGuid = new Guid(guidBytes);

            var connParams = new WLAN_CONNECTION_PARAMETERS
            {
                wlanConnectionMode = WlanConnectionModeProfile,
                strProfile = profileName,
                pDot11Ssid = IntPtr.Zero,
                pDesiredBssidList = IntPtr.Zero,
                dot11BssType = Dot11BssTypeAny,
                dwFlags = 0
            };

            return WlanConnect(clientHandle, ref ifaceGuid, ref connParams, 0) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ifaceListPtr != 0) WlanFreeMemory(ifaceListPtr);
            if (clientHandle != 0) WlanCloseHandle(clientHandle, 0);
        }
    }

    public IReadOnlyList<BluetoothDeviceInfo> GetPairedBluetoothDevices()
    {
        var list = new List<BluetoothDeviceInfo>();
        try
        {
            var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                fReturnAuthenticated = true,
                fReturnRemembered = true,
                fReturnUnknown = false,
                fReturnConnected = true,
                fIssueInquiry = false,
                cTimeoutMultiplier = 1,
                hRadio = 0
            };

            var info = new BLUETOOTH_DEVICE_INFO
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>()
            };

            nint findHandle = BluetoothFindFirstDevice(ref search, ref info);
            if (findHandle == 0) return list;

            try
            {
                do
                {
                    string name = (info.szName ?? string.Empty).Trim('\0', ' ');
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(new BluetoothDeviceInfo(name, info.Address, info.fConnected, info.fRemembered));
                    }
                    info.dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();
                }
                while (BluetoothFindNextDevice(findHandle, ref info));
            }
            finally
            {
                BluetoothFindDeviceClose(findHandle);
            }
        }
        catch { }

        return list
            .OrderByDescending(d => d.IsConnected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .ToList();
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType;
        public uint dwFlags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(nint hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, nint pReserved, out nint ppAvailableNetworkList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanConnect(nint hClientHandle, ref Guid pInterfaceGuid, ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, nint pReserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint pMemory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public nint hRadio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern nint BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(nint hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(nint hFind);
}
