using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.Windows.Shapes.Path;

namespace WinDockIt;

/// <summary>
/// A thin frosted bar along the top of the screen, like the Mac menu bar: Start and the focused
/// app's name on the left; network, Bluetooth, volume, battery, quick settings and the clock on
/// the right. Clicking a status item opens the matching Windows panel. It reserves its strip of
/// the screen the same way the dock does, so maximized windows stop underneath it.
/// </summary>
public sealed class StatusBar : Window
{
    const double BarHeight = 28;
    const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    readonly GlassBackdrop _glass = new();
    readonly Func<IntPtr, DockItem?> _itemFor;
    readonly Action _hideBar;
    readonly DispatcherTimer _tick;
    AppBar? _appBar;
    IntPtr _hwnd;
    double _dpi = 1;
    bool _closed, _menuOpen;
    int _ticks;

    readonly Grid _root = new();
    readonly Border _tint = new() { BorderThickness = new Thickness(0, 0, 0, 1) };
    readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
    readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
    readonly List<Border> _buttons = new();

    readonly TextBlock _appText = new() { FontWeight = FontWeights.SemiBold };
    readonly TextBlock _netGlyph = Glyph(), _btGlyph = Glyph(), _volGlyph = Glyph();
    readonly TextBlock _batText = new() { FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _clockText = new();
    readonly Border _batShell = new() { Width = 23, Height = 12, CornerRadius = new CornerRadius(3.5), BorderThickness = new Thickness(1) };
    readonly Border _batFill = new() { Height = 8, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 0, 0, 0) };
    readonly Border _batNub = new() { Width = 1.6, Height = 4, CornerRadius = new CornerRadius(0, 1, 1, 0), Margin = new Thickness(0.6, 0, 0, 0) };
    readonly Path _bolt = new()
    {
        Data = Geometry.Parse("M 6,0 L 1,7 L 4.6,7 L 3.6,12 L 8.6,4.6 L 5,4.6 Z"),
        StrokeThickness = 0.8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Stretch = Stretch.Uniform, Height = 10,
    };
    readonly Border _netBtn, _btBtn, _volBtn, _batBtn;
    readonly List<System.Windows.Shapes.Shape> _iconStrokes = new();
    readonly List<System.Windows.Shapes.Shape> _iconFills = new();

    Brush _fg = Brushes.Black, _hover = Brushes.Transparent, _contrast = Brushes.White;
    IntPtr _lastForeground;
    readonly Dictionary<uint, (string Exe, string Name)> _processes = new();

    /// <summary>Windows' own panels (quick settings, Start, search, notifications): not apps, so the bar keeps naming the app underneath.</summary>
    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp", "TextInputHost",
    };

    public StatusBar(Func<IntPtr, DockItem?> itemFor, Action hideBar)
    {
        _itemFor = itemFor;
        _hideBar = hideBar;

        Title = "WinDockIt status bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Content = _root;

        _root.Children.Add(_tint);
        _root.Children.Add(_left);
        _root.Children.Add(_right);

        // Left: Start, then the app you're working in.
        _left.Children.Add(Button(StartIcon(), Native.OpenStart));
        _left.Children.Add(Button(_appText, ShowAppMenu));

        // Right: status items, then the clock.
        _netBtn = Button(_netGlyph, () => Open("ms-availablenetworks:"));
        _btBtn = Button(_btGlyph, () => Open("ms-settings:bluetooth"));
        _btGlyph.Text = "";
        _volBtn = Button(_volGlyph, Native.OpenQuickSettings);
        _volBtn.MouseWheel += (_, e) =>
        {
            if (SystemStatus.Volume() is { } v) SystemStatus.SetVolume(Math.Round((v.Level + Math.Sign(e.Delta) * 0.02) * 50) / 50);
            UpdateVolume();
            e.Handled = true;
        };
        var bat = new StackPanel { Orientation = Orientation.Horizontal };
        var cell = new Grid();
        _batShell.Child = new Grid { Children = { _batFill } };
        cell.Children.Add(_batShell);
        cell.Children.Add(_bolt);
        bat.Children.Add(_batText);
        bat.Children.Add(cell);
        bat.Children.Add(_batNub);
        _batBtn = Button(bat, () => Open("ms-settings:batterysaver"));
        foreach (var b in new[] { _netBtn, _btBtn, _volBtn, _batBtn }) _right.Children.Add(b);
        _right.Children.Add(Button(QuickSettingsIcon(), Native.OpenQuickSettings));
        _right.Children.Add(Button(_clockText, Native.OpenNotifications));

        _root.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowBarMenu(); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            _appBar = new AppBar(_hwnd);
            _appBar.Changed += () => Dispatcher.InvokeAsync(ApplyLayout);
            ApplyLayout();
        };
        DpiChanged += (_, _) => ApplyLayout();

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ => Dispatcher.InvokeAsync(UpdateNetwork);
        SystemStatus.LoadBluetooth(() => Dispatcher.InvokeAsync(UpdateBluetooth));
        Application.Current.Exit += (_, _) => _appBar?.Dispose();

        ApplyTheme();
        Refresh();
    }

    public void Start()
    {
        _glass.Show();
        new WindowInteropHelper(this).Owner = _glass.Handle;
        Show();
        ApplyLayout();
    }

    /// <summary>Removes the bar and gives its strip of the screen back.</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _tick.Stop();
        _appBar?.Dispose();
        Close();
        _glass.Dispose();
    }

    /// <summary>Explorer restarted and forgot our reserved space.</summary>
    public void Reset()
    {
        _appBar?.Reset();
        ApplyLayout();
    }

    /// <summary>Hidden while a game or video is fullscreen, like the dock.</summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_closed) return;
        if (fullscreen) { Hide(); _glass.Hide(); }
        else { _glass.Show(); Show(); }
    }

    // ───────────────────────── layout ─────────────────────────

    /// <summary>Full width along the top of the primary screen, above any other bar there.</summary>
    void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero || _closed) return;
        var mon = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;
        if (Native.GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0) _dpi = dpiX / 96.0;

        int h = (int)Math.Round(BarHeight * _dpi);
        var outer = _appBar?.Reserve(mi.rcMonitor, DockPosition.Top, h) ?? mi.rcMonitor;
        int w = outer.Right - outer.Left;

        Left = outer.Left / _dpi;
        Top = outer.Top / _dpi;
        Width = w / _dpi;
        Height = BarHeight;
        _glass.SetShape(outer.Left, outer.Top, w, h, 0, 0, w, h, 0);
    }

    // ───────────────────────── theme ─────────────────────────

    public void ApplyTheme()
    {
        bool light = Theme.IsLight, glass = Theme.TransparencyEnabled && _glass.IsBlurAvailable;
        // Same see-through wash as the dock, so the wallpaper shows through at the top and bottom.
        _tint.Background = light ? Theme.Brush(glass ? 0x59FFFFFFu : 0xF2F4F4F7u) : Theme.Brush(glass ? 0x47202024u : 0xF21E1E22u);
        _tint.BorderBrush = Theme.Brush(light ? 0x1A000000u : 0x1FFFFFFFu);
        _fg = Theme.Brush(light ? 0xFF1C1C1Eu : 0xFFF5F5F7u);
        _contrast = Theme.Brush(light ? 0xFFFFFFFFu : 0xFF1C1C1Eu);
        _hover = Theme.Brush(light ? 0x1F000000u : 0x29FFFFFFu);
        TextElement.SetForeground(_root, _fg);
        foreach (var s in _iconStrokes) s.Stroke = _fg;
        foreach (var s in _iconFills) s.Fill = _fg;
        _batShell.BorderBrush = Theme.Brush(light ? 0x731C1C1Eu : 0x80F5F5F7u);
        _batNub.Background = _batShell.BorderBrush;
        _bolt.Fill = _fg;
        _bolt.Stroke = _contrast;
        UpdateBattery();
    }

    // ───────────────────────── state ─────────────────────────

    void Tick()
    {
        UpdateApp();
        UpdateVolume();
        if (++_ticks % 4 == 0) UpdateClock();
        if (_ticks % 20 == 0) { UpdateBattery(); UpdateNetwork(); }
    }

    void Refresh()
    {
        UpdateApp();
        UpdateClock();
        UpdateNetwork();
        UpdateBluetooth();
        UpdateVolume();
        UpdateBattery();
        _tick.Start();
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        var c = CultureInfo.CurrentCulture;
        string text = $"{now.ToString("ddd d MMM", c)}   {now.ToString(c.DateTimeFormat.ShortTimePattern, c)}";
        if (_clockText.Text != text) _clockText.Text = text;
    }

    void UpdateNetwork()
    {
        var (kind, bars) = SystemStatus.Network();
        _netGlyph.Text = kind switch
        {
            SystemStatus.NetKind.Ethernet => "",
            SystemStatus.NetKind.Cellular => "",
            _ => bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" },
        };
        // No connection: a faded Wi-Fi symbol, the way the Mac shows it.
        _netGlyph.Opacity = kind == SystemStatus.NetKind.None ? 0.35 : 1;
    }

    void UpdateBluetooth()
    {
        var on = SystemStatus.Bluetooth;
        _btBtn.Visibility = on == null ? Visibility.Collapsed : Visibility.Visible;
        _btGlyph.Opacity = on == true ? 1 : 0.35;
    }

    void UpdateVolume()
    {
        var v = SystemStatus.Volume();
        _volBtn.Visibility = v == null ? Visibility.Collapsed : Visibility.Visible;
        if (v is not { } vol) return;
        string g = vol.Muted || vol.Level < 0.005 ? "" : vol.Level < 0.34 ? "" : vol.Level < 0.67 ? "" : "";
        if (_volGlyph.Text != g) _volGlyph.Text = g;
    }

    void UpdateBattery()
    {
        var b = SystemStatus.Battery();
        _batBtn.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b is not { } bat) return;
        _batText.Text = $"{bat.Percent}%";
        _batFill.Width = Math.Max(1.5, 19 * bat.Percent / 100.0);
        // Like the Mac: red when nearly empty, yellow in battery saver, a bolt while charging.
        _batFill.Background = bat.Charging ? Theme.Brush(0xFF34C759u)
            : bat.Saver ? Theme.Brush(0xFFFFCC00u)
            : bat.Percent <= 10 ? Theme.Brush(0xFFFF3B30u)
            : _fg;
        _bolt.Visibility = bat.Charging ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateApp()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == _lastForeground || fg == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return;   // our own menus: keep showing the app underneath
        string cls = Native.ClassName(fg);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") return;
        var proc = Process(pid);
        if (ShellProcesses.Contains(proc.Exe)) return;
        _lastForeground = fg;

        if (cls is "Progman" or "WorkerW") { _appText.Text = "Desktop"; return; }
        var root = GetAncestor(fg, 3 /* GA_ROOTOWNER */);
        var item = _itemFor(fg) ?? (root != IntPtr.Zero ? _itemFor(root) : null);
        _appText.Text = item?.Name is { Length: > 0 } n ? n : proc.Name;
    }

    /// <summary>The exe's file name, and the program's own description ("Notepad", "Google Chrome") or else that file name.</summary>
    (string Exe, string Name) Process(uint pid)
    {
        if (_processes.TryGetValue(pid, out var cached)) return cached;
        string name = "", exe = "";
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                {
                    string path = sb.ToString();
                    exe = System.IO.Path.GetFileNameWithoutExtension(path);
                    try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
                    if (name.Length == 0) name = exe;
                }
            }
            finally { CloseHandle(h); }
        }
        if (name.Length == 0) name = "Desktop";
        return _processes[pid] = (exe, name);
    }

    // ───────────────────────── menus ─────────────────────────

    /// <summary>Like the Mac's app menu: the app's windows, then hide and quit.</summary>
    void ShowAppMenu()
    {
        var fg = _lastForeground;
        if (fg == IntPtr.Zero || !Native.IsWindowVisible(fg)) return;
        var menu = new ContextMenu();
        var item = _itemFor(fg) ?? _itemFor(GetAncestor(fg, 3));
        var windows = item?.Windows.ToList() ?? [fg];

        foreach (var h in windows.Take(12))
        {
            string title = Native.WindowTitle(h);
            if (title.Length == 0) continue;
            if (title.Length > 60) title = title[..57] + "…";
            var mi = MenuItem(title.Replace("_", "__"), () => Native.Focus(h));
            mi.IsChecked = h == fg;
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem($"Hide {_appText.Text}", () =>
        {
            foreach (var h in windows) Native.ShowWindow(h, Native.SW_MINIMIZE);
        }));
        menu.Items.Add(MenuItem("Close window", () => Native.PostMessage(fg, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)));
        if (windows.Count > 1 || item != null)
            menu.Items.Add(MenuItem($"Quit {_appText.Text}", () =>
            {
                foreach (var h in windows) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }));

        // Opens just under the app name, like a menu bar menu.
        menu.PlacementTarget = (UIElement)_left.Children[1];
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.VerticalOffset = 3;
        OpenMenu(menu, (Border)_left.Children[1], restoreFocus: false);
    }

    void ShowBarMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _root, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(MenuItem("Date and time settings", () => Open("ms-settings:dateandtime")));
        menu.Items.Add(MenuItem("Sound settings", () => Open("ms-settings:sound")));
        menu.Items.Add(MenuItem("Network settings", () => Open("ms-settings:network")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Hide status bar", _hideBar));
        OpenMenu(menu, null, restoreFocus: true);
    }

    /// <summary>
    /// Same trick as the dock's menus: become foreground just before opening, so a click elsewhere
    /// closes the menu, and give focus back afterwards if nothing else took it.
    /// </summary>
    void OpenMenu(ContextMenu menu, Border? owner, bool restoreFocus)
    {
        IntPtr previous = _lastForeground;
        Native.SetForegroundWindow(_hwnd);
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            if (owner != null) owner.Background = _hover;
            menu.Focus();
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (owner != null) owner.Background = Theme.Brush(0x01000000u);
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            if (pid == Environment.ProcessId && previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
        };
        menu.IsOpen = true;
    }

    static MenuItem MenuItem(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ───────────────────────── pieces ─────────────────────────

    static TextBlock Glyph() => new()
    {
        FontFamily = new FontFamily(IconFont), FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A rounded hover highlight around one item, like the Mac menu bar.</summary>
    Border Button(UIElement content, Action click)
    {
        if (content is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border
        {
            Child = content, Height = 22, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(0x01000000u),   // fully clear pixels don't receive clicks
        };
        b.MouseEnter += (_, _) => b.Background = _hover;
        b.MouseLeave += (_, _) => { if (!_menuOpen) b.Background = Theme.Brush(0x01000000u); };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { click(); } catch (Exception ex) { App.Log(ex); }
        };
        _buttons.Add(b);
        return b;
    }

    /// <summary>The Windows logo, drawn as four squares so it follows the bar's text colour.</summary>
    UIElement StartIcon()
    {
        var g = new Grid { Width = 13, Height = 13 };
        for (int i = 0; i < 4; i++)
        {
            var r = new System.Windows.Shapes.Rectangle { Width = 6, Height = 6, RadiusX = 0.8, RadiusY = 0.8 };
            r.HorizontalAlignment = i % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            r.VerticalAlignment = i < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _iconFills.Add(r);
            g.Children.Add(r);
        }
        return g;
    }

    /// <summary>Two little switches, like the Mac's Control Center icon.</summary>
    UIElement QuickSettingsIcon()
    {
        var c = new Canvas { Width = 16, Height = 14 };
        void Switch(double y, bool knobRight)
        {
            var pill = new System.Windows.Shapes.Rectangle { Width = 15, Height = 6, RadiusX = 3, RadiusY = 3, StrokeThickness = 1.2 };
            Canvas.SetLeft(pill, 0.5);
            Canvas.SetTop(pill, y);
            _iconStrokes.Add(pill);
            var knob = new Ellipse { Width = 4, Height = 4 };
            Canvas.SetLeft(knob, knobRight ? 10.5 : 1.5);
            Canvas.SetTop(knob, y + 1);
            _iconFills.Add(knob);
            c.Children.Add(pill);
            c.Children.Add(knob);
        }
        Switch(0.5, true);
        Switch(7.5, false);
        return c;
    }

    static void Open(string uri)
    {
        try { System.Diagnostics.Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
}
