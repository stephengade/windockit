using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace WinDockIt;

/// <summary>
/// The dock itself. Everything is laid out by hand on a Canvas each frame in "dock space":
///   m = distance along the dock, c = distance from the screen edge the dock is attached to.
/// <see cref="ToWin"/> maps that onto the window for whichever edge is selected, so the
/// same code drives bottom, top, left and right docks.
/// </summary>
public sealed class DockWindow : Window
{
    const double EdgeGap = 10;   // gap between screen edge and glass
    const double PadEdge = 12;   // glass padding on the edge side (room for the running dot)
    const double PadOuter = 9;   // glass padding on the far side
    const double PadMain = 7;    // glass padding at each end
    const double SepSlot = 17;   // width of the divider slot
    const double ReserveGap = 6; // space kept between the glass and maximized windows
    const double MinIcon = 16;   // smallest icons get when shrinking to fit
    const double OverflowIcon = 32; // below this, extra pinned apps move into the "+N" tile
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "WinDockIt";

    readonly DockConfig _cfg;
    readonly List<DockItem> _items = new();
    readonly DockItem _sep = new() { IsSeparator = true };
    readonly DockItem _trash = new() { IsTrash = true, Name = "Recycle Bin", Location = Shell.RecycleBinPath };
    readonly DockItem _sepRun = new() { IsSeparator = true };   // between pinned and running-only apps
    readonly DockItem _start = new() { IsStart = true, Name = "Launchpad" };
    readonly DockItem _more = new() { IsMore = true, Name = "More apps" };
    /// <summary>Pinned apps that don't fit on the dock; they live in the "+N" tile and Launchpad.</summary>
    readonly List<DockItem> _overflow = new();
    LaunchpadWindow? _launchpad;

    readonly Canvas _root = new();
    readonly Canvas _shadowHost = new() { IsHitTestVisible = false };
    readonly Border _shadow = new() { Background = Brushes.Black };
    readonly Border _tint = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    readonly Border _shine = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    readonly Rectangle _hit = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    readonly Canvas _itemLayer = new();
    readonly Border _label = new()
    {
        CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5), BorderThickness = new Thickness(1),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    readonly TextBlock _labelText = new() { FontSize = 12.5 };
    readonly GlassBackdrop _plate = new();
    AppBar? _appBar;
    StatusBar? _status;
    readonly DispatcherTimer _fastTimer, _slowTimer;
    Brush _dotBrush = Brushes.Black, _sepBrush = Brushes.Gray;
    static readonly Brush AttentionBrush = Theme.Brush(0xFFFF9F0Au);
    uint _shellHookMsg, _taskbarCreatedMsg;
    double _length;               // length of the screen edge the dock sits on, in DIPs
    readonly DispatcherTimer _refreshSoon;

    IntPtr _hwnd;
    double _dpi = 1;
    bool _animating, _hidden, _menuOpen, _fullscreen, _dragging, _removeArmed;
    double? _mouseM;
    DockItem? _hovered, _pressItem;
    Point _pressPoint;
    double _shift, _hideT, _plateM0, _plateM1, _radius;
    Rect _plateRect;
    DateTime _lastOver = DateTime.Now;
    long _trashCount = -1;

    double _base;

    /// <summary>Icon size actually used: the configured size, or smaller if that's what it takes to fit the screen.</summary>
    double Base => _base;
    double Mag => _cfg.Magnification;
    double PlateDepth => PadEdge + Base + PadOuter;
    bool Horizontal => _cfg.Position is DockPosition.Bottom or DockPosition.Top;

    public DockWindow()
    {
        _cfg = DockConfig.Load();
        Theme.Mode = _cfg.Theme;
        Theme.GlassOn = _cfg.Glass;
        _base = _cfg.IconSize;

        Title = AppName;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = _root;

        _shadow.Effect = new DropShadowEffect
        {
            BlurRadius = 34, ShadowDepth = 9, Direction = 270, Opacity = 0.30, RenderingBias = RenderingBias.Quality,
        };
        _shadowHost.Children.Add(_shadow);
        _label.Child = _labelText;
        foreach (var el in new UIElement[] { _shadowHost, _tint, _shine, _hit, _itemLayer, _label })
            _root.Children.Add(el);

        foreach (var it in new[] { _sep, _sepRun, _trash, _start, _more }) Prepare(it);
        var pins = _cfg.Items.ToList();
        foreach (var it in pins) Prepare(it);
        Rebuild(pins, new());

        _root.MouseMove += OnMouseMove;
        _root.MouseLeave += (_, _) => { if (!_dragging && _pressItem == null) EndHover(); };
        _root.MouseLeftButtonDown += OnLeftDown;
        _root.MouseLeftButtonUp += OnLeftUp;
        _root.MouseUp += OnMiddleUp;
        _root.MouseRightButtonUp += OnRightUp;
        DragOver += OnDragOver;
        DragLeave += (_, _) => EndHover();
        Drop += OnDrop;

        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.InvokeAsync(ApplyTheme);
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(ApplyLayout);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.InvokeAsync(ApplyLayout);
        };

        _fastTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FastTick(), Dispatcher);
        _slowTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => SlowTick(), Dispatcher);
        _refreshSoon = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _refreshSoon!.Stop();
            RefreshRunning();
        }, Dispatcher);
        _refreshSoon.Stop();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            _appBar = new AppBar(_hwnd);
            _appBar.Changed += () => Dispatcher.InvokeAsync(ApplyLayout);

            // Shell hook: windows opening, closing, being activated and flashing for attention.
            _shellHookMsg = Native.RegisterWindowMessage("SHELLHOOK");
            _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
            if (Native.RegisterShellHookWindow(_hwnd)) HwndSource.FromHwnd(_hwnd)?.AddHook(ShellHook);
            ApplyLayout();
        };
        Loaded += (_, _) =>
        {
            SlowTick();
            Kick();
        };
        DpiChanged += (_, _) => ApplyLayout();
        SizeChanged += (_, _) => Kick();

        ApplyTheme();
        ApplyLayout();
    }

    /// <summary>The icon window is owned by the glass window, which keeps it stacked directly above the glass.</summary>
    public void Start()
    {
        // Give the reserved screen space back however we exit.
        Application.Current.Exit += (_, _) =>
        {
            _appBar?.Dispose();
            TaskbarHider.Restore(_cfg);
            if (_hwnd != IntPtr.Zero) Native.DeregisterShellHookWindow(_hwnd);
        };
        Application.Current.SessionEnding += (_, _) => TaskbarHider.Restore(_cfg);
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            _appBar?.Dispose();
            TaskbarHider.Restore(_cfg);
        };
        // Also undoes a hide left over from a crash, if the option has since been turned off.
        if (_cfg.HideTaskbar) TaskbarHider.Hide(_cfg); else TaskbarHider.Restore(_cfg);
        _plate.Show();
        new WindowInteropHelper(this).Owner = _plate.Handle;
        Show();
        Kick();
        if (_cfg.ShowStatusBar) SetStatusBar(true);
        AppCatalog.Refresh();   // so Launchpad opens with every app ready
    }

    /// <summary>Shows or removes the bar along the top of the screen.</summary>
    internal void SetStatusBar(bool on)
    {
        if (_cfg.ShowStatusBar != on) { _cfg.ShowStatusBar = on; _cfg.Save(); }
        if (on && _status == null)
        {
            _status = new StatusBar(h => _items.FirstOrDefault(i => i.IsApp && i.Windows.Contains(h)), () => SetStatusBar(false), () => OpenSettings("behaviour"), () => ToggleLaunchpad(), _cfg.StatusBarHide);
            _status.Start();
        }
        else if (!on && _status != null)
        {
            _status.Shutdown();
            _status = null;
        }
        ApplyLayout();
    }

    // ───────────────────────── items ─────────────────────────

    /// <summary>Creates an item's visuals and works out which windows belong to it.</summary>
    void Prepare(DockItem it)
    {
        it.Size = it.GoalSize = Base;
        if (it.IsSeparator)
        {
            it.SepLine = new Border { IsHitTestVisible = false, Background = _sepBrush, Width = 1, Height = 1, RenderTransform = new ScaleTransform() };
            return;
        }

        it.Icon ??= it.IsStart ? Shell.AppIcon() : it.IsMore ? Shell.MoreIcon(0, Theme.IsLight) : Shell.GetIcon(it.Location) ?? Shell.LetterIcon(it.Name);
        // Icons keep a fixed layout size and are scaled with a transform. Changing Width every frame made
        // WPF occasionally leave newly added elements unmeasured (drawn at 256px, or not at all), and
        // transforms skip the layout pass entirely.
        it.Img = new Image
        {
            Source = it.Icon, Stretch = Stretch.Uniform, Width = IconCanvas, Height = IconCanvas,
            RenderTransform = new ScaleTransform(),
        };
        RenderOptions.SetBitmapScalingMode(it.Img, BitmapScalingMode.HighQuality);
        // Shadow values are in the icon's unscaled space (IconCanvas px); at 46px they're ~8px blur, 2px drop.
        it.Img.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 9, Direction = 270, Opacity = 0.26 };
        it.Dot = new Ellipse { Width = 4, Height = 4, Fill = _dotBrush, IsHitTestVisible = false };
        if (it.IsPinned)
        {
            (it.AppId, it.ExePath) = Shell.PinIdentity(it.Location);
            it.AppId = it.AppUserModelId ?? it.AppId;
        }
    }

    List<DockItem> Pins => _items.Where(i => i.IsPinned).Concat(_overflow).ToList();
    List<DockItem> Transients => _items.Where(i => i.IsTransient).ToList();
    int PinStart => _items.Count > 0 && _items[0].IsStart ? 1 : 0;

    /// <summary>
    /// Lays the dock out in its fixed order, adding and removing visuals for items that come and go:
    /// Start · pinned apps · | · running apps that aren't pinned · | · Recycle Bin.
    /// </summary>
    void Rebuild(List<DockItem> pins, List<DockItem> transients)
    {
        var next = new List<DockItem>();
        if (_cfg.ShowStart) next.Add(_start);
        int fits = PinsThatFit(pins.Count, transients.Count);
        next.AddRange(pins.Take(fits));
        _overflow.Clear();
        _overflow.AddRange(pins.Skip(fits));
        if (_overflow.Count > 0)
        {
            next.Add(_more);
            _more.Icon = Shell.MoreIcon(_overflow.Count, Theme.IsLight);
            if (_more.Img != null) _more.Img.Source = _more.Icon;
        }
        if (transients.Count > 0) { next.Add(_sepRun); next.AddRange(transients); }
        next.Add(_sep);
        next.Add(_trash);

        foreach (var it in _items.Except(next)) Detach(it);
        foreach (var it in next.Except(_items)) Attach(it);
        _items.Clear();
        _items.AddRange(next);
        if (_hovered != null && !_items.Contains(_hovered)) _hovered = null;
        Refit();
    }

    /// <summary>
    /// Like the Mac, the dock shrinks its icons to fit everything, but only down to a readable size.
    /// Past that, the last pinned apps move into a "+N" tile (they're still in Launchpad).
    /// </summary>
    int PinsThatFit(int pins, int transients)
    {
        if (_length <= 0 || pins == 0) return pins;
        double min = Math.Min(OverflowIcon, _cfg.IconSize);
        double avail = _length - 2 * (EdgeGap + PadMain);
        double seps = (transients > 0 ? 2 : 1) * SepSlot;
        double growth = 2.4 * Math.Max(Mag - 1, 0);
        int others = (_cfg.ShowStart ? 1 : 0) + transients + 1;   // Launchpad, running apps, Recycle Bin
        // Same sum as Fit(): n icons at size `min`, their spacing, and the magnified row's growth.
        int maxIcons = (int)Math.Floor((avail - seps - growth * min) / (min + Spacing));
        if (pins + others <= maxIcons) return pins;
        return Math.Clamp(maxIcons - others - 1, 1, pins);   // one slot goes to the "+N" tile
    }

    void Attach(DockItem it)
    {
        foreach (var el in new UIElement?[] { it.SepLine, it.Img, it.Dot })
            if (el != null && !_itemLayer.Children.Contains(el)) _itemLayer.Children.Add(el);
    }

    void Detach(DockItem it)
    {
        foreach (var el in new UIElement?[] { it.SepLine, it.Img, it.Dot })
            if (el != null) _itemLayer.Children.Remove(el);
    }

    bool AddPinned(string path, int index)
    {
        string key = Shell.IdentityKey(path);
        var pins = Pins;
        if (pins.Any(i => Shell.IdentityKey(i.Location) == key)) return false;
        var it = new DockItem { Name = Shell.DisplayName(path), Location = path };
        Prepare(it);
        pins.Insert(Math.Clamp(index, 0, pins.Count), it);
        Rebuild(pins, Transients);
        return true;
    }

    /// <summary>Takes an item off the dock. If it's running it stays, with the other running apps, until it closes.</summary>
    void Unpin(DockItem it)
    {
        var pins = Pins;
        pins.Remove(it);
        Rebuild(pins, Transients);
        SaveItems();
        RefreshRunning();
    }

    /// <summary>Pins a running app, like "Options › Keep in Dock" on the Mac.</summary>
    void KeepInDock(DockItem t)
    {
        var it = new DockItem
        {
            Name = t.Name, Location = t.Location, Arguments = t.Arguments, Icon = t.Icon,
            // Store apps' location already is their app ID; others need it kept so their windows still match.
            AppUserModelId = t.Location.StartsWith(Shell.AppsFolder, StringComparison.OrdinalIgnoreCase) ? null : t.AppId,
        };
        Prepare(it);
        var pins = Pins;
        pins.Add(it);
        var transients = Transients;
        transients.Remove(t);
        Rebuild(pins, transients);
        SaveItems();
        RefreshRunning();
    }

    static void CloseAll(DockItem it)
    {
        foreach (var h in it.Windows) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    void SaveItems()
    {
        _cfg.Items = Pins;
        _cfg.Save();
        PinsChanged?.Invoke();
    }

    // ───────────────────────── Launchpad ─────────────────────────

    /// <summary>Opens Launchpad, or closes it if it's already open.</summary>
    internal void ToggleLaunchpad()
    {
        if (_launchpad != null) { _launchpad.CloseSheet(); return; }
        AppCatalog.Refresh();
        RefreshRunning();
        _launchpad = new LaunchpadWindow(this);
        _launchpad.Closed += (_, _) => _launchpad = null;
        _launchpad.Open();
    }

    /// <summary>Where Launchpad should sit: the dock's shown rectangle and its screen's work area, in DIPs.</summary>
    internal (Rect Dock, Rect Work, DockPosition Position, double Dpi) LaunchpadAnchor()
    {
        var r = ToWin(_plateM0, _plateM1, 0, EdgeGap + PlateDepth);
        var dock = new Rect(Left + r.X, Top + r.Y, r.Width, r.Height);
        var work = SystemParameters.WorkArea;
        if (TryMonitor(Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST), out var mi))
            work = new Rect(mi.rcWork.Left / _dpi, mi.rcWork.Top / _dpi, (mi.rcWork.Right - mi.rcWork.Left) / _dpi, (mi.rcWork.Bottom - mi.rcWork.Top) / _dpi);
        return (dock, work, _cfg.Position, _dpi);
    }

    internal List<DockItem> OverflowItems => _overflow.ToList();

    /// <summary>Apps with windows open, pinned or not, in dock order.</summary>
    internal List<DockItem> OpenApps => _items.Where(i => i.IsApp).Concat(_overflow).Where(i => i.Windows.Count > 0).ToList();

    /// <summary>The dock item (pinned, overflowed or running) for a location, if there is one.</summary>
    internal DockItem? ItemFor(string location) =>
        _items.Where(i => i.IsApp).Concat(_overflow).FirstOrDefault(i => string.Equals(i.Location, location, StringComparison.OrdinalIgnoreCase));

    void CountLaunch(string location)
    {
        if (string.IsNullOrEmpty(location)) return;
        _cfg.LaunchCounts[location] = _cfg.LaunchCounts.GetValueOrDefault(location) + 1;
        _cfg.Save();
    }

    /// <summary>The most-opened apps that still exist, most-opened first.</summary>
    internal List<(string Name, string Location, ImageSource? Icon)> FrequentApps(int count, Func<string, CatalogApp?> catalog)
    {
        var result = new List<(string, string, ImageSource?)>();
        foreach (var (location, _) in _cfg.LaunchCounts.OrderByDescending(kv => kv.Value))
        {
            if (result.Count >= count) break;
            if (ItemFor(location) is { } it) result.Add((it.Name, it.Location, it.Icon));
            else if (catalog(location) is { } app) result.Add((app.Name, app.Location, app.Icon));
        }
        return result;
    }

    /// <summary>Opens (or switches to) an app picked in Launchpad.</summary>
    internal void LaunchFromLaunchpad(string location, DockItem? item, bool newInstance)
    {
        item ??= ItemFor(location);
        if (item != null) { Activate(item, newInstance); return; }
        CountLaunch(location);
        Start(location, null);
        _refreshSoon.Start();
    }

    internal void PinFromLaunchpad(string location, string name)
    {
        if (!AddPinned(location, Pins.Count)) return;
        SaveItems();
        RefreshRunning();
    }

    // ───────────────────────── settings window ─────────────────────────

    SettingsWindow? _settings;

    /// <summary>The pinned items were added to, removed or reordered.</summary>
    internal event Action? PinsChanged;
    internal DockConfig Config => _cfg;
    internal List<DockItem> PinnedItems => Pins;

    internal void OpenSettings(string page = "appearance")
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(this);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.ShowPage(page);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        // Raise it above other windows even if Windows won't hand us the focus.
        var h = new WindowInteropHelper(_settings).Handle;
        SetWindowPos(h, new IntPtr(-1) /* TOPMOST */, 0, 0, 0, 0, 0x3 | 0x40 /* NOSIZE | NOMOVE | SHOWWINDOW */);
        SetWindowPos(h, new IntPtr(-2) /* NOTOPMOST */, 0, 0, 0, 0, 0x3 | 0x40);
        _settings.Activate();
        if (Native.GetForegroundWindow() != h) Native.Focus(h);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    internal void SetIconSize(double size) { _cfg.IconSize = Math.Clamp(size, 24, 128); _cfg.Save(); ApplyLayout(); }
    internal void SetIconSpacing(double gap) { _cfg.IconSpacing = Math.Clamp(gap, 2, 32); _cfg.Save(); ApplyLayout(); }
    internal void SetStatusBarHide(HideMode mode) { _cfg.StatusBarHide = mode; _cfg.Save(); _status?.SetHideMode(mode); }
    internal void SetMagnification(double mag) { _cfg.Magnification = mag; _cfg.Save(); ApplyLayout(); }
    internal void SetHide(HideMode mode) { _cfg.Hide = mode; _cfg.Save(); _covered = false; ApplyLayout(); }
    internal void SetShowStart(bool on) { _cfg.ShowStart = on; _cfg.Save(); Rebuild(Pins, Transients); }
    internal void SetShowRunning(bool on) { _cfg.ShowRunning = on; _cfg.Save(); RefreshRunning(); }
    internal void SetTheme(ThemeMode mode) { _cfg.Theme = Theme.Mode = mode; _cfg.Save(); ApplyTheme(); }
    internal void SetGlass(bool on) { _cfg.Glass = Theme.GlassOn = on; _cfg.Save(); ApplyTheme(); }
    internal void RemovePin(DockItem it) => Unpin(it);

    /// <summary>Moves a pinned item one place along the dock.</summary>
    internal void MovePin(DockItem it, int delta)
    {
        var pins = Pins;
        int i = pins.IndexOf(it), j = i + delta;
        if (i < 0 || j < 0 || j >= pins.Count) return;
        (pins[i], pins[j]) = (pins[j], pins[i]);
        Rebuild(pins, Transients);
        SaveItems();
    }

    // ───────────────────────── layout ─────────────────────────

    /// <summary>
    /// Positions the dock on the primary screen and reserves its strip of the screen, so other
    /// windows maximize up to the dock rather than underneath it (nothing is reserved when auto-hiding).
    /// </summary>
    void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero) return;

        var mon = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        if (!TryMonitor(mon, out var mi)) return;
        if (Native.GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0) _dpi = dpiX / 96.0;
        double s = _dpi;

        // Reserve first (switching edges frees the old strip), then size the icons to the space
        // left along the dock, then reserve again in case the icon size, and so the depth, changed.
        _appBar?.Reserve(mi.rcMonitor, _cfg.Position, ReserveThickness());
        if (!TryMonitor(mon, out mi)) return;
        var work = mi.rcWork;
        Fit((Horizontal ? work.Right - work.Left : work.Bottom - work.Top) / s);
        var outer = _appBar?.Reserve(mi.rcMonitor, _cfg.Position, ReserveThickness()) ?? work;
        double oldLength = _length;
        _length = (Horizontal ? work.Right - work.Left : work.Bottom - work.Top) / s;
        if (Math.Abs(oldLength - _length) > 0.5)
        {
            var pins = Pins;
            if (PinsThatFit(pins.Count, Transients.Count) != _items.Count(i => i.IsPinned)) Rebuild(pins, Transients);
        }

        double cross = Math.Ceiling(EdgeGap + PadEdge + Base * Math.Max(Mag, 1) + Base * 0.5 + (Horizontal ? 46 : 240));
        double wl = work.Left / s, wt = work.Top / s, ww = (work.Right - work.Left) / s, wh = (work.Bottom - work.Top) / s;

        switch (_cfg.Position)
        {
            case DockPosition.Bottom: Left = wl; Top = outer.Bottom / s - cross; Width = ww; Height = cross; break;
            case DockPosition.Top: Left = wl; Top = outer.Top / s; Width = ww; Height = cross; break;
            case DockPosition.Left: Left = outer.Left / s; Top = wt; Width = cross; Height = wh; break;
            case DockPosition.Right: Left = outer.Right / s - cross; Top = wt; Width = cross; Height = wh; break;
        }

        foreach (var it in _items) it.Size = it.GoalSize = Base;
        _shift = 0;
        Kick();
    }

    /// <summary>Re-fits after items come or go; only redoes the whole layout if the icon size changed.</summary>
    void Refit()
    {
        if (_length <= 0) { Kick(); return; }
        double old = _base;
        Fit(_length);
        if (Math.Abs(old - _base) > 0.01) ApplyLayout(); else Kick();
    }

    int ReserveThickness() => _cfg.Hide != HideMode.KeepSpace ? 0 : (int)Math.Ceiling((EdgeGap + PlateDepth + ReserveGap) * _dpi);

    static bool TryMonitor(IntPtr mon, out Native.MONITORINFO mi)
    {
        mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfo(mon, ref mi);
    }

    /// <summary>
    /// Like the Mac Dock: never scroll, just shrink every icon evenly until everything fits,
    /// leaving room for the magnified row so it stays on screen too.
    /// </summary>
    void Fit(double length)
    {
        int n = _items.Count(i => !i.IsSeparator);
        if (n == 0) { _base = _cfg.IconSize; return; }
        double avail = length - 2 * (EdgeGap + PadMain);
        double seps = _items.Count(i => i.IsSeparator) * SepSlot;
        // Cosine magnification adds about 2.4 icons' worth of (Mag - 1) to the row's length.
        double growth = 2.4 * Math.Max(Mag - 1, 0);
        double fit = (avail - seps - n * Spacing) / (n + growth);
        _base = Math.Clamp(Math.Floor(fit), MinIcon, _cfg.IconSize);
    }

    Rect ToWin(double m0, double m1, double c0, double c1)
    {
        double w = Math.Max(m1 - m0, 0), d = Math.Max(c1 - c0, 0);
        return _cfg.Position switch
        {
            DockPosition.Bottom => new Rect(m0, ActualHeight - c1, w, d),
            DockPosition.Top => new Rect(m0, c0, w, d),
            DockPosition.Left => new Rect(c0, m0, d, w),
            _ => new Rect(ActualWidth - c1, m0, d, w),
        };
    }

    (double M, double C) FromWin(Point p) => _cfg.Position switch
    {
        DockPosition.Bottom => (p.X, ActualHeight - p.Y),
        DockPosition.Top => (p.X, p.Y),
        DockPosition.Left => (p.Y, p.X),
        _ => (p.Y, ActualWidth - p.X),
    };

    double BaseSlot(DockItem it) => it.IsSeparator ? SepSlot : Base + Spacing;
    double CurSlot(DockItem it) => it.IsSeparator ? SepSlot : it.Size + Spacing;
    double Spacing => _cfg.IconSpacing;   // space between icons

    const double IconCanvas = 192;

    /// <summary>Positions a fixed-size element and scales it to fill <paramref name="r"/>.</summary>
    static void PlaceScaled(FrameworkElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        var st = (ScaleTransform)el.RenderTransform;
        st.ScaleX = r.Width / el.Width;
        st.ScaleY = r.Height / el.Height;
    }

    static void Place(FrameworkElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        el.Width = r.Width;
        el.Height = r.Height;
    }

    static bool Approach(ref double v, double goal, double k = 0.28, double eps = 0.2)
    {
        double d = goal - v;
        if (Math.Abs(d) <= eps) { v = goal; return false; }
        v += d * k;
        return true;
    }

    // ───────────────────────── animation loop ─────────────────────────

    void Kick()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        long now = Stopwatch.GetTimestamp();
        bool moving = false;
        double B = Base, L = Horizontal ? ActualWidth : ActualHeight;

        // 1) Magnification goals, measured against the un-magnified layout so they don't feed back.
        double w0 = _items.Sum(BaseSlot), start0 = L / 2 - w0 / 2;
        double? mouse = _hidden ? null : _mouseM;
        if (mouse is double mm && Mag > 1.001)
        {
            double u = Math.Clamp(mm - start0, 0, w0), acc = 0, radius = B * 2.7;
            foreach (var it in _items)
            {
                double slot = BaseSlot(it), centre = acc + slot / 2;
                acc += slot;
                if (it.IsSeparator) continue;
                double d = Math.Abs(u - centre);
                it.GoalSize = d < radius ? B * (1 + (Mag - 1) * (Math.Cos(Math.PI * d / radius) + 1) / 2) : B;
            }
        }
        else foreach (var it in _items) it.GoalSize = B;

        foreach (var it in _items)
            if (!it.IsSeparator) moving |= Approach(ref it.Size, it.GoalSize);

        // 2) Keep the icon under the cursor under the cursor: anchor the grown row at the mouse.
        double w1 = _items.Sum(CurSlot);
        if (mouse is double m2 && Mag > 1.001)
        {
            double u = Math.Clamp(m2 - start0, 0, w0), a0 = 0, a1 = 0, mapped = double.NaN;
            foreach (var it in _items)
            {
                double s0 = BaseSlot(it), s1 = CurSlot(it);
                if (double.IsNaN(mapped) && u <= a0 + s0) mapped = a1 + (u - a0) / s0 * s1;
                a0 += s0; a1 += s1;
            }
            if (double.IsNaN(mapped)) mapped = w1;
            _shift = m2 - (L / 2 - w1 / 2 + mapped);
        }
        else moving |= Approach(ref _shift, 0);

        // 3) Auto-hide slide.
        moving |= Approach(ref _hideT, _hidden ? 1 : 0, 0.22, 0.002);
        double cOff = -_hideT * (EdgeGap + PlateDepth + 40);

        // 4) Place everything.
        double m = L / 2 - w1 / 2 + _shift;
        double lo = EdgeGap + PadMain, hi = L - EdgeGap - PadMain - w1;
        m = hi >= lo ? Math.Clamp(m, lo, hi) : L / 2 - w1 / 2; // keep the whole row on screen
        double pm0 = m - PadMain, pm1 = m + w1 + PadMain, pc0 = EdgeGap + cOff, pc1 = pc0 + PlateDepth;
        double iconBase = EdgeGap + cOff + PadEdge, maxSize = B;

        foreach (var it in _items)
        {
            double slot = CurSlot(it), mc = m + slot / 2;
            m += slot;
            it.CenterM = mc;

            if (it.IsSeparator)
            {
                PlaceScaled(it.SepLine!, ToWin(mc - 0.5, mc + 0.5, iconBase + B * 0.18, iconBase + B * 0.82));
                continue;
            }

            maxSize = Math.Max(maxSize, it.Size);
            double bounce = BounceOffset(it, now);
            if (it.BounceStart != 0) moving = true;

            PlaceScaled(it.Img!, ToWin(mc - it.Size / 2, mc + it.Size / 2, iconBase + bounce, iconBase + bounce + it.Size));
            double dc = EdgeGap + cOff + PadEdge / 2;
            Place(it.Dot!, ToWin(mc - 2, mc + 2, dc - 2, dc + 2));
            it.Dot!.Visibility = it.IsRunning ? Visibility.Visible : Visibility.Hidden;
            it.Dot.Fill = it.Attention ? AttentionBrush : _dotBrush;
            it.Img!.Opacity = _dragging && it == _pressItem && _removeArmed ? 0.35 : 1;
        }

        _plateM0 = pm0; _plateM1 = pm1;
        _plateRect = ToWin(pm0, pm1, pc0, pc1);
        _radius = Math.Round(PlateDepth * 0.3);
        Place(_tint, _plateRect);
        Place(_shine, Inflate(_plateRect, -1));
        Place(_shadow, _plateRect);
        _tint.CornerRadius = _shadow.CornerRadius = new CornerRadius(_radius);
        _shine.CornerRadius = new CornerRadius(Math.Max(_radius - 1, 0));
        _shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)),
            new RectangleGeometry(_plateRect, _radius, _radius));
        Place(_hit, ToWin(pm0, pm1, Math.Min(0, pc0), Math.Max(pc1, iconBase + maxSize + 8)));

        PlaceGlass();
        UpdateLabel();

        if (!moving)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r.Width < 0 || r.Height < 0 ? Rect.Empty : r;
    }

    double BounceOffset(DockItem it, long now)
    {
        if (it.BounceStart == 0) return 0;
        double t = (now - it.BounceStart) / (double)Stopwatch.Frequency / 0.85;
        if (t >= 1)
        {
            if (it.BouncesLeft > 0) { it.BouncesLeft--; it.BounceStart = now; return 0; }
            it.BounceStart = 0;
            return 0;
        }
        double v = t < 0.55 ? Math.Sin(Math.PI * t / 0.55) : Math.Sin(Math.PI * (t - 0.55) / 0.45) * 0.35;
        return Base * 0.45 * v;
    }

    /// <summary>Moves the blur window under the glass, clipped to this window so it never covers the taskbar.</summary>
    void PlaceGlass()
    {
        var vis = Rect.Intersect(_plateRect, new Rect(0, 0, ActualWidth, ActualHeight));
        if (vis.IsEmpty || vis.Width < 1 || vis.Height < 1)
        {
            _plate.SetShape(-32000, -32000, 0, 0, 0, 0, 0, 0, 0);
            return;
        }
        double s = _dpi;
        int R(double v) => (int)Math.Round(v * s);
        _plate.SetShape(
            R(Left + vis.X), R(Top + vis.Y), R(vis.Width), R(vis.Height),
            R(_plateRect.X - vis.X), R(_plateRect.Y - vis.Y), R(_plateRect.Width), R(_plateRect.Height),
            R(_radius));
    }

    void UpdateLabel()
    {
        var it = _hovered;
        if (it == null || it.IsSeparator || _dragging || _menuOpen || _hideT > 0.05 || string.IsNullOrEmpty(it.Name))
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        _labelText.Text = it.Name;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = _label.DesiredSize;

        double tip = EdgeGap + PadEdge + it.Size + 10;
        var a = ToWin(it.CenterM, it.CenterM, tip, tip);
        var (x, y) = _cfg.Position switch
        {
            DockPosition.Bottom => (a.X - sz.Width / 2, a.Y - sz.Height),
            DockPosition.Top => (a.X - sz.Width / 2, a.Y),
            DockPosition.Left => (a.X, a.Y - sz.Height / 2),
            _ => (a.X - sz.Width, a.Y - sz.Height / 2),
        };
        Canvas.SetLeft(_label, Math.Round(Math.Clamp(x, 4, Math.Max(4, ActualWidth - sz.Width - 4))));
        Canvas.SetTop(_label, Math.Round(Math.Clamp(y, 4, Math.Max(4, ActualHeight - sz.Height - 4))));
    }

    // ───────────────────────── theme ─────────────────────────

    void ApplyTheme()
    {
        bool light = Theme.IsLight, glass = Theme.TransparencyEnabled && _plate.IsBlurAvailable;
        Theme.ApplyMenus(light);

        // Like the Mac Dock: a light wash over the blur so the wallpaper's colour shows through,
        // a fine bright edge to define the shape, and a soft shadow.
        _tint.Background = light
            ? Theme.Vertical(glass ? 0x4DFFFFFFu : 0xF2F4F4F7u, glass ? 0x38FFFFFFu : 0xF2ECECF0u)
            : Theme.Vertical(glass ? 0x33404046u : 0xF22A2A2Eu, glass ? 0x4D26262Au : 0xF21E1E22u);
        _tint.BorderBrush = Theme.Brush(light ? 0x14000000u : 0x40000000u);
        _shine.BorderBrush = light ? Theme.Vertical(0xB3FFFFFFu, 0x40FFFFFFu) : Theme.Vertical(0x33FFFFFFu, 0x14FFFFFFu);
        ((DropShadowEffect)_shadow.Effect).Opacity = light ? 0.14 : 0.28;

        _sepBrush = Theme.Brush(light ? 0x29000000u : 0x38FFFFFFu);
        foreach (var it in new[] { _sep, _sepRun }) if (it.SepLine != null) it.SepLine.Background = _sepBrush;
        _dotBrush = Theme.Brush(light ? 0xA6000000u : 0xE6FFFFFFu);
        _more.Icon = Shell.MoreIcon(_overflow.Count, light);
        if (_more.Img != null) _more.Img.Source = _more.Icon;
        foreach (var it in _items) if (it.Dot != null) it.Dot.Fill = _dotBrush;

        _label.Background = Theme.Brush(light ? 0xEBFFFFFFu : 0xEB2C2C30u);
        _label.BorderBrush = Theme.Brush(light ? 0x1A000000u : 0x29FFFFFFu);
        _labelText.Foreground = Theme.Brush(light ? 0xFF1C1C1Eu : 0xFFF5F5F7u);
        _status?.ApplyTheme();
        Kick();
    }

    // ───────────────────────── mouse ─────────────────────────

    DockItem? ItemAt(double m) =>
        _items.FirstOrDefault(i => !i.IsSeparator && Math.Abs(m - i.CenterM) <= (i.Size + Spacing) / 2);

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_root);
        var (m, c) = FromWin(p);

        if (_pressItem != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && _pressItem.IsPinned && (p - _pressPoint).Length > 6) _dragging = true;
            if (_dragging) DragTo(m, c);
        }

        if (_hidden) return;
        _lastOver = DateTime.Now;
        bool inDock = c <= EdgeGap + PadEdge + Base * Mag + 14;
        _mouseM = inDock ? m : null;
        _hovered = _dragging || !inDock ? null : ItemAt(m);
        Kick();
    }

    void EndHover()
    {
        _mouseM = null;
        _hovered = null;
        Kick();
    }

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_root);
        _pressItem = ItemAt(FromWin(p).M);
        if (_pressItem == null) return;
        _pressPoint = p;
        _root.CaptureMouse();
    }

    void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        var it = _pressItem;
        _pressItem = null;
        _root.ReleaseMouseCapture();

        if (_dragging)
        {
            _dragging = false;
            if (_removeArmed && it != null) Unpin(it); else SaveItems();
            _removeArmed = false;
            if (!IsMouseOver) EndHover();
            Kick();
            return;
        }

        if (it != null && ItemAt(FromWin(e.GetPosition(_root)).M) == it)
            Activate(it, newInstance: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    void OnMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (ItemAt(FromWin(e.GetPosition(_root)).M) is { } it) Activate(it, newInstance: true);
    }

    /// <summary>Live reorder while dragging; dragging far off the dock arms "remove".</summary>
    void DragTo(double m, double c)
    {
        var it = _pressItem!;
        _removeArmed = c > EdgeGap + PlateDepth + Base * 1.4;
        if (!_removeArmed)
        {
            int target = PinStart + _items.Count(i => i.IsPinned && i != it && i.CenterM < m);
            int current = _items.IndexOf(it);
            if (target != current)
            {
                _items.RemoveAt(current);
                _items.Insert(target, it);
            }
        }
        Kick();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        // "Link" so Explorer never moves or deletes the file being dropped.
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(LaunchpadWindow.DragFormat) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects == DragDropEffects.None) return;
        _mouseM = FromWin(e.GetPosition(_root)).M;
        _hovered = null;
        Kick();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(LaunchpadWindow.DragFormat) is string location)
        {
            double m = FromWin(e.GetPosition(_root)).M;
            AddPinned(location, _items.Count(i => i.IsPinned && i.CenterM < m));
            SaveItems();
            RefreshRunning();
            e.Effects = DragDropEffects.Link;
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            double m = FromWin(e.GetPosition(_root)).M;
            int index = _items.Count(i => i.IsPinned && i.CenterM < m);
            foreach (var p in paths)
                if (AddPinned(p, index)) index++;
            SaveItems();
            RefreshRunning();
        }
        e.Handled = true;
        EndHover();
    }

    // ───────────────────────── launching ─────────────────────────

    void Activate(DockItem it, bool newInstance)
    {
        if (it.IsStart || it.IsMore)
        {
            ToggleLaunchpad();
            return;
        }
        if (it.IsTrash)
        {
            if (Start("explorer.exe", "shell:RecycleBinFolder")) Bounce(it);
            return;
        }

        it.Attention = false;
        it.BouncesLeft = 0;
        CountLaunch(it.Location);
        if (!newInstance)
        {
            RefreshRunning();
            var wins = it.Windows;
            if (wins.Count > 0)
            {
                var fg = Native.GetForegroundWindow();
                if (!wins.Contains(fg)) Native.Focus(wins[0]);
                else if (wins.Count == 1) Native.ShowWindow(fg, Native.SW_MINIMIZE);
                else Native.Focus(wins[^1]); // cycle through this app's windows
                return;
            }
        }

        if (Start(it.Location, it.Arguments)) Bounce(it);
    }

    void Bounce(DockItem it)
    {
        it.BounceStart = Stopwatch.GetTimestamp();
        Kick();
    }

    static bool Start(string file, string? args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true, Arguments = args ?? "" };
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                psi.WorkingDirectory = Path.GetDirectoryName(file) ?? "";
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {file}.\n\n{ex.Message}", AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // ───────────────────────── menus ─────────────────────────

    static MenuItem Item(string header, Action click, bool? check = null)
    {
        var mi = new MenuItem { Header = header };
        if (check is bool c) mi.IsChecked = c;
        mi.Click += (_, _) => click();
        return mi;
    }

    static MenuItem Sub(string header, params MenuItem[] children)
    {
        var mi = new MenuItem { Header = header };
        foreach (var c in children) mi.Items.Add(c);
        return mi;
    }

    void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var it = ItemAt(FromWin(e.GetPosition(_root)).M);
        var menu = new ContextMenu { PlacementTarget = _root, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, HorizontalOffset = -12, VerticalOffset = -8 };

        if (it is { IsApp: true })
        {
            // Like the Mac: the app's open windows first, then actions.
            RefreshRunning();
            foreach (var h in it.Windows.Take(12))
            {
                string title = Native.WindowTitle(h);
                if (title.Length > 60) title = title[..57] + "…";
                menu.Items.Add(Item(title.Replace("_", "__"), () => Native.Focus(h)));
            }
            if (it.Windows.Count > 0) menu.Items.Add(new Separator());

            menu.Items.Add(Item(it.Windows.Count > 0 ? "New window" : "Open", () => Activate(it, newInstance: true)));
            if (File.Exists(it.Location) || Directory.Exists(it.Location))
                menu.Items.Add(Item("Show in File Explorer", () => Start("explorer.exe", $"/select,\"{it.Location}\"")));
            menu.Items.Add(new Separator());
            menu.Items.Add(it.IsPinned ? Item("Remove from Dock", () => Unpin(it)) : Item("Keep in Dock", () => KeepInDock(it)));
            if (it.Windows.Count > 0)
                menu.Items.Add(Item(it.Windows.Count == 1 ? "Close window" : "Close all windows", () => CloseAll(it)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Dock settings…", () => OpenSettings()));
        }
        else if (it is { IsStart: true } or { IsMore: true })
        {
            menu.Items.Add(Item("Open Launchpad", () => ToggleLaunchpad()));
            menu.Items.Add(Item("Windows Start menu", Native.OpenStart));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Dock settings…", () => OpenSettings()));
        }
        else if (it is { IsTrash: true })
        {
            menu.Items.Add(Item("Open", () => Activate(it, false)));
            var empty = Item("Empty Recycle Bin", () => { Native.SHEmptyRecycleBin(_hwnd, null, 0); SlowTick(); });
            empty.IsEnabled = _trashCount != 0;
            menu.Items.Add(empty);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Dock settings…", () => OpenSettings()));
        }
        else
        {
            AddDockMenu(menu);
        }

        // The dock never activates, so WinDockIt is never the foreground app, and a menu only hears
        // about clicks elsewhere when its app is in the foreground. So, like a tray icon's menu, make
        // the dock foreground just before the menu opens (allowed: the user just clicked it), and hand
        // focus back afterwards if nothing else took it. Doing this after opening would steal the
        // menu's mouse capture and close it at once.
        IntPtr previous = Native.GetForegroundWindow();
        Native.SetForegroundWindow(_hwnd);
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            menu.Focus();
            Kick();
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _lastOver = DateTime.Now;
            Native.HandBackFocus(Dispatcher, previous, _hwnd);
        };
        menu.IsOpen = true;
    }

    void AddDockMenu(ContextMenu menu)
    {
        menu.Items.Add(Item("Settings…", () => OpenSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add app…", AddApps));
        menu.Items.Add(Item("Import from taskbar", ImportTaskbar));
        menu.Items.Add(new Separator());
        menu.Items.Add(Sub("Position",
            Item("Bottom", () => SetPosition(DockPosition.Bottom), _cfg.Position == DockPosition.Bottom),
            Item("Top", () => SetPosition(DockPosition.Top), _cfg.Position == DockPosition.Top),
            Item("Left", () => SetPosition(DockPosition.Left), _cfg.Position == DockPosition.Left),
            Item("Right", () => SetPosition(DockPosition.Right), _cfg.Position == DockPosition.Right)));
        menu.Items.Add(Sub("Icon size",
            SizeItem("Small", 36), SizeItem("Medium", 46), SizeItem("Large", 56), SizeItem("Extra large", 68)));
        menu.Items.Add(Sub("Magnification",
            MagItem("Off", 1), MagItem("Subtle", 1.35), MagItem("Medium", 1.75), MagItem("Large", 2.1)));
        menu.Items.Add(Item("Hide Windows taskbar", () => SetHideTaskbar(!_cfg.HideTaskbar), _cfg.HideTaskbar));
        menu.Items.Add(Item("Show status bar", () => SetStatusBar(!_cfg.ShowStatusBar), _cfg.ShowStatusBar));
        menu.Items.Add(Item("Show Launchpad button", () => { _cfg.ShowStart = !_cfg.ShowStart; _cfg.Save(); Rebuild(Pins, Transients); }, _cfg.ShowStart));
        menu.Items.Add(Sub("Hide dock",
            Item("Smart hide", () => SetHide(HideMode.Smart), _cfg.Hide == HideMode.Smart),
            Item("Keep space for the dock", () => SetHide(HideMode.KeepSpace), _cfg.Hide == HideMode.KeepSpace),
            Item("Always auto-hide", () => SetHide(HideMode.Always), _cfg.Hide == HideMode.Always)));
        menu.Items.Add(Item("Start with Windows", () => SetStartup(!IsStartup), IsStartup));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open settings folder", () => Start(DockConfig.Folder, null)));
        menu.Items.Add(Item("Quit WinDockIt", () => Application.Current.Shutdown()));
    }

    MenuItem SizeItem(string name, double size) =>
        Item(name, () => { _cfg.IconSize = size; _cfg.Save(); ApplyLayout(); }, Math.Abs(_cfg.IconSize - size) < 0.5);

    MenuItem MagItem(string name, double mag) =>
        Item(name, () => { _cfg.Magnification = mag; _cfg.Save(); ApplyLayout(); }, Math.Abs(_cfg.Magnification - mag) < 0.01);

    internal void SetHideTaskbar(bool hide)
    {
        _cfg.HideTaskbar = hide;
        _cfg.Save();
        if (hide) TaskbarHider.Hide(_cfg); else TaskbarHider.Restore(_cfg);
        ApplyLayout();
    }

    internal void SetPosition(DockPosition p)
    {
        _cfg.Position = p;
        _cfg.Save();
        _hovered = null;
        _mouseM = null;
        ApplyLayout();
    }

    internal void AddApps()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add to WinDockIt",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        if (dlg.ShowDialog() != true) return;
        int index = Pins.Count;
        foreach (var f in dlg.FileNames)
            if (AddPinned(f, index)) index++;
        SaveItems();
        RefreshRunning();
    }

    /// <summary>Adds the taskbar's pinned apps, in taskbar order, skipping any already on the dock.</summary>
    internal void ImportTaskbar()
    {
        var pins = Pins;
        var have = new HashSet<string>(pins.Select(i => Shell.IdentityKey(i.Location)));
        int added = 0;
        foreach (var it in TaskbarPins.Read())
        {
            if (!have.Add(Shell.IdentityKey(it.Location))) continue;
            Prepare(it);
            pins.Add(it);
            added++;
        }
        if (added == 0) return;
        Rebuild(pins, Transients);
        SaveItems();
        RefreshRunning();
    }

    internal static bool IsStartup
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppName) != null;
        }
    }

    internal static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k == null) return;
        if (on) k.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(AppName, throwOnMissingValue: false);
    }

    // ───────────────────────── timers ─────────────────────────

    /// <summary>Fullscreen detection and auto-hide.</summary>
    void FastTick()
    {
        if (_cfg.HideTaskbar) TaskbarHider.KeepHidden();
        bool fs = IsForegroundFullscreen();
        if (fs != _fullscreen)
        {
            _fullscreen = fs;
            if (fs) { Hide(); _plate.Hide(); }
            else { _plate.Show(); Show(); Kick(); }
            _status?.SetFullscreen(fs);
        }
        if (fs) return;

        // Smart hide: step aside only while a window overlaps the dock (checked every other tick).
        if (_cfg.Hide == HideMode.Smart && ++_coverTick % 2 == 0) _covered = Covered();
        bool wantHidden = _cfg.Hide == HideMode.Always || (_cfg.Hide == HideMode.Smart && _covered);
        if (!wantHidden)
        {
            if (_hidden) Reveal();
            return;
        }

        bool over = IsMouseOver || _menuOpen || _dragging || _pressItem != null;
        if (over)
        {
            _lastOver = DateTime.Now;
            if (_hidden) Reveal();
            return;
        }

        if (_hidden)
        {
            if (!Native.GetCursorPos(out var pt) || PresentationSource.FromVisual(this)?.CompositionTarget is not { } ct) return;
            var dip = ct.TransformFromDevice.Transform(new Point(pt.X, pt.Y));
            var (m, c) = FromWin(new Point(dip.X - Left, dip.Y - Top));
            if (c >= -6 && c <= 2 && m >= _plateM0 && m <= _plateM1) Reveal();
        }
        else if ((DateTime.Now - _lastOver).TotalMilliseconds > 800)
        {
            _hidden = true;
            EndHover();
        }
    }

    int _coverTick;
    bool _covered;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);

    /// <summary>
    /// True when some app window overlaps the spot where the dock sits when shown. Uses each
    /// window's visible frame (not its larger invisible resize border), so a window that merely
    /// ends at the dock's edge doesn't count.
    /// </summary>
    bool Covered()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0) return false;
        var r = ToWin(_plateM0, _plateM1, 0, EdgeGap + PlateDepth);
        double s = _dpi;
        return AnyWindowOver((int)((Left + r.X) * s), (int)((Top + r.Y) * s), (int)((Left + r.Right) * s), (int)((Top + r.Bottom) * s));
    }

    /// <summary>True when some app window's visible frame overlaps the given rectangle (physical pixels).</summary>
    internal static bool AnyWindowOver(int x0, int y0, int x1, int y1)
    {
        uint me = (uint)Environment.ProcessId;
        bool hit = false;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x20 /* TRANSPARENT */)) != 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (Native.GetWindowTextLength(h) == 0) return true;
            if (Native.ClassName(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetWindowAttribute(h, 9 /* EXTENDED_FRAME_BOUNDS */, out var b, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0
                && !Native.GetWindowRect(h, out b)) return true;
            if (b.Left < x1 && b.Right > x0 && b.Top < y1 && b.Bottom > y0) { hit = true; return false; }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    void Reveal()
    {
        _hidden = false;
        _lastOver = DateTime.Now;
        Kick();
    }

    bool IsForegroundFullscreen()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || Native.IsZoomed(fg)) return false;
        if (Native.ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;

        var mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (mon != Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST)) return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>Running indicators and the Recycle Bin's full/empty icon.</summary>
    void SlowTick()
    {
        RefreshRunning();
        bool changed = false;

        long n = Shell.RecycleBinCount();
        if (_trashCount < 0 || (n == 0) != (_trashCount == 0))
        {
            if (Shell.GetIcon(Shell.RecycleBinPath) is { } icon && _trash.Img != null) _trash.Img.Source = icon;
            changed = true;
        }
        _trashCount = n;

        if (changed) Kick();
    }

    // ───────────────────────── running apps ─────────────────────────

    /// <summary>
    /// Assigns every open window to a dock icon, the way the taskbar groups its buttons: a pinned
    /// item with the same app ID, else one that runs the same exe. Windows nothing claims become
    /// running-only icons after the pinned ones.
    /// </summary>
    void RefreshRunning()
    {
        if (_dragging) return;
        var windows = WindowList.Get();
        var pins = Pins;
        var groups = new Dictionary<string, List<AppWindow>>();
        foreach (var it in _items.Concat(_overflow)) it.Windows = new();

        foreach (var w in windows)
        {
            if (OwnerOf(pins, w) is { } owner) { owner.Windows.Add(w.Handle); continue; }
            if (!groups.TryGetValue(w.GroupKey, out var g)) groups[w.GroupKey] = g = new();
            g.Add(w);
        }
        if (!_cfg.ShowRunning) groups.Clear();

        // Keep existing running-only icons in place; new ones join at the end, as on the Mac.
        var old = Transients;
        var transients = old.Where(t => groups.ContainsKey(t.GroupKey!)).ToList();
        foreach (var (key, ws) in groups)
        {
            var t = transients.FirstOrDefault(x => x.GroupKey == key);
            if (t == null)
            {
                var (name, location, args, icon) = Shell.DescribeRunning(ws[0]);
                t = new DockItem { IsTransient = true, GroupKey = key, Name = name, Location = location, Arguments = args, Icon = icon, AppId = ws[0].Aumid };
                Prepare(t);
                transients.Add(t);
            }
            t.Windows = ws.Select(w => w.Handle).ToList();
        }
        if (!transients.SequenceEqual(old)) Rebuild(pins, transients);

        bool changed = false;
        foreach (var it in _items.Where(i => i.IsApp))
        {
            bool running = it.Windows.Count > 0;
            if (running != it.IsRunning) { it.IsRunning = running; changed = true; }
            if (!running && it.Attention) { it.Attention = false; changed = true; }
        }
        if (changed) Kick();
    }

    static DockItem? OwnerOf(List<DockItem> pins, AppWindow w)
    {
        static bool Eq(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return pins.FirstOrDefault(p => Eq(p.ProcessName, w.ProcessName))
            ?? pins.FirstOrDefault(p => Eq(p.AppId, w.Aumid))
            // A window without its own app ID belongs to whatever runs its exe. One with an app ID only
            // falls back to a pin that has none, so e.g. an Edge web app doesn't light up Edge.
            ?? pins.FirstOrDefault(p => Eq(p.ExePath, w.ExePath) && (w.Aumid == null || p.AppId == null));
    }

    IntPtr ShellHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            // Explorer restarted: it forgot our reserved space and brought its taskbar back.
            _appBar?.Reset();
            _status?.Reset();
            if (_cfg.HideTaskbar) TaskbarHider.Hide(_cfg);
            Dispatcher.InvokeAsync(ApplyLayout);
            return IntPtr.Zero;
        }
        if (_shellHookMsg == 0 || msg != _shellHookMsg) return IntPtr.Zero;
        int code = wParam.ToInt32();
        switch (code & 0x7FFF)
        {
            case 1: // HSHELL_WINDOWCREATED
            case 2: // HSHELL_WINDOWDESTROYED
                _refreshSoon.Stop();
                _refreshSoon.Start();
                break;
            case 4: // HSHELL_WINDOWACTIVATED (and RUDEAPPACTIVATED): the user has seen it
                foreach (var it in _items.Where(i => i.Attention && i.Windows.Contains(lParam)))
                {
                    it.Attention = false;
                    it.BouncesLeft = 0;
                    Kick();
                }
                break;
            case 6 when (code & 0x8000) != 0: // HSHELL_FLASH: a window wants attention
                Flash(lParam);
                break;
        }
        return IntPtr.Zero;
    }

    void Flash(IntPtr h)
    {
        var it = _items.FirstOrDefault(i => i.Windows.Contains(h));
        if (it == null)
        {
            RefreshRunning();
            it = _items.FirstOrDefault(i => i.Windows.Contains(h));
        }
        if (it == null || it.Attention) return;
        it.Attention = true;   // the dot turns orange until the window is activated
        it.BouncesLeft = 2;
        Bounce(it);
    }
}
