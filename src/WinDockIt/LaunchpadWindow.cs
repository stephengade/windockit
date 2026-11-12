using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinDockIt;

/// <summary>
/// Launchpad: a frosted sheet that rises from the dock with search, the apps that are open, the
/// ones you use most, any pinned apps that didn't fit on the dock, and every app A–Z.
/// Type to search, arrows to move, Enter to open, Esc or a click elsewhere to close.
/// </summary>
internal sealed class LaunchpadWindow : Window
{
    public const string DragFormat = "WinDockIt.AppLocation";

    const double TileW = 86, TileH = 90, IconPx = 48, Cols = 7, Pad = 16, RailW = 18, Radius = 18;

    sealed record Entry(string Name, string Location, ImageSource? Icon, DockItem? Item);

    readonly DockWindow _dock;
    readonly GlassBackdrop _glass = new();
    readonly Border _rootBorder = new() { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1) };
    readonly TextBox _search = new();
    readonly TextBlock _placeholder = new() { Text = "Search apps", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _content = new();
    readonly StackPanel _rail = new() { Width = RailW, VerticalAlignment = VerticalAlignment.Center };
    readonly List<(Border Tile, Entry Entry)> _tiles = new();
    readonly Dictionary<char, Border> _letterStarts = new();
    int _selected = -1;
    bool _closing, _menuOpen;
    Point? _pressAt;
    Brush _fg = Brushes.White, _sub = Brushes.Gray, _hover = Brushes.Transparent, _selBrush = Brushes.Transparent;

    public LaunchpadWindow(DockWindow dock)
    {
        _dock = dock;
        Title = "Launchpad";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;

        // Search field.
        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        _search.FontSize = 14;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => { _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
        var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Opacity = 0.7 };
        var fieldGrid = new Grid();
        fieldGrid.Children.Add(_placeholder);
        fieldGrid.Children.Add(_search);
        var field = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(glyph, Dock.Left);
        field.Children.Add(glyph);
        field.Children.Add(fieldGrid);
        _searchBox = new Border { Child = field, Height = 36, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(11, 0, 11, 0), Margin = new Thickness(Pad, Pad, Pad, 6) };

        _scroll.Content = _content;
        _content.Margin = new Thickness(0, 0, 0, Pad);
        var body = new Grid { Margin = new Thickness(Pad - 4, 0, 4, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RailW + 4) });
        body.Children.Add(_scroll);
        Grid.SetColumn(_rail, 1);
        body.Children.Add(_rail);

        var layout = new DockPanel();
        DockPanel.SetDock(_searchBox, Dock.Top);
        layout.Children.Add(_searchBox);
        layout.Children.Add(body);
        _rootBorder.Child = layout;
        _rootBorder.RenderTransform = new TranslateTransform();
        Content = _rootBorder;

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_menuOpen) CloseSheet(); };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(h, Native.WS_EX_TOOLWINDOW);
        };
        AppCatalog.Changed += OnCatalogChanged;
        Closed += (_, _) => { AppCatalog.Changed -= OnCatalogChanged; _glass.Dispose(); };

        ApplyTheme();
        BuildRail();
        Render();
    }

    readonly Border _searchBox;

    void OnCatalogChanged() => Dispatcher.InvokeAsync(() => { if (!_closing) Render(); });

    // ───────────────────────── showing ─────────────────────────

    /// <summary>Places the sheet next to the dock and shows it with the search field focused.</summary>
    public void Open()
    {
        var (dockRect, work, position, dpi) = _dock.LaunchpadAnchor();
        const double gap = 12, margin = 8, topRoom = 36;   // keep clear of the status bar
        double w = Pad * 2 + RailW + Cols * TileW + 4, h;
        double x, y;
        switch (position)
        {
            case DockPosition.Top:
                h = Math.Min(560, work.Bottom - margin - (dockRect.Bottom + gap));
                x = dockRect.Left + dockRect.Width / 2 - w / 2; y = dockRect.Bottom + gap; break;
            case DockPosition.Left:
                h = Math.Min(560, work.Height - topRoom - margin);
                x = dockRect.Right + gap; y = dockRect.Top + dockRect.Height / 2 - h / 2; break;
            case DockPosition.Right:
                h = Math.Min(560, work.Height - topRoom - margin);
                x = dockRect.Left - gap - w; y = dockRect.Top + dockRect.Height / 2 - h / 2; break;
            default:
                h = Math.Min(560, dockRect.Top - gap - (work.Top + topRoom));
                x = dockRect.Left + dockRect.Width / 2 - w / 2; y = dockRect.Top - gap - h; break;
        }
        h = Math.Max(h, 240);
        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - margin - w));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - h));
        Left = x; Top = y; Width = w; Height = h;

        int px = (int)Math.Round(x * dpi), py = (int)Math.Round(y * dpi), pw = (int)Math.Round(w * dpi), ph = (int)Math.Round(h * dpi);
        _glass.SetShape(px, py, pw, ph, 0, 0, pw, ph, (int)Math.Round(Radius * dpi));
        _glass.Show();
        new WindowInteropHelper(this).Owner = _glass.Handle;
        Show();

        // Rise into place.
        var slide = position switch { DockPosition.Top => -10.0, DockPosition.Bottom => 10.0, _ => 0.0 };
        var tt = (TranslateTransform)_rootBorder.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slide, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        _rootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        var hwnd = new WindowInteropHelper(this).Handle;
        Activate();
        if (Native.GetForegroundWindow() != hwnd) Native.Focus(hwnd);
        _search.Focus();
        Keyboard.Focus(_search);
    }

    public void CloseSheet()
    {
        if (_closing) return;
        _closing = true;
        _glass.Hide();
        Close();
    }

    // ───────────────────────── theme ─────────────────────────

    void ApplyTheme()
    {
        bool light = Theme.IsLight, glass = Theme.TransparencyEnabled && _glass.IsBlurAvailable;
        _rootBorder.Background = light
            ? Theme.Brush(glass ? 0x8CF6F6F8u : 0xFAF4F4F7u)
            : Theme.Brush(glass ? 0x8C26262Cu : 0xFA222226u);
        _rootBorder.BorderBrush = Theme.Brush(light ? 0x1F000000u : 0x2EFFFFFFu);
        _fg = Theme.Brush(light ? 0xFF1C1C1Eu : 0xFFF5F5F7u);
        _sub = Theme.Brush(light ? 0x991C1C1Eu : 0x99F5F5F7u);
        _hover = Theme.Brush(light ? 0x14000000u : 0x1AFFFFFFu);
        _selBrush = Theme.Brush(light ? 0x24000000u : 0x2EFFFFFFu);
        Foreground = _fg;
        _search.Foreground = _search.CaretBrush = _fg;
        _placeholder.Foreground = _sub;
        _searchBox.Background = Theme.Brush(light ? 0x0F000000u : 0x14FFFFFFu);
        _searchBox.BorderBrush = Theme.Brush(light ? 0x14000000u : 0x1AFFFFFFu);
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

    Entry FromItem(DockItem it) => new(it.Name, it.Location, it.Icon, it);

    void Render()
    {
        double offset = _scroll.VerticalOffset;
        _content.Children.Clear();
        _tiles.Clear();
        _letterStarts.Clear();
        string q = _search.Text.Trim();

        if (q.Length > 0)
        {
            var matches = AllApps()
                .Select(e => (e, rank: Rank(e.Name, q)))
                .Where(x => x.rank >= 0)
                .OrderBy(x => x.rank).ThenBy(x => x.e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.e).ToList();
            Section(matches.Count > 0 ? "Results" : $"No apps match “{q}”", matches);
            Select(matches.Count > 0 ? 0 : -1);
            _scroll.ScrollToTop();
            UpdateRail(false);
            return;
        }

        var overflow = _dock.OverflowItems;
        if (overflow.Count > 0) Section("In your dock", overflow.Select(FromItem).ToList());

        var open = _dock.OpenApps;
        if (open.Count > 0) Section("Open now", open.Select(FromItem).ToList());

        var frequent = _dock.FrequentApps(7, l => AppCatalog.Apps.FirstOrDefault(a => a.Location.Equals(l, StringComparison.OrdinalIgnoreCase)));
        if (frequent.Count > 0) Section("Frequently used", frequent.Select(f => new Entry(f.Name, f.Location, f.Icon, _dock.ItemFor(f.Location))).ToList());

        var all = AppCatalog.Apps.Select(a => new Entry(a.Name, a.Location, a.Icon, _dock.ItemFor(a.Location))).ToList();
        Section(all.Count > 0 ? "All apps" : "Loading apps…", all, letters: true);

        Select(-1);
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
        UpdateRail(true);
    }

    /// <summary>Lower is better: name starts with the query, then a word starts with it, then it appears anywhere. -1 = no match.</summary>
    static int Rank(string name, string q)
    {
        if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 0;
        foreach (var word in name.Split(' ', '-', '.', '(', '_'))
            if (word.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 1;
        return name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ? 2 : -1;
    }

    void Section(string label, List<Entry> entries, bool letters = false)
    {
        _content.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = _sub, Margin = new Thickness(6, 12, 0, 6) });
        var wrap = new WrapPanel { ItemWidth = TileW, ItemHeight = TileH };
        foreach (var e in entries)
        {
            var tile = Tile(e);
            wrap.Children.Add(tile);
            if (letters)
            {
                char c = char.ToUpperInvariant(e.Name.FirstOrDefault(' '));
                if (!char.IsLetter(c)) c = '#';
                _letterStarts.TryAdd(c, tile);
            }
        }
        _content.Children.Add(wrap);
    }

    Border Tile(Entry e)
    {
        var img = new Image { Source = e.Icon ?? Shell.LetterIcon(e.Name), Width = IconPx, Height = IconPx, Margin = new Thickness(0, 8, 0, 0) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = e.Name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = TileW - 10, Margin = new Thickness(0, 5, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(img);
        stack.Children.Add(name);
        if (e.Item is { Windows.Count: > 0 })
            stack.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = _fg, Margin = new Thickness(0, 3, 0, 0) });
        var tile = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(10), Margin = new Thickness(2), ToolTip = e.Name,
            Background = Theme.Brush(0x01000000u),
        };
        ToolTipService.SetInitialShowDelay(tile, 900);
        int index = _tiles.Count;
        _tiles.Add((tile, e));

        tile.MouseEnter += (_, _) => { if (_selected != index) tile.Background = _hover; };
        tile.MouseLeave += (_, _) => { if (_selected != index) tile.Background = Theme.Brush(0x01000000u); };
        tile.MouseLeftButtonDown += (_, ev) => { _pressAt = ev.GetPosition(this); tile.CaptureMouse(); ev.Handled = true; };
        tile.MouseMove += (_, ev) =>
        {
            if (_pressAt is not { } p || ev.LeftButton != MouseButtonState.Pressed) return;
            var d = ev.GetPosition(this) - p;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
            _pressAt = null;
            tile.ReleaseMouseCapture();
            // Drag onto the dock to pin it.
            var data = new DataObject(DragFormat, e.Location);
            _menuOpen = true;   // the drag must not count as clicking away
            try
            {
                if (DragDrop.DoDragDrop(tile, data, DragDropEffects.Link) != DragDropEffects.None) CloseSheet();
            }
            finally { _menuOpen = false; }
        };
        tile.MouseLeftButtonUp += (_, ev) =>
        {
            tile.ReleaseMouseCapture();
            if (_pressAt == null) return;
            _pressAt = null;
            ev.Handled = true;
            Launch(e, newInstance: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        tile.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ShowMenu(e, tile); };
        return tile;
    }

    void Launch(Entry e, bool newInstance = false)
    {
        CloseSheet();
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
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (!IsActive) CloseSheet(); };
        menu.IsOpen = true;
    }

    static MenuItem MenuItem(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ───────────────────────── letter rail ─────────────────────────

    const string Letters = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    void BuildRail()
    {
        foreach (char c in Letters)
        {
            var t = new TextBlock { Text = c.ToString(), FontSize = 10, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 0.5, 0, 0.5), Background = Theme.Brush(0x01000000u), Cursor = Cursors.Hand };
            char letter = c;
            t.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; JumpTo(letter); };
            _rail.Children.Add(t);
        }
    }

    void UpdateRail(bool show)
    {
        _rail.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        foreach (TextBlock t in _rail.Children)
            t.Foreground = _letterStarts.ContainsKey(t.Text[0]) ? _fg : _sub;
    }

    void JumpTo(char c)
    {
        int i = Letters.IndexOf(c);
        // Nothing under this letter: use the next one that has apps.
        for (; i < Letters.Length; i++)
            if (_letterStarts.TryGetValue(Letters[i], out var tile))
            {
                var y = tile.TranslatePoint(new Point(0, 0), _content).Y;
                _scroll.ScrollToVerticalOffset(Math.Max(0, y - 30));
                return;
            }
    }

    // ───────────────────────── keyboard ─────────────────────────

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_search.Text.Length > 0) _search.Text = ""; else CloseSheet();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_selected >= 0 && _selected < _tiles.Count) Launch(_tiles[_selected].Entry, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Down: Move((int)Cols); e.Handled = true; break;
            case Key.Up: Move(-(int)Cols); e.Handled = true; break;
            case Key.Right when _selected >= 0 || _search.Text.Length == 0: Move(1); e.Handled = true; break;
            case Key.Left when _selected >= 0 || _search.Text.Length == 0: Move(-1); e.Handled = true; break;
            case Key.Tab: Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; break;
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
        if (_selected >= 0 && _selected < _tiles.Count) _tiles[_selected].Tile.Background = Theme.Brush(0x01000000u);
        _selected = index;
        if (_selected >= 0 && _selected < _tiles.Count) _tiles[_selected].Tile.Background = _selBrush;
    }
}
