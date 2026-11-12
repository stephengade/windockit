using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinDockIt;

/// <summary>
/// Optionally hides the Windows taskbar so the dock is the only app switcher.
///
/// A hidden taskbar still reserves its strip of the screen, so it's also switched to auto-hide
/// (which reserves nothing). That setting persists, so its original value is saved in the config
/// before changing it and put back on the way out. If WinDockIt dies without cleaning up, a small
/// watchdog copy of the app (started with --watchdog) restores the taskbar.
/// </summary>
internal static class TaskbarHider
{
    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
    const int SW_HIDE = 0, SW_SHOWNA = 8;

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    static bool s_watchdogStarted;

    /// <summary>The main taskbar and the ones on other monitors.</summary>
    static List<IntPtr> Bars()
    {
        var list = new List<IntPtr>();
        var main = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero;)
            list.Add(h);
        return list;
    }

    static APPBARDATA Data(IntPtr tray) => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray };

    static bool? IsAutoHide()
    {
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        var d = Data(tray);
        return ((int)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
    }

    static void SetAutoHide(bool on)
    {
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        var d = Data(tray);
        d.lParam = new IntPtr(on ? ABS_AUTOHIDE : ABS_ALWAYSONTOP);
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }

    public static void Hide(DockConfig cfg)
    {
        if (cfg.TaskbarWasAutoHide == null && IsAutoHide() is bool was)
        {
            cfg.TaskbarWasAutoHide = was;   // saved first, so a crash can't lose it
            cfg.Save();
        }
        SetAutoHide(true);
        foreach (var bar in Bars()) Native.ShowWindow(bar, SW_HIDE);
        StartWatchdog();
    }

    /// <summary>Explorer sometimes shows the taskbar again by itself; hide it if so.</summary>
    public static void KeepHidden()
    {
        foreach (var bar in Bars())
            if (Native.IsWindowVisible(bar)) Native.ShowWindow(bar, SW_HIDE);
    }

    /// <summary>Puts the taskbar back exactly as it was. Safe to call when it was never hidden.</summary>
    public static void Restore(DockConfig cfg)
    {
        if (cfg.TaskbarWasAutoHide is not bool was) return;
        SetAutoHide(was);
        foreach (var bar in Bars()) Native.ShowWindow(bar, SW_SHOWNA);
        cfg.TaskbarWasAutoHide = null;
        cfg.Save();
    }

    static void StartWatchdog()
    {
        if (s_watchdogStarted || Environment.ProcessPath is not { } exe) return;
        s_watchdogStarted = true;
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Watchdog mode: wait for the dock to exit, then restore the taskbar if it didn't.</summary>
    public static void RunWatchdog(int pid)
    {
        try
        {
            using var dock = Process.GetProcessById(pid);
            dock.WaitForExit();
        }
        catch (ArgumentException) { } // already gone
        Thread.Sleep(500);
        Restore(DockConfig.Load());
    }
}
