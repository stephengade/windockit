using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinDockIt;

/// <summary>
/// Launchpad: covers the screen with a blurred, dimmed copy of the wallpaper and shows every app in
/// large icons. Search at the top, a Suggestions strip (open and frequently used apps), any pinned
/// apps that didn't fit on the dock, then all apps grouped A–Z with an index down the side.
/// The dock stays on top, so apps can be dragged onto it.
/// </summary>
internal sealed class LaunchpadWindow : Window
{
    public const string DragFormat = "WinDockIt.AppLocation";

    sealed record Entry(string Name, string Location, ImageSource? Icon, DockItem? Item);

    static readonly Brush White = Brushes.White;
    static readonly Brush Clear = Theme.Brush(0x01000000u);   // fully clear pixels don't receive clicks
    readonly Brush Accent = Theme.AccentBrush();

    readonly DockWindow _dock;
    readonly GlassBackdrop _glass = new();
    readonly Grid _root = new();
    readonly Grid _stage = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly Border _searchBox = new();
    readonly TextBox _search = new();
    readonly TextBlock _placeholder = new() { Text = "Search", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(0xB3FFFFFFu) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    readonly StackPanel _content = new();
    readonly Border _rail = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(3, 6, 3, 6) };
    readonly StackPanel _railLetters = new();
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly Border _bubble = new() { Width = 60, Height = 60, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Opacity = 0 };
    readonly TextBlock _bubbleText = new() { FontSize = 30, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = White };

    readonly List<(Border Tile, Entry Entry)> _tiles = new();
    readonly List<(char Letter, FrameworkElement Header)> _groups = new();
    readonly Dictionary<string, Image> _catalogImages = new(StringComparer.OrdinalIgnoreCase);
    int _renderedCatalog = -1;
    int _selected = -1;
    char _activeLetter;
    bool _closing, _busy;       // _busy: a menu or drag is in progress, so losing focus mustn't close us
    Point? _pressAt;
    bool _railDown;
    readonly Border _close = new();
    bool _following;
    DispatcherTimer? _renderSoon;

    // Sizes, worked out from the screen in Open().
    double _icon = 72, _cellW = 150, _cellH = 124;
    int _cols = 8;

    // Smooth scrolling.
    double _scrollTarget;
    bool _scrolling;

    public LaunchpadWindow(DockWindow dock)
    {
        _dock = dock;
        Title = "Launchpad";
        WindowStyle = WindowStyle.None;
        // Not a layered (AllowsTransparency) window: those are drawn on the CPU and copied every frame,
        // which made a full-screen Launchpad slow to open and to animate. Transparency comes from DWM instead.
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Foreground = White;

        bool glass = Theme.TransparencyEnabled && _glass.IsBlurAvailable;
        // A dark wash over the blurred wallpaper keeps white labels readable on any background.
        _root.Background = Theme.Brush(Theme.Tinted(glass ? 0x66101420u : 0xF2181B26u));
        _root.Children.Add(_stage);
        _root.Children.Add(_rail);
        _root.Children.Add(_close);
        _root.Children.Add(_overlay);
        Content = _root;

        BuildSearch();
        BuildClose();
        _scroll.Content = _content;
        _scroll.Background = Clear;   // so the wheel works over the gaps between apps
        PreviewMouseWheel += OnWheel;
        _scroll.ScrollChanged += (_, _) => { if (!_scrolling) _scrollTarget = _scroll.VerticalOffset; UpdateActiveLetter(); };
        // Fade the grid out at the top and bottom edges instead of cutting it off.
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.035));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.95));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _scroll.OpacityMask = mask;

        var layout = new DockPanel();
        DockPanel.SetDock(_searchBox, System.Windows.Controls.Dock.Top);
        layout.Children.Add(_searchBox);
        layout.Children.Add(_scroll);
        _stage.Children.Add(layout);
        _stage.RenderTransform = new ScaleTransform(1, 1);

        _rail.Background = Theme.Brush(0x1AFFFFFFu);
        _rail.BorderBrush = Theme.Brush(0x24FFFFFFu);
        _rail.Child = _railLetters;
        _rail.MouseMove += (_, e) => Scrub(e.GetPosition(_railLetters).Y, instant: true);
        _rail.MouseLeftButtonDown += (_, e) => { _railDown = true; _rail.CaptureMouse(); Scrub(e.GetPosition(_railLetters).Y, instant: true); e.Handled = true; };
        _rail.MouseLeftButtonUp += (_, e) => { _railDown = false; _rail.ReleaseMouseCapture(); e.Handled = true; };
        _rail.MouseLeave += (_, _) => { if (!_railDown) HideBubble(); };
        _rail.LostMouseCapture += (_, _) => { _railDown = false; HideBubble(); };

        _bubble.Background = Theme.Brush(0x33FFFFFFu);
        _bubble.BorderBrush = Theme.Brush(0x4DFFFFFFu);
        _bubble.Child = _bubbleText;
        _overlay.Children.Add(_bubble);

        // Clicking anywhere that isn't an app, the search field or the index closes Launchpad.
        _root.MouseLeftButtonUp += (_, e) => { if (!e.Handled && _pressAt == null) CloseSheet(); };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_busy) CloseSheet(); };
        SourceInitialized += (_, _) => PrepareWindow();
        AppCatalog.Changed += OnCatalogChanged;
        Closed += (_, _) =>
        {
            AppCatalog.Changed -= OnCatalogChanged;
            if (_scrolling) CompositionTarget.Rendering -= StepScroll;
            if (_following) CompositionTarget.Rendering -= SyncGlass;
            _renderSoon?.Stop();
            _glass.Dispose();
        };
    }

    void BuildSearch()
    {
        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.Foreground = _search.CaretBrush = White;
        _search.FontSize = 14.5;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Render();
        };
        var glyph = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0), Foreground = Theme.Brush(0xCCFFFFFFu),
        };
        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(_search);
        var row = new DockPanel();
        DockPanel.SetDock(glyph, System.Windows.Controls.Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(field);
        _searchBox.Child = row;
        _searchBox.Width = 340;
        _searchBox.Height = 38;
        _searchBox.CornerRadius = new CornerRadius(19);
        _searchBox.BorderThickness = new Thickness(1);
        _searchBox.Padding = new Thickness(14, 0, 14, 0);
        _searchBox.HorizontalAlignment = HorizontalAlignment.Center;
        _searchBox.Background = Theme.Brush(0x29FFFFFFu);
        _searchBox.BorderBrush = Theme.AccentBrush(0x8C);   // search always has the focus
        _searchBox.MouseLeftButtonUp += (_, e) => e.Handled = true;
    }

    void OnCatalogChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (_closing) return;
        // Icons arriving for apps already on screen: swap them in without rebuilding the grid.
        if (_search.Text.Length == 0 && AppCatalog.Apps.Count == _renderedCatalog)
        {
            foreach (var a in AppCatalog.Apps)
                if (a.Icon != null && _catalogImages.TryGetValue(a.Location, out var img) && img.Source != a.Icon) img.Source = a.Icon;
        }
        else RenderSoon();
    });

    // ───────────────────────── showing ─────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// <summary>
    /// A normal, GPU-drawn window whose clear areas DWM shows through (to the blur window behind it),
    /// with no Windows 11 rounded corners or border. Cloaked until its first frame is ready.
    /// </summary>
    void PrepareWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(h, Native.WS_EX_TOOLWINDOW);
        if (HwndSource.FromHwnd(h)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(h, ref m);
        int noRound = 1 /* DWMWCP_DONOTROUND */, noBorder = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */;
        DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref noRound, sizeof(int));
        DwmSetWindowAttribute(h, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));
        SetCloak(true);
    }

    void SetCloak(bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 13 /* DWMWA_CLOAK */, ref v, sizeof(int));
    }

    public void Open()
    {
        var (dock, mon, position, dpi) = _dock.LaunchpadAnchor();
        double x = mon.Left / dpi, y = mon.Top / dpi, w = (mon.Right - mon.Left) / dpi, h = (mon.Bottom - mon.Top) / dpi;
        Left = x; Top = y; Width = w; Height = h;

        // Room the dock takes on its edge, so nothing sits underneath it.
        double padL = 0, padR = 0, padT = 0, padB = 0;
        switch (position)
        {
            case DockPosition.Bottom: padB = y + h - dock.Top; break;
            case DockPosition.Top: padT = dock.Bottom - y; break;
            case DockPosition.Left: padL = dock.Right - x; break;
            case DockPosition.Right: padR = x + w - dock.Left; break;
        }

        // Icons scale with the screen: about 72px on a 1080p screen.
        _icon = Math.Round(Math.Clamp(h / 15, 56, 88));
        double side = Math.Max(110, w * 0.08);
        double avail = w - padL - padR - 2 * side;
        _cols = (int)Math.Clamp(Math.Floor(avail / (_icon * 1.9)), 4, 8);
        _cellW = Math.Floor(Math.Min(_icon * 2.3, avail / _cols));
        _cellH = Math.Round(_icon + 54);

        _searchBox.Margin = new Thickness(0, padT + 34, 0, 10);
        _scroll.Width = _cols * _cellW;
        _scroll.HorizontalAlignment = HorizontalAlignment.Center;
        _stage.Margin = new Thickness(padL, 0, padR, padB + 8);
        _rail.Margin = new Thickness(0, padT + 80, padR + 22, padB + 30);

        Render();
        _close.Margin = new Thickness(0, padT + 34, padR + 22, 0);

        // Start fully transparent, blur included, so nothing pops in before the fade begins.
        _root.Opacity = 0;
        _glass.SetOpacity(0);
        int px = mon.Left, py = mon.Top, pw = mon.Right - mon.Left, ph = mon.Bottom - mon.Top;
        _glass.SetShape(px, py, pw, ph, 0, 0, pw, ph, 0);
        _glass.Show();
        new WindowInteropHelper(this).Owner = _glass.Handle;

        // The window stays cloaked (invisible) until its first frame is drawn, then fades in while the
        // apps settle from slightly larger, like the Mac. Starting the fade earlier wasted it on frames
        // nobody saw, so Launchpad seemed to pop in.
        ContentRendered += (_, _) =>
        {
            SetCloak(false);
            FollowFade();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            var st = (ScaleTransform)_stage.RenderTransform;
            var zoom = new DoubleAnimation(1.04, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        };
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        Activate();
        if (Native.GetForegroundWindow() != hwnd) Native.Focus(hwnd);
        _dock.RaiseAboveLaunchpad();
        _search.Focus();
        Keyboard.Focus(_search);
    }

    /// <summary>Fades out and closes. Pass <paramref name="quick"/> when an app is opening, so it isn't kept waiting.</summary>
    public void CloseSheet(bool quick = false)
    {
        if (_closing) return;
        _closing = true;
        if (quick) { _glass.Hide(); Close(); return; }
        FollowFade();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => { _glass.Hide(); Close(); };
        _root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// The blur lives in its own window, so it doesn't fade with the content by itself. Matching it to the
    /// content's opacity every frame stops the blur from appearing or vanishing all at once.
    /// </summary>
    void FollowFade()
    {
        if (_following) return;
        _following = true;
        CompositionTarget.Rendering += SyncGlass;
    }

    void SyncGlass(object? sender, EventArgs e)
    {
        _glass.SetOpacity(_root.Opacity);
        bool settled = _closing ? _root.Opacity <= 0.001 : _root.Opacity >= 0.999;
        if (!settled) return;
        _following = false;
        CompositionTarget.Rendering -= SyncGlass;
    }

    /// <summary>The catalog reports in batches as icons load; rebuilding the grid for each one made it flicker.</summary>
    void RenderSoon()
    {
        if (_renderSoon == null)
        {
            _renderSoon = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            _renderSoon.Tick += (_, _) => { _renderSoon.Stop(); if (!_closing) Render(); };
        }
        _renderSoon.Stop();
        _renderSoon.Start();
    }

    /// <summary>A plain close button in the top corner, for anyone who doesn't know clicking the background closes Launchpad.</summary>
    void BuildClose()
    {
        _close.Width = _close.Height = 38;
        _close.CornerRadius = new CornerRadius(19);
        _close.BorderThickness = new Thickness(1);
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.VerticalAlignment = VerticalAlignment.Top;
        _close.Cursor = Cursors.Hand;
        _close.ToolTip = "Close (Esc)";
        Brush rest = Theme.Brush(0x29FFFFFFu), hover = Theme.Brush(0x47FFFFFFu), down = Theme.Brush(0x1AFFFFFFu);
        _close.Background = rest;
        _close.BorderBrush = Theme.Brush(0x33FFFFFFu);
        _close.Child = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
            Foreground = White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _close.MouseEnter += (_, _) => _close.Background = hover;
        _close.MouseLeave += (_, _) => _close.Background = rest;
        _close.MouseLeftButtonDown += (_, e) => { _close.Background = down; e.Handled = true; };
        _close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseSheet(); };
    }

    // ───────────────────────── content ─────────────────────────

    List<Entry> AllApps()
    {
        var list = AppCatalog.Apps.Select(a => new Entry(a.Name, a.Location, a.Icon, _dock.ItemFor(a.Location))).ToList();
        // Pinned things that aren't in All apps (files, folders, portable apps) are still searchable.
        var have = new HashSet<string>(list.Select(e => e.Location), StringComparer.OrdinalIgnoreCase);
        foreach (var it in _dock.PinnedItems)
            if (have.Add(it.Location)) list.Add(new Entry(it.Name, it.Location, it.Icon, it));
        return list;
    }

    static Entry FromItem(DockItem it) => new(it.Name, it.Location, it.Icon, it);

    void Render()
    {
        double offset = _scroll.VerticalOffset;
        _content.Children.Clear();
        _tiles.Clear();
        _groups.Clear();
        _catalogImages.Clear();
        _railLetterList.Clear();
        _selected = -1;
        _renderGen++;
        string q = _search.Text.Trim();

        if (q.Length > 0)
        {
            var matches = AllApps()
                .Select(e => (e, rank: Rank(e.Name, q)))
                .Where(x => x.rank >= 0)
                .OrderBy(x => x.rank).ThenBy(x => x.e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.e).ToList();
            Label(matches.Count > 0 ? "Results" : $"No apps match “{q}”", top: 8);
            _content.Children.Add(TileGrid(matches));
            Select(matches.Count > 0 ? 0 : -1);
            _renderedCatalog = -1;
            _scrollTarget = 0;
            _scroll.ScrollToTop();
            BuildRail();
            return;
        }

        var overflow = _dock.OverflowItems;
        if (overflow.Count > 0)
        {
            Label("In your dock", top: 8);
            _content.Children.Add(TileGrid(overflow.Select(FromItem).ToList()));
        }

        // Suggestions: what's open, then what you use most.
        var suggestions = _dock.OpenApps.Select(FromItem).ToList();
        var frequent = _dock.FrequentApps(_cols * 2, l => AppCatalog.Apps.FirstOrDefault(a => a.Location.Equals(l, StringComparison.OrdinalIgnoreCase)));
        foreach (var f in frequent)
            if (suggestions.Count < _cols && !suggestions.Any(s => s.Location.Equals(f.Location, StringComparison.OrdinalIgnoreCase)))
                suggestions.Add(new Entry(f.Name, f.Location, f.Icon, _dock.ItemFor(f.Location)));
        if (suggestions.Count > 0)
        {
            Label("Suggestions", top: overflow.Count > 0 ? 18 : 8);
            var strip = TileGrid(suggestions.Take(_cols).ToList());
            _content.Children.Add(new Border
            {
                Child = strip, CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1), Padding = new Thickness(6, 6, 6, 4),
                Background = Theme.Brush(0x14FFFFFFu), BorderBrush = Theme.Brush(0x1AFFFFFFu), Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,   // hugs its apps, so two suggestions don't sit in a huge empty box
            });
        }

        // All apps, grouped under letter headings.
        var apps = AppCatalog.Apps;
        _renderedCatalog = apps.Count;
        if (apps.Count == 0) Label("Loading apps…", top: 18);
        var groups = apps.GroupBy(a => LetterOf(a.Name)).OrderBy(g => g.Key == '#' ? 0 : 1).ThenBy(g => g.Key)
            .Select(g => (g.Key, Apps: g.ToList())).ToList();
        _railLetterList = groups.Select(g => g.Key).ToList();

        // Laying out hundreds of tiles before the first frame kept Launchpad blank for half a second.
        // So only enough to fill the screen (plus whatever the old scroll position needs) is built now;
        // the rest follows straight after, once the window is showing.
        int gen = _renderGen;
        double budget = (ActualHeight > 0 ? ActualHeight : SystemParameters.PrimaryScreenHeight) + offset + _cellH * 2, used = 0;
        int built = 0;
        foreach (var (letter, list) in groups)
        {
            if (used > budget) break;
            AddGroup(letter, list);
            used += 60 + Math.Ceiling(list.Count / (double)_cols) * _cellH;
            built++;
        }
        var rest = groups.Skip(built).ToList();
        if (rest.Count == 0) _content.Children.Add(new Border { Height = 24 });
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (gen != _renderGen || _closing) return;
            foreach (var (letter, list) in rest) AddGroup(letter, list);
            _content.Children.Add(new Border { Height = 24 });
            UpdateActiveLetter();
        });

        BuildRail();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
        _scrollTarget = offset;
        UpdateActiveLetter();
    }

    void AddGroup(char letter, List<CatalogApp> list)
    {
        var header = Header(letter);
        _groups.Add((letter, header));
        _content.Children.Add(header);
        _content.Children.Add(TileGrid(list.Select(a => new Entry(a.Name, a.Location, a.Icon, _dock.ItemFor(a.Location))).ToList(), catalog: true));
    }

    int _renderGen;
    List<char> _railLetterList = new();

    static char LetterOf(string name)
    {
        char c = char.ToUpperInvariant(name.FirstOrDefault(' '));
        return c is >= 'A' and <= 'Z' ? c : '#';
    }

    /// <summary>Lower is better: name starts with the query, then a word starts with it, then it appears anywhere. -1 = no match.</summary>
    static int Rank(string name, string q)
    {
        if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 0;
        foreach (var word in name.Split(' ', '-', '.', '(', '_'))
            if (word.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 1;
        return name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ? 2 : -1;
    }

    void Label(string text, double top) =>
        _content.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Theme.Brush(0xB3FFFFFFu),
            Margin = new Thickness(Inset, top, 0, 8),
        });

    double Inset => Math.Max(8, (_cellW - _icon) / 2 - 6);

    FrameworkElement Header(char letter)
    {
        var g = new Grid { Margin = new Thickness(Inset, 22, Inset, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = letter.ToString(), FontSize = 24, FontWeight = FontWeights.Medium, Foreground = White });
        var line = new Rectangle { Height = 1, Fill = Theme.Brush(0x29FFFFFFu), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 3, 0, 0) };
        Grid.SetColumn(line, 1);
        g.Children.Add(line);
        return g;
    }

    WrapPanel TileGrid(List<Entry> entries, bool catalog = false)
    {
        var wrap = new WrapPanel { ItemWidth = _cellW, ItemHeight = _cellH };
        foreach (var e in entries) wrap.Children.Add(Tile(e, catalog));
        return wrap;
    }

    Border Tile(Entry e, bool catalog)
    {
        var img = new Image { Source = e.Icon ?? Shell.LetterIcon(e.Name), Width = _icon, Height = _icon };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (catalog) _catalogImages[e.Location] = img;
        // The same soft shadow as the dock's icons, rendered once and cached so scrolling stays smooth.
        var lift = new Grid
        {
            Width = _icon, Height = _icon, Margin = new Thickness(0, 8, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new DropShadowEffect { BlurRadius = _icon * 0.22, ShadowDepth = _icon * 0.05, Direction = 270, Opacity = 0.38 },
            CacheMode = new BitmapCache { RenderAtScale = 1.25, SnapsToDevicePixels = true },
        };
        lift.Children.Add(img);

        var name = new TextBlock
        {
            Text = e.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = _cellW - 16, Margin = new Thickness(0, 8, 0, 0), Foreground = White,
        };
        var stack = new StackPanel();
        stack.Children.Add(lift);
        stack.Children.Add(name);
        stack.Children.Add(new Ellipse
        {
            Width = 4, Height = 4, Fill = White, Margin = new Thickness(0, 4, 0, 0),
            Visibility = e.Item is { Windows.Count: > 0 } ? Visibility.Visible : Visibility.Hidden,
        });

        var tile = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(16), Margin = new Thickness(4, 2, 4, 2), Background = Clear,
        };
        if (e.Name.Length > 22) { tile.ToolTip = e.Name; ToolTipService.SetInitialShowDelay(tile, 900); }   // only for cut-off names
        int index = _tiles.Count;
        _tiles.Add((tile, e));

        var scale = (ScaleTransform)lift.RenderTransform;
        void ScaleTo(double v, int ms)
        {
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        tile.MouseEnter += (_, _) => ScaleTo(1.08, 140);
        tile.MouseLeave += (_, _) => { ScaleTo(1, 160); if (_pressAt != null) { _pressAt = null; tile.ReleaseMouseCapture(); } };
        tile.MouseLeftButtonDown += (_, ev) => { _pressAt = ev.GetPosition(this); tile.CaptureMouse(); ScaleTo(0.94, 90); ev.Handled = true; };
        tile.MouseMove += (_, ev) =>
        {
            if (_pressAt is not { } p || ev.LeftButton != MouseButtonState.Pressed) return;
            var d = ev.GetPosition(this) - p;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            _pressAt = null;
            tile.ReleaseMouseCapture();
            ScaleTo(1, 120);
            // Drag onto the dock to pin it.
            _busy = true;
            try
            {
                if (DragDrop.DoDragDrop(tile, new DataObject(DragFormat, e.Location), DragDropEffects.Link) != DragDropEffects.None) Render();
            }
            finally { _busy = false; }
        };
        tile.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            tile.ReleaseMouseCapture();
            if (_pressAt == null) return;
            _pressAt = null;
            ScaleTo(1.08, 90);
            Launch(e, newInstance: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        tile.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ShowMenu(e, tile); };
        return tile;
    }

    void Launch(Entry e, bool newInstance = false)
    {
        CloseSheet(quick: true);
        _dock.LaunchFromLaunchpad(e.Location, e.Item, newInstance);
    }

    void ShowMenu(Entry e, Border tile)
    {
        var menu = new ContextMenu { PlacementTarget = tile, Placement = PlacementMode.MousePoint };
        bool running = e.Item is { Windows.Count: > 0 };
        menu.Items.Add(MenuItem(running ? "Show" : "Open", () => Launch(e)));
        if (running) menu.Items.Add(MenuItem("New window", () => Launch(e, newInstance: true)));
        if (e.Item is not { IsPinned: true })
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Keep in Dock", () => { _dock.PinFromLaunchpad(e.Location, e.Name); Render(); }));
        }
        menu.Opened += (_, _) => _busy = true;
        menu.Closed += (_, _) => { _busy = false; if (!IsActive) CloseSheet(); };
        menu.IsOpen = true;
    }

    static MenuItem MenuItem(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ───────────────────────── A–Z index ─────────────────────────

    /// <summary>Only the letters that have apps; hidden while searching.</summary>
    void BuildRail()
    {
        _railLetters.Children.Clear();
        _rail.Visibility = _railLetterList.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var letter in _railLetterList)
        {
            _railLetters.Children.Add(new Border
            {
                Width = 22, Height = 19, CornerRadius = new CornerRadius(9.5), Margin = new Thickness(0, 1, 0, 1), Background = Clear, Tag = letter,
                Child = new TextBlock
                {
                    Text = letter.ToString(), FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = Theme.Brush(0xD9FFFFFFu),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        _activeLetter = '\0';
    }

    /// <summary>Hovering or dragging along the index jumps through the groups, with a big letter bubble beside it.</summary>
    void Scrub(double y, bool instant)
    {
        int n = _railLetters.Children.Count;
        if (n == 0) return;
        double each = _railLetters.ActualHeight / n;
        int i = Math.Clamp((int)(y / Math.Max(each, 1)), 0, n - 1);
        var cell = (Border)_railLetters.Children[i];
        char letter = (char)cell.Tag;
        ShowBubble(letter, cell);
        if (letter != _activeLetter) JumpTo(letter, instant);
    }

    void ShowBubble(char letter, Border cell)
    {
        _bubbleText.Text = letter.ToString();
        var p = cell.TranslatePoint(new Point(0, cell.ActualHeight / 2), _root);
        Canvas.SetLeft(_bubble, p.X - _bubble.Width - 18);
        Canvas.SetTop(_bubble, p.Y - _bubble.Height / 2);
        if (_bubble.Opacity < 1) _bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
    }

    void HideBubble() => _bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));

    void JumpTo(char letter, bool instant)
    {
        var header = _groups.FirstOrDefault(g => g.Letter == letter).Header;
        if (header == null) return;
        double y = Math.Max(0, header.TranslatePoint(new Point(0, 0), _content).Y - 6);
        y = Math.Min(y, _scroll.ScrollableHeight);
        if (instant)
        {
            StopScroll();
            _scroll.ScrollToVerticalOffset(y);
            _scrollTarget = y;
        }
        else ScrollTo(y);
    }

    /// <summary>Highlights the index letter for the group at the top of the view.</summary>
    void UpdateActiveLetter()
    {
        if (_groups.Count == 0) return;
        char active = _groups[0].Letter;
        double top = _scroll.VerticalOffset + 40;
        foreach (var (letter, header) in _groups)
        {
            if (header.TranslatePoint(new Point(0, 0), _content).Y > top) break;
            active = letter;
        }
        if (active == _activeLetter) return;
        _activeLetter = active;
        foreach (Border b in _railLetters.Children)
        {
            bool on = (char)b.Tag == active;
            b.Background = on ? Accent : Clear;
            ((TextBlock)b.Child).Foreground = on ? White : Theme.Brush(0xD9FFFFFFu);
        }
    }

    // ───────────────────────── smooth scrolling ─────────────────────────

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ScrollTo(_scrollTarget - e.Delta * 1.1);
    }

    void ScrollTo(double y)
    {
        _scrollTarget = Math.Clamp(y, 0, _scroll.ScrollableHeight);
        if (_scrolling) return;
        _scrolling = true;
        CompositionTarget.Rendering += StepScroll;
    }

    void StepScroll(object? sender, EventArgs e)
    {
        double cur = _scroll.VerticalOffset, diff = _scrollTarget - cur;
        if (Math.Abs(diff) < 0.5) { _scroll.ScrollToVerticalOffset(_scrollTarget); StopScroll(); return; }
        _scroll.ScrollToVerticalOffset(cur + diff * 0.2);
    }

    void StopScroll()
    {
        if (!_scrolling) return;
        _scrolling = false;
        CompositionTarget.Rendering -= StepScroll;
    }

    // ───────────────────────── keyboard ─────────────────────────

    void OnKey(object sender, KeyEventArgs e)
    {
        bool searching = _search.Text.Length > 0;
        switch (e.Key)
        {
            case Key.Escape:
                if (searching) _search.Text = ""; else CloseSheet();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_selected >= 0 && _selected < _tiles.Count) Launch(_tiles[_selected].Entry, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Down: Move(_cols); e.Handled = true; break;
            case Key.Up: Move(-_cols); e.Handled = true; break;
            case Key.Right when _selected >= 0 || !searching: Move(1); e.Handled = true; break;
            case Key.Left when _selected >= 0 || !searching: Move(-1); e.Handled = true; break;
            case Key.Tab: Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; break;
            case Key.PageDown or Key.PageUp when !searching && _groups.Count > 0:
                int i = Math.Max(0, _groups.FindIndex(g => g.Letter == _activeLetter));
                i = Math.Clamp(i + (e.Key == Key.PageDown ? 1 : -1), 0, _groups.Count - 1);
                JumpTo(_groups[i].Letter, instant: false);
                e.Handled = true;
                break;
        }
    }

    void Move(int delta)
    {
        if (_tiles.Count == 0) return;
        Select(_selected < 0 ? 0 : Math.Clamp(_selected + delta, 0, _tiles.Count - 1));
        _tiles[_selected].Tile.BringIntoView();
    }

    void Select(int index)
    {
        if (_selected >= 0 && _selected < _tiles.Count) _tiles[_selected].Tile.Background = Clear;
        _selected = index;
        if (_selected >= 0 && _selected < _tiles.Count) _tiles[_selected].Tile.Background = Theme.Brush(0x24FFFFFFu);
    }
}
