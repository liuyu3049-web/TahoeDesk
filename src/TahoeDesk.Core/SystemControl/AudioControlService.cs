using System;
using System.Runtime.InteropServices;

namespace TahoeDesk.Core.SystemControl;

/// <summary>
/// Lightweight zero-dependency CoreAudio COM adapter for master volume (0-100) and mute control.
/// </summary>
public sealed class AudioControlService
{
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const uint ClsCtxAll = 23;
    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public AudioState GetState()
    {
        return WithDefaultEndpoint(vol =>
        {
            vol.GetMasterVolumeLevelScalar(out float scalar);
            vol.GetMute(out bool muted);
            return new AudioState(SystemControlHelpers.ScalarToPercent(scalar), muted, true, []);
        }, AudioState.Unavailable);
    }

    public bool SetVolumePercent(int percent)
    {
        float scalar = SystemControlHelpers.PercentToScalar(percent);
        return WithDefaultEndpoint(vol =>
        {
            Guid ctx = Guid.Empty;
            vol.SetMasterVolumeLevelScalar(scalar, ref ctx);
            if (percent > 0)
            {
                vol.SetMute(false, ref ctx);
            }
            return true;
        }, false);
    }

    public bool ToggleMute()
    {
        return WithDefaultEndpoint(vol =>
        {
            vol.GetMute(out bool muted);
            Guid ctx = Guid.Empty;
            vol.SetMute(!muted, ref ctx);
            return true;
        }, false);
    }

    private static T WithDefaultEndpoint<T>(Func<IAudioEndpointVolume, T> action, T fallback)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out device) != 0 || device is null)
            {
                return fallback;
            }

            Guid iid = IidIAudioEndpointVolume;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object activated) != 0 || activated is not IAudioEndpointVolume vol)
            {
                return fallback;
            }

            endpoint = vol;
            return action(endpoint);
        }
        catch
        {
            return fallback;
        }
        finally
        {
            if (endpoint is not null) Marshal.FinalReleaseComObject(endpoint);
            if (device is not null) Marshal.FinalReleaseComObject(device);
            if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCoClass { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? ppEndpoint);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
