using System.Runtime.InteropServices;
using System.Text;

namespace WinDockIt;

/// <summary>
/// A window the user would see in Alt+Tab, with the identity Windows uses to group it on the
/// taskbar: an explicit app ID if the window or its Store package has one, otherwise its exe.
/// </summary>
internal sealed record AppWindow(IntPtr Handle, string? Aumid, string? ExePath, string ProcessName)
{
    /// <summary>Groups windows into one dock icon, the same way the taskbar groups buttons.</summary>
    public string GroupKey => (Aumid ?? ExePath ?? ProcessName).ToLowerInvariant();
}

internal static class WindowList
{
    static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr h, ref int length, StringBuilder sb);
    delegate bool EnumChildProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);

    /// <summary>Windows in z-order (top first).</summary>
    public static List<AppWindow> Get()
    {
        var list = new List<AppWindow>();
        var procs = new Dictionary<uint, (string? Exe, string? Package)>();
        uint self = (uint)Environment.ProcessId;

        Native.EnumWindows((h, _) =>
        {
            if (!IsTaskWindow(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;

            // Store apps draw inside a frame window owned by ApplicationFrameHost; the app itself
            // is the process behind the frame's CoreWindow child.
            var (exe, package) = Process(pid, procs);
            if (exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                && HostedProcess(h, pid) is uint inner)
                (exe, package) = Process(inner, procs);

            string? aumid = Shell.WindowAppId(h) ?? package;
            string name = exe != null ? Path.GetFileNameWithoutExtension(exe) : "";
            list.Add(new AppWindow(h, aumid, exe, name));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    static (string? Exe, string? Package) Process(uint pid, Dictionary<uint, (string?, string?)> cache)
    {
        if (cache.TryGetValue(pid, out var v)) return v;
        string? exe = null, package = null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref n)) exe = sb.ToString();
                int len = 512;
                var id = new StringBuilder(len);
                if (GetApplicationUserModelId(h, ref len, id) == 0) package = id.ToString();
            }
            finally { CloseHandle(h); }
        }
        return cache[pid] = (exe, package);
    }

    static uint? HostedProcess(IntPtr frame, uint framePid)
    {
        uint? found = null;
        EnumChildWindows(frame, (c, _) =>
        {
            Native.GetWindowThreadProcessId(c, out uint pid);
            if (pid != framePid && Native.ClassName(c) == "Windows.UI.Core.CoreWindow") { found = pid; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool IsTaskWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
        return !ExcludedClasses.Contains(Native.ClassName(h));
    }
}
