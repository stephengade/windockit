using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace WinDockIt;

/// <summary>
/// The frosted glass behind the dock.
///
/// Windows ignores window regions when it blurs a window, so a plain "blur behind" window always
/// has square corners. Instead this is a bare window hosting a Windows.UI.Composition visual
/// painted with a host-backdrop brush (the blurred desktop behind us, the same source acrylic
/// uses), clipped by an anti-aliased rounded-rectangle geometry.
///
/// The icons live in <see cref="DockWindow"/>, a separate window owned by this one so it always
/// sits on top — which is also what lets magnified icons grow past the glass.
/// </summary>
internal sealed class GlassBackdrop : IDisposable
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_NOACTIVATE = 0x08000000;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static IntPtr s_queueController; // must stay alive for the lifetime of the thread's compositor

    readonly HwndSource _window;
    readonly Compositor? _compositor;
    readonly DesktopWindowTarget? _target;
    readonly SpriteVisual?[] _sprites = new SpriteVisual?[2];
    readonly CompositionRoundedRectangleGeometry?[] _shapes = new CompositionRoundedRectangleGeometry?[2];
    string _last = "";
    float _opacity = 1;

    public IntPtr Handle => _window.Handle;

    /// <summary>False if composition isn't available; the dock then uses a more opaque tint.</summary>
    public bool IsBlurAvailable { get; }

    public GlassBackdrop()
    {
        _window = new HwndSource(new HwndSourceParameters("WinDockIt glass")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST,
            PositionX = -32000, PositionY = -32000, Width = 1, Height = 1,
        });

        try
        {
            // Windows 11 only draws host-backdrop brushes in windows that opt in (DWMWA_USE_HOSTBACKDROPBRUSH).
            // On Windows 10 the attribute doesn't exist and the call harmlessly fails.
            int on = 1;
            DwmSetWindowAttribute(_window.Handle, 17, ref on, sizeof(int));

            EnsureDispatcherQueue();
            _compositor = new Compositor();
            _target = CreateTarget(_compositor, _window.Handle);

            // Two pieces of glass: the dock, and the second island of the Split style.
            var root = _compositor.CreateContainerVisual();
            var brush = _compositor.CreateHostBackdropBrush();
            for (int i = 0; i < 2; i++)
            {
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Brush = brush;
                _shapes[i] = _compositor.CreateRoundedRectangleGeometry();
                sprite.Clip = _compositor.CreateGeometricClip(_shapes[i]);
                sprite.IsVisible = i == 0;
                root.Children.InsertAtTop(sprite);
                _sprites[i] = sprite;
            }
            _target.Root = root;
            IsBlurAvailable = true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            IsBlurAvailable = false;
        }
    }

    public void Show() => ShowWindow(Handle, SW_SHOWNOACTIVATE);
    public void Hide() => ShowWindow(Handle, SW_HIDE);

    /// <summary>
    /// Window bounds in physical pixels, then the rounded glass shape relative to the window.
    /// The shape may extend past the window; that part is simply cut off.
    /// </summary>
    public void SetShape(int x, int y, int w, int h, int rx, int ry, int rw, int rh, int radius) =>
        SetShapes(x, y, w, h, [(rx, ry, rw, rh, radius)]);

    /// <summary>Like <see cref="SetShape"/>, with one or two rounded shapes, each relative to the window.</summary>
    public void SetShapes(int x, int y, int w, int h, (int X, int Y, int W, int H, int Radius)[] shapes)
    {
        var key = $"{x},{y},{w},{h}|" + string.Join("|", shapes);
        if (key == _last) return;
        _last = key;

        SetWindowPos(Handle, IntPtr.Zero, x, y, Math.Max(w, 0), Math.Max(h, 0), SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        for (int i = 0; i < _sprites.Length; i++)
        {
            var sprite = _sprites[i];
            var shape = _shapes[i];
            if (sprite == null || shape == null) continue;
            sprite.IsVisible = i < shapes.Length;
            if (i >= shapes.Length) continue;
            var s = shapes[i];
            sprite.Offset = new Vector3(s.X, s.Y, 0);
            sprite.Size = new Vector2(s.W, s.H);
            shape.Size = new Vector2(s.W, s.H);
            shape.CornerRadius = new Vector2(s.Radius, s.Radius);
        }
    }

    /// <summary>Fades the glass, for the Minimal style that only shows it under the pointer.</summary>
    public void SetOpacity(double opacity)
    {
        float o = (float)Math.Clamp(opacity, 0, 1);
        if (Math.Abs(o - _opacity) < 0.001) return;
        _opacity = o;
        foreach (var s in _sprites) if (s != null) s.Opacity = o;
    }

    static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 0,  // DQTAT_COM_NONE (WPF already initialised COM)
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out s_queueController));
    }

    /// <summary>ICompositorDesktopInterop::CreateDesktopWindowTarget, called through the vtable.</summary>
    static unsafe DesktopWindowTarget CreateTarget(Compositor compositor, IntPtr hwnd)
    {
        var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807");
        IntPtr unknown = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr interop));
        try
        {
            IntPtr target;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)(*(*(void***)interop + 3));
            Marshal.ThrowExceptionForHR(create(interop, hwnd, 0, &target));
            try { return DesktopWindowTarget.FromAbi(target); }
            finally { Marshal.Release(target); }
        }
        finally { Marshal.Release(interop); }
    }

    public void Dispose()
    {
        _target?.Dispose();
        _compositor?.Dispose();
        _window.Dispose();
    }
}
