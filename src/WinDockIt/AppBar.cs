using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WinDockIt;

/// <summary>
/// Registers the dock as a Windows "app bar", the same mechanism the taskbar uses to reserve
/// screen space. Windows then shrinks the work area so maximized and snapped windows stop at the
/// dock instead of sliding underneath it, which is how the Mac Dock behaves.
/// </summary>
internal sealed class AppBar : IDisposable
{
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_WINDOWPOSCHANGED = 9;
    const int ABN_POSCHANGED = 1;
    const int WM_WINDOWPOSCHANGED = 0x47;

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);

    readonly IntPtr _hwnd;
    readonly uint _callback;
    readonly HwndSource? _source;
    (uint Edge, Native.RECT Rc)? _last;
    bool _registered;

    /// <summary>Another app bar (usually the taskbar) moved or resized; re-run layout.</summary>
    public event Action? Changed;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _callback = RegisterWindowMessage("WinDockIt.AppBar");
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        var d = Data();
        _registered = SHAppBarMessage(ABM_NEW, ref d) != UIntPtr.Zero;
    }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callback };

    static uint Edge(DockPosition p) => p switch
    {
        DockPosition.Left => 0,
        DockPosition.Top => 1,
        DockPosition.Right => 2,
        _ => 3,
    };

    /// <summary>
    /// Reserves <paramref name="thickness"/> physical pixels along the given edge of the monitor
    /// (0 reserves nothing, for auto-hide). Returns the monitor rect minus other app bars on that
    /// edge, i.e. where the dock's outer edge should sit.
    /// </summary>
    public Native.RECT Reserve(Native.RECT monitor, DockPosition position, int thickness)
    {
        if (!_registered) return monitor;

        var d = Data();
        d.uEdge = Edge(position);
        d.rc = monitor;
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        var outer = d.rc;

        switch (position)
        {
            case DockPosition.Bottom: d.rc.Top = d.rc.Bottom - thickness; break;
            case DockPosition.Top: d.rc.Bottom = d.rc.Top + thickness; break;
            case DockPosition.Left: d.rc.Right = d.rc.Left + thickness; break;
            case DockPosition.Right: d.rc.Left = d.rc.Right - thickness; break;
        }

        // Only tell Windows when something changed: every SETPOS re-lays out the desktop.
        if (_last is not { } l || l.Edge != d.uEdge || !Same(l.Rc, d.rc))
        {
            _last = (d.uEdge, d.rc);
            SHAppBarMessage(ABM_SETPOS, ref d);
        }
        return outer;
    }

    static bool Same(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_registered) return IntPtr.Zero;
        if (msg == _callback && wParam.ToInt32() == ABN_POSCHANGED)
        {
            Changed?.Invoke();
        }
        else if (msg == WM_WINDOWPOSCHANGED)
        {
            var d = Data();
            SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref d);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered) return;
        _registered = false;
        var d = Data();
        SHAppBarMessage(ABM_REMOVE, ref d);
        _source?.RemoveHook(WndProc);
    }
}
