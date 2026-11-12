using System.Diagnostics;

namespace WinDockIt;

/// <summary>Enumerates the top-level windows the user would see in Alt+Tab.</summary>
internal static class WindowList
{
    static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Windows in z-order (top first) with the name of the process that owns each.</summary>
    public static List<(IntPtr Handle, string Process)> Get()
    {
        var list = new List<(IntPtr, string)>();
        var names = new Dictionary<uint, string>();
        uint self = (uint)Environment.ProcessId;

        Native.EnumWindows((h, _) =>
        {
            if (!IsTaskWindow(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            if (!names.TryGetValue(pid, out var name))
            {
                try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
                catch { name = ""; }
                names[pid] = name;
            }
            list.Add((h, name));
            return true;
        }, IntPtr.Zero);

        return list;
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
