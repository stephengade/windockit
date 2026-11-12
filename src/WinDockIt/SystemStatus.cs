using System.Runtime.InteropServices;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace WinDockIt;

/// <summary>Reads the system state the status bar shows: network, Bluetooth, volume and battery.</summary>
internal static class SystemStatus
{
    // ───────── network ─────────

    public enum NetKind { None, Wifi, Ethernet, Cellular }

    /// <summary>The connection Windows uses for the internet, and its Wi-Fi signal (0–5 bars).</summary>
    public static (NetKind Kind, int Bars) Network()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null || p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) return (NetKind.None, 0);
            if (p.IsWlanConnectionProfile) return (NetKind.Wifi, p.GetSignalBars() ?? 5);
            if (p.IsWwanConnectionProfile) return (NetKind.Cellular, p.GetSignalBars() ?? 5);
            return (NetKind.Ethernet, 5);
        }
        catch { return (NetKind.None, 0); }
    }

    // ───────── Bluetooth ─────────

    static Radio? s_bluetooth;
    static bool s_radiosLoaded;

    /// <summary>Null when the PC has no Bluetooth radio, otherwise whether it's switched on.</summary>
    public static bool? Bluetooth => s_bluetooth == null ? null : s_bluetooth.State == RadioState.On;

    /// <summary>Finds the Bluetooth radio once; <paramref name="changed"/> fires whenever it's switched on or off.</summary>
    public static async void LoadBluetooth(Action changed)
    {
        if (s_radiosLoaded) return;
        s_radiosLoaded = true;
        try
        {
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind != RadioKind.Bluetooth) continue;
                s_bluetooth = r;
                r.StateChanged += (_, _) => changed();
                break;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        changed();
    }

    // ───────── volume ─────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume? s_volume;
    static DateTime s_volumeAt;

    /// <summary>The default playback device's volume control, looked up again every few seconds in case it changed.</summary>
    static IAudioEndpointVolume? Endpoint()
    {
        if (s_volume != null && (DateTime.Now - s_volumeAt).TotalSeconds < 4) return s_volume;
        s_volumeAt = DateTime.Now;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var dev) != 0) return s_volume = null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0) return s_volume = null;
            return s_volume = (IAudioEndpointVolume)o;
        }
        catch { return s_volume = null; }
    }

    /// <summary>Volume 0–1 and mute state, or null when there's no playback device.</summary>
    public static (double Level, bool Muted)? Volume()
    {
        var v = Endpoint();
        if (v == null || v.GetMasterVolumeLevelScalar(out float level) != 0 || v.GetMute(out bool muted) != 0)
        {
            s_volume = null;
            return null;
        }
        return (level, muted);
    }

    public static void SetVolume(double level)
    {
        if (Endpoint() is not { } v) return;
        var ctx = Guid.Empty;
        v.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref ctx);
        if (level > 0) v.SetMute(false, ref ctx);
    }

    public static void ToggleMute()
    {
        if (Endpoint() is not { } v || v.GetMute(out bool muted) != 0) return;
        var ctx = Guid.Empty;
        v.SetMute(!muted, ref ctx);
    }

    // ───────── battery ─────────

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Null on PCs without a battery.</summary>
    public static (int Percent, bool Charging, bool Saver)? Battery()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100)
            return null;
        return (s.BatteryLifePercent, s.ACLineStatus == 1, s.SystemStatusFlag == 1);
    }
}
