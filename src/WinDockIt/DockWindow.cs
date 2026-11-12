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
    const double Spacing = 6;    // space between icons
    const double SepSlot = 17;   // width of the divider slot
    const double ReserveGap = 6; // space kept between the glass and maximized windows
    const double MinIcon = 16;   // smallest icons get when shrinking to fit
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "WinDockIt";

    readonly DockConfig _cfg;
    readonly List<DockItem> _items = new();
    readonly DockItem _sep = new() { IsSeparator = true };
    readonly DockItem _trash = new() { IsTrash = true, Name = "Recycle Bin", Location = Shell.RecycleBinPath };

    readonly Canvas _root = new();
    readonly Canvas _shadowHost = new() { IsHitTestVisible = false };
    readonly Border _shadow = new() { Background = Brushes.Black };
    readonly Border _tint = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    readonly Border _shine = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    readonly Rectangle _hit = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    readonly Border _sepLine = new() { IsHitTestVisible = false };
    readonly Canvas _itemLayer = new();
    readonly Border _label = new()
    {
        CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5), BorderThickness = new Thickness(1),
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    readonly TextBlock _labelText = new() { FontSize = 12.5 };
    readonly GlassBackdrop _plate = new();
    AppBar? _appBar;
    readonly DispatcherTimer _fastTimer, _slowTimer;
    Brush _dotBrush = Brushes.Black;

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
        foreach (var el in new UIElement[] { _shadowHost, _tint, _shine, _hit, _sepLine, _itemLayer, _label })
            _root.Children.Add(el);

        foreach (var it in _cfg.Items) { Prepare(it); _items.Add(it); }
        Prepare(_sep); Prepare(_trash);
        _items.Add(_sep); _items.Add(_trash);

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

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            _appBar = new AppBar(_hwnd);
            _appBar.Changed += () => Dispatcher.InvokeAsync(ApplyLayout);
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
        Application.Current.Exit += (_, _) => _appBar?.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _appBar?.Dispose();
        _plate.Show();
        new WindowInteropHelper(this).Owner = _plate.Handle;
        Show();
        Kick();
    }

    // ───────────────────────── items ─────────────────────────

    void Prepare(DockItem it)
    {
        it.Size = it.GoalSize = Base;
        if (it.IsSeparator) return;

        it.Icon = Shell.GetIcon(it.Location) ?? Shell.LetterIcon(it.Name);
        it.Img = new Image { Source = it.Icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(it.Img, BitmapScalingMode.HighQuality);
        it.Img.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.26 };
        it.Dot = new Ellipse { Width = 4, Height = 4, Fill = _dotBrush, IsHitTestVisible = false };
        _itemLayer.Children.Add(it.Img);
        _itemLayer.Children.Add(it.Dot);
        it.MatchName = it.IsTrash ? null : Shell.ResolveProcessName(it);
    }

    int PinnedCount => _items.Count(i => i.IsPinned);

    bool AddPinned(string path, int index)
    {
        string key = Shell.IdentityKey(path);
        if (_items.Any(i => i.IsPinned && Shell.IdentityKey(i.Location) == key)) return false;
        var it = new DockItem { Name = Shell.DisplayName(path), Location = path };
        Prepare(it);
        _items.Insert(Math.Clamp(index, 0, PinnedCount), it);
        return true;
    }

    void Unpin(DockItem it)
    {
        _items.Remove(it);
        _itemLayer.Children.Remove(it.Img);
        _itemLayer.Children.Remove(it.Dot);
        if (_hovered == it) _hovered = null;
        SaveItems();
        ApplyLayout();
    }

    void SaveItems()
    {
        _cfg.Items = _items.Where(i => i.IsPinned).ToList();
        _cfg.Save();
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

    int ReserveThickness() => _cfg.AutoHide ? 0 : (int)Math.Ceiling((EdgeGap + PlateDepth + ReserveGap) * _dpi);

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
    static double CurSlot(DockItem it) => it.IsSeparator ? SepSlot : it.Size + Spacing;

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
                Place(_sepLine, ToWin(mc - 0.5, mc + 0.5, iconBase + B * 0.18, iconBase + B * 0.82));
                continue;
            }

            maxSize = Math.Max(maxSize, it.Size);
            double bounce = BounceOffset(it, now);
            if (it.BounceStart != 0) moving = true;

            Place(it.Img!, ToWin(mc - it.Size / 2, mc + it.Size / 2, iconBase + bounce, iconBase + bounce + it.Size));
            double dc = EdgeGap + cOff + PadEdge / 2;
            Place(it.Dot!, ToWin(mc - 2, mc + 2, dc - 2, dc + 2));
            it.Dot!.Visibility = it.IsRunning ? Visibility.Visible : Visibility.Hidden;
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
        if (t >= 1) { it.BounceStart = 0; return 0; }
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

        _tint.Background = light
            ? Theme.Vertical(glass ? 0x80FFFFFFu : 0xF2F4F4F7u, glass ? 0x66FFFFFFu : 0xF2ECECF0u)
            : Theme.Vertical(glass ? 0x8A2C2C30u : 0xF22A2A2Eu, glass ? 0x991C1C20u : 0xF21E1E22u);
        _tint.BorderBrush = Theme.Brush(light ? 0x1F000000u : 0x80000000u);
        _shine.BorderBrush = light ? Theme.Vertical(0xCCFFFFFFu, 0x33FFFFFFu) : Theme.Vertical(0x38FFFFFFu, 0x0DFFFFFFu);
        ((DropShadowEffect)_shadow.Effect).Opacity = light ? 0.22 : 0.45;

        _sepLine.Background = Theme.Brush(light ? 0x29000000u : 0x38FFFFFFu);
        _dotBrush = Theme.Brush(light ? 0xA6000000u : 0xE6FFFFFFu);
        foreach (var it in _items) if (it.Dot != null) it.Dot.Fill = _dotBrush;

        _label.Background = Theme.Brush(light ? 0xEBFFFFFFu : 0xEB2C2C30u);
        _label.BorderBrush = Theme.Brush(light ? 0x1A000000u : 0x29FFFFFFu);
        _labelText.Foreground = Theme.Brush(light ? 0xFF1C1C1Eu : 0xFFF5F5F7u);
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
            int target = _items.Count(i => i.IsPinned && i != it && i.CenterM < m);
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects == DragDropEffects.None) return;
        _mouseM = FromWin(e.GetPosition(_root)).M;
        _hovered = null;
        Kick();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            double m = FromWin(e.GetPosition(_root)).M;
            int index = _items.Count(i => i.IsPinned && i.CenterM < m);
            foreach (var p in paths)
                if (AddPinned(p, index)) index++;
            SaveItems();
            ApplyLayout();
        }
        e.Handled = true;
        EndHover();
    }

    // ───────────────────────── launching ─────────────────────────

    void Activate(DockItem it, bool newInstance)
    {
        if (it.IsTrash)
        {
            if (Start("explorer.exe", "shell:RecycleBinFolder")) Bounce(it);
            return;
        }

        if (!newInstance && it.MatchName != null)
        {
            var wins = WindowList.Get()
                .Where(w => string.Equals(w.Process, it.MatchName, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.Handle).ToList();
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

        if (it is { IsPinned: true })
        {
            menu.Items.Add(Item(it.IsRunning ? "New window" : "Open", () => Activate(it, newInstance: true)));
            if (File.Exists(it.Location) || Directory.Exists(it.Location))
                menu.Items.Add(Item("Show in File Explorer", () => Start("explorer.exe", $"/select,\"{it.Location}\"")));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove from Dock", () => Unpin(it)));
        }
        else if (it is { IsTrash: true })
        {
            menu.Items.Add(Item("Open", () => Activate(it, false)));
            var empty = Item("Empty Recycle Bin", () => { Native.SHEmptyRecycleBin(_hwnd, null, 0); SlowTick(); });
            empty.IsEnabled = _trashCount != 0;
            menu.Items.Add(empty);
        }
        else
        {
            AddDockMenu(menu);
        }

        menu.Opened += (_, _) => { _menuOpen = true; Kick(); };
        menu.Closed += (_, _) => { _menuOpen = false; _lastOver = DateTime.Now; };
        menu.IsOpen = true;
    }

    void AddDockMenu(ContextMenu menu)
    {
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
        menu.Items.Add(Item("Automatically hide", () => { _cfg.AutoHide = !_cfg.AutoHide; _cfg.Save(); ApplyLayout(); }, _cfg.AutoHide));
        menu.Items.Add(Item("Start with Windows", () => SetStartup(!IsStartup), IsStartup));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open settings folder", () => Start(DockConfig.Folder, null)));
        menu.Items.Add(Item("Quit WinDockIt", () => Application.Current.Shutdown()));
    }

    MenuItem SizeItem(string name, double size) =>
        Item(name, () => { _cfg.IconSize = size; _cfg.Save(); ApplyLayout(); }, Math.Abs(_cfg.IconSize - size) < 0.5);

    MenuItem MagItem(string name, double mag) =>
        Item(name, () => { _cfg.Magnification = mag; _cfg.Save(); ApplyLayout(); }, Math.Abs(_cfg.Magnification - mag) < 0.01);

    void SetPosition(DockPosition p)
    {
        _cfg.Position = p;
        _cfg.Save();
        _hovered = null;
        _mouseM = null;
        ApplyLayout();
    }

    void AddApps()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add to WinDockIt",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        if (dlg.ShowDialog() != true) return;
        int index = PinnedCount;
        foreach (var f in dlg.FileNames)
            if (AddPinned(f, index)) index++;
        SaveItems();
        ApplyLayout();
    }

    /// <summary>Adds the taskbar's pinned apps, in taskbar order, skipping any already on the dock.</summary>
    void ImportTaskbar()
    {
        var have = new HashSet<string>(_items.Where(i => i.IsPinned).Select(i => Shell.IdentityKey(i.Location)));
        int index = PinnedCount, added = 0;
        foreach (var it in TaskbarPins.Read())
        {
            if (!have.Add(Shell.IdentityKey(it.Location))) continue;
            Prepare(it);
            _items.Insert(index++, it);
            added++;
        }
        if (added == 0) return;
        SaveItems();
        ApplyLayout();
    }

    static bool IsStartup
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppName) != null;
        }
    }

    static void SetStartup(bool on)
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
        bool fs = IsForegroundFullscreen();
        if (fs != _fullscreen)
        {
            _fullscreen = fs;
            if (fs) { Hide(); _plate.Hide(); }
            else { _plate.Show(); Show(); Kick(); }
        }
        if (fs) return;

        if (!_cfg.AutoHide)
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
        var running = new HashSet<string>(WindowList.Get().Select(w => w.Process), StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var it in _items.Where(i => i.IsPinned))
        {
            bool r = it.MatchName != null && running.Contains(it.MatchName);
            if (r != it.IsRunning) { it.IsRunning = r; changed = true; }
        }

        long n = Shell.RecycleBinCount();
        if (_trashCount < 0 || (n == 0) != (_trashCount == 0))
        {
            if (Shell.GetIcon(Shell.RecycleBinPath) is { } icon && _trash.Img != null) _trash.Img.Source = icon;
            changed = true;
        }
        _trashCount = n;

        if (changed) Kick();
    }
}
