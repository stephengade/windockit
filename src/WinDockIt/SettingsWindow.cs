using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace WinDockIt;

/// <summary>
/// WinDockIt's settings: a sidebar with Appearance, Behaviour, Apps and About. Every change applies
/// to the dock straight away, so the dock itself is the preview, and is saved at once.
/// </summary>
public sealed class SettingsWindow : Window
{
    const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    readonly DockWindow _dock;
    readonly Grid _layout = new();
    readonly StackPanel _nav = new() { Margin = new Thickness(10, 14, 10, 10) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _page = new() { Margin = new Thickness(28, 22, 28, 28) };
    readonly Border _side = new();
    string _current = "appearance";
    Palette P = Palette.Make(true);

    static readonly (string Key, string Title, string Glyph)[] Pages =
    [
        ("appearance", "Appearance", ""),
        ("behaviour", "Behaviour", ""),
        ("apps", "Apps", ""),
        ("about", "About", ""),
    ];

    sealed record Palette(Brush Bg, Brush Side, Brush Card, Brush Line, Brush Text, Brush Sub, Brush Accent, Brush AccentSoft,
        Brush Hover, Brush Track, Brush SegBg, Brush Off, bool Light)
    {
        public static Palette Make(bool light) => light
            ? new(Theme.Brush(0xFFF7F7F9u), Theme.Brush(0xFFEDEDF0u), Theme.Brush(0xFFFFFFFFu), Theme.Brush(0x17000000u),
                Theme.Brush(0xFF1C1C1Eu), Theme.Brush(0xFF6E6E73u), Theme.Brush(0xFF007AFFu), Theme.Brush(0x24007AFFu),
                Theme.Brush(0x0D000000u), Theme.Brush(0x1F000000u), Theme.Brush(0x0F000000u), Theme.Brush(0x29000000u), true)
            : new(Theme.Brush(0xFF1E1E20u), Theme.Brush(0xFF252528u), Theme.Brush(0xFF2C2C2Fu), Theme.Brush(0x1FFFFFFFu),
                Theme.Brush(0xFFF5F5F7u), Theme.Brush(0xFF98989Du), Theme.Brush(0xFF0A84FFu), Theme.Brush(0x330A84FFu),
                Theme.Brush(0x12FFFFFFu), Theme.Brush(0x29FFFFFFu), Theme.Brush(0x14FFFFFFu), Theme.Brush(0x40FFFFFFu), false);
    }

    internal SettingsWindow(DockWindow dock)
    {
        _dock = dock;
        Title = "WinDockIt Settings";
        Icon = Shell.AppIcon();
        Width = 780;
        Height = 600;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        _side.Child = _nav;
        _layout.Children.Add(_side);
        _scroll.Content = _page;
        Grid.SetColumn(_scroll, 1);
        _layout.Children.Add(_scroll);
        Content = _layout;

        _dock.PinsChanged += OnPinsChanged;
        Closed += (_, _) => _dock.PinsChanged -= OnPinsChanged;
        SourceInitialized += (_, _) => StyleTitleBar();
        Restyle();
    }

    void OnPinsChanged()
    {
        if (_current == "apps") Render();
    }

    public void ShowPage(string key)
    {
        _current = key;
        RenderNav();
        Render();
    }

    /// <summary>Repaints everything in the current light/dark palette.</summary>
    void Restyle()
    {
        P = Palette.Make(Theme.IsLight);
        Background = P.Bg;
        Foreground = P.Text;
        _side.Background = P.Side;
        _side.BorderBrush = P.Line;
        _side.BorderThickness = new Thickness(0, 0, 1, 0);
        StyleTitleBar();
        RenderNav();
        Render();
    }

    /// <summary>Dark or light title bar to match, coloured like the page on Windows 11.</summary>
    void StyleTitleBar()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int dark = P.Light ? 0 : 1;
        DwmSetWindowAttribute(h, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        var c = ((SolidColorBrush)P.Bg).Color;
        int colorRef = c.R | (c.G << 8) | (c.B << 16);
        DwmSetWindowAttribute(h, 35 /* DWMWA_CAPTION_COLOR */, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    // ───────────────────────── sidebar ─────────────────────────

    void RenderNav()
    {
        _nav.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 16) };
        head.Children.Add(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = P.Accent, Margin = new Thickness(0, 0, 10, 0),
            Child = new Border
            {
                Width = 16, Height = 5, CornerRadius = new CornerRadius(2), Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5),
            },
        });
        head.Children.Add(new TextBlock { Text = "WinDockIt", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _nav.Children.Add(head);

        foreach (var (key, title, glyph) in Pages)
        {
            bool on = key == _current;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 15, Width = 26, VerticalAlignment = VerticalAlignment.Center, Foreground = on ? P.Accent : P.Sub });
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Foreground = on ? P.Accent : P.Text, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal });
            var b = new Border { Child = row, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 2) };
            Clickable(b, on ? P.AccentSoft : Brushes.Transparent, on ? P.AccentSoft : P.Hover, () => ShowPage(key));
            _nav.Children.Add(b);
        }
    }

    // ───────────────────────── pages ─────────────────────────

    void Render()
    {
        _page.Children.Clear();
        var cfg = _dock.Config;
        switch (_current)
        {
            case "appearance": RenderAppearance(cfg); break;
            case "behaviour": RenderBehaviour(cfg); break;
            case "apps": RenderApps(); break;
            default: RenderAbout(); break;
        }
    }

    void RenderAppearance(DockConfig cfg)
    {
        Heading("Appearance");
        Note("Changes apply to the dock straight away.");

        var positions = new[] { DockPosition.Bottom, DockPosition.Left, DockPosition.Right, DockPosition.Top };
        double[] mags = [1, 1.35, 1.75, 2.1];
        int mag = Array.FindIndex(mags, m => Math.Abs(m - cfg.Magnification) < 0.01);

        Group(
            Row("Position on screen", null, Segmented(["Bottom", "Left", "Right", "Top"], Array.IndexOf(positions, cfg.Position),
                i => _dock.SetPosition(positions[i]))),
            Row("Icon size", "Icons shrink below this when the dock is full.", Slider(32, 72, 2, cfg.IconSize, v => $"{v:0} px", v => _dock.SetIconSize(v))),
            Row("Space between icons", null, Slider(2, 24, 1, cfg.IconSpacing, v => $"{v:0} px", v => _dock.SetIconSpacing(v))),
            Row("Magnification", null, Segmented(["Off", "Subtle", "Medium", "Large"], mag, i => _dock.SetMagnification(mags[i]))));

        Group(
            Row("Theme", null, Segmented(["Match Windows", "Light", "Dark"], (int)cfg.Theme, i =>
            {
                _dock.SetTheme((ThemeMode)i);
                Restyle();
            })),
            Row("Frosted glass", "Blurs what's behind the dock and status bar. Also needs Windows' transparency effects.",
                Toggle(cfg.Glass, _dock.SetGlass)));
    }

    void RenderBehaviour(DockConfig cfg)
    {
        Heading("Behaviour");

        Label("Dock: when a window is near it");
        (HideMode Mode, string Title, string Hint)[] modes =
        [
            (HideMode.Smart, "Smart hide", "Windows use the whole screen. The dock steps aside only while one overlaps it."),
            (HideMode.KeepSpace, "Keep space for the dock", "Maximized windows stop above the dock, like the taskbar."),
            (HideMode.Always, "Always auto-hide", "Slides away until you touch the screen edge."),
        ];
        Group(modes.Select(m => Choice(m.Title, m.Hint, cfg.Hide == m.Mode, () => { _dock.SetHide(m.Mode); Render(); },
            m.Mode == HideMode.Smart ? "Recommended" : null)).ToArray());

        Label("Status bar");
        HideMode[] barModes = [HideMode.KeepSpace, HideMode.Smart, HideMode.Always];
        Group(
            Row("Show status bar", "Wi-Fi, Bluetooth, volume, battery and the clock along the top of the screen.",
                Toggle(cfg.ShowStatusBar, on => { _dock.SetStatusBar(on); Render(); })),
            Row("Hide the status bar", cfg.StatusBarHide switch
                {
                    HideMode.Smart => "Steps aside while a window covers it. Rest the cursor at the top edge to bring it back.",
                    HideMode.Always => "Stays out of sight until you rest the cursor at the top edge.",
                    _ => "Never: maximized windows stop below the bar.",
                },
                Enable(cfg.ShowStatusBar, Segmented(["Never", "Smart", "Always"], Array.IndexOf(barModes, cfg.StatusBarHide),
                    i => { _dock.SetStatusBarHide(barModes[i]); Render(); }))));

        Label("General");
        Group(
            Row("Hide the Windows taskbar", "Comes back when WinDockIt quits.", Toggle(cfg.HideTaskbar, _dock.SetHideTaskbar)),
            Row("Show running apps that aren't pinned", "They appear after a divider and leave when closed.",
                Toggle(cfg.ShowRunning, _dock.SetShowRunning)),
            Row("Show Launchpad button", "Your apps A–Z, with search. Right-click it for the Windows Start menu.", Toggle(cfg.ShowStart, _dock.SetShowStart)),
            Row("Start with Windows", null, Toggle(DockWindow.IsStartup, DockWindow.SetStartup)));
    }

    void RenderApps()
    {
        Heading("Apps");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        buttons.Children.Add(Button("", "Add app", _dock.AddApps));
        buttons.Children.Add(Button("", "Import from taskbar", _dock.ImportTaskbar));
        _page.Children.Add(buttons);

        var pins = _dock.PinnedItems;
        Label($"Pinned, in dock order ({pins.Count})");
        if (pins.Count == 0)
        {
            Note("Nothing pinned yet. Drag apps onto the dock, or use Add app.");
            return;
        }
        var rows = new List<UIElement>();
        for (int i = 0; i < pins.Count; i++)
        {
            var it = pins[i];
            var line = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(IconButton("", "Move up", i > 0, () => _dock.MovePin(it, -1)));
            actions.Children.Add(IconButton("", "Move down", i < pins.Count - 1, () => _dock.MovePin(it, 1)));
            actions.Children.Add(IconButton("", "Remove from dock", true, () => _dock.RemovePin(it)));
            DockPanel.SetDock(actions, Dock.Right);
            line.Children.Add(actions);

            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = it.Icon ?? it.Img?.Source;
            if (icon != null)
            {
                var img = new Image { Source = icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                name.Children.Add(img);
            }
            name.Children.Add(new TextBlock { Text = it.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            line.Children.Add(name);
            rows.Add(new Border { Child = line, Padding = new Thickness(14, 7, 8, 7) });
        }
        Group(rows.ToArray());
        Note("You can also drag icons along the dock to reorder them, or off it to remove them.");
    }

    void RenderAbout()
    {
        Heading("About");
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        Group(
            Row("Version", null, new TextBlock { Text = version, Foreground = P.Sub }),
            Row("Settings file", DockConfig.FilePath, Button(null, "Open folder", () => Open(DockConfig.Folder))),
            Row("Licence", null, new TextBlock { Text = "MIT", Foreground = P.Sub }));
        Group(Row("Quit WinDockIt", "Removes the dock and status bar, and brings the taskbar back if it was hidden.",
            Button(null, "Quit", () => Application.Current.Shutdown())));
    }

    // ───────────────────────── building blocks ─────────────────────────

    void Heading(string text) =>
        _page.Children.Add(new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });

    void Label(string text) =>
        _page.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = P.Sub, Margin = new Thickness(2, 0, 0, 6) });

    void Note(string text) =>
        _page.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = P.Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, -4, 0, 16) });

    /// <summary>A rounded card of rows separated by hairlines.</summary>
    void Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = P.Line, Margin = new Thickness(14, 0, 0, 0) });
            stack.Children.Add(rows[i]);
        }
        _page.Children.Add(new Border
        {
            Child = stack, Background = P.Card, BorderBrush = P.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 18),
        });
    }

    static UIElement Enable(bool on, UIElement el)
    {
        el.IsEnabled = on;
        el.Opacity = on ? 1 : 0.45;
        return el;
    }

    UIElement Row(string title, string? hint, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(14, 10, 14, 10), MinHeight = 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Texts(title, hint));
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    StackPanel Texts(string title, string? hint, string? badge = null)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = title });
        if (badge != null)
            line.Children.Add(new Border
            {
                Background = P.AccentSoft, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock { Text = badge, FontSize = 11, Foreground = P.Accent },
            });
        texts.Children.Add(line);
        if (hint != null)
            texts.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = P.Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return texts;
    }

    /// <summary>One option of a pick-one list: the whole row is clickable, with a radio mark on the right.</summary>
    UIElement Choice(string title, string hint, bool on, Action pick, string? badge)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Texts(title, hint, badge));
        var mark = new Grid { Width = 20, Height = 20, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(new Ellipse { Stroke = on ? P.Accent : P.Off, StrokeThickness = on ? 6 : 1.5, Fill = on ? Brushes.White : Brushes.Transparent });
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);
        var b = new Border { Child = grid, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10) };
        Clickable(b, Brushes.Transparent, P.Hover, () => { if (!on) pick(); });
        return b;
    }

    /// <summary>An on/off switch.</summary>
    UIElement Toggle(bool value, Action<bool> set)
    {
        var knob = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2) };
        var track = new Border { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), Background = value ? P.Accent : P.Off, Child = knob, Cursor = Cursors.Hand };
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            value = !value;
            knob.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            track.Background = value ? P.Accent : P.Off;
            Try(() => set(value));
        };
        return track;
    }

    /// <summary>A row of options, one selected, like a macOS segmented control.</summary>
    UIElement Segmented(string[] labels, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var chips = new List<Border>();
        void Paint()
        {
            for (int i = 0; i < chips.Count; i++)
            {
                bool on = i == selected;
                chips[i].Background = on ? P.Card : Brushes.Transparent;
                chips[i].BorderBrush = on ? P.Line : Brushes.Transparent;
                ((TextBlock)chips[i].Child).Foreground = on ? P.Text : P.Sub;
            }
        }
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var chip = new Border
            {
                Child = new TextBlock { Text = labels[i], FontSize = 12 }, Padding = new Thickness(11, 4, 11, 5),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0),
            };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (index == selected) return;
                selected = index;
                Paint();
                Try(() => pick(index));
            };
            chips.Add(chip);
            row.Children.Add(chip);
        }
        Paint();
        return new Border { Child = row, Background = P.SegBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
    }

    /// <summary>A slider with its value shown beside it; applies as you drag.</summary>
    UIElement Slider(double min, double max, double step, double value, Func<double, string> format, Action<double> set)
    {
        const double W = 180;
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = P.Accent, HorizontalAlignment = HorizontalAlignment.Left };
        var thumb = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, Stroke = P.Line, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Grid { Width = W, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        track.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = P.Track });
        track.Children.Add(fill);
        track.Children.Add(thumb);
        var text = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, Foreground = P.Sub, VerticalAlignment = VerticalAlignment.Center };

        void Show(double v)
        {
            double t = (v - min) / (max - min);
            fill.Width = Math.Max(0, t * (W - 18) + 9);
            thumb.Margin = new Thickness(t * (W - 18), 0, 0, 0);
            text.Text = format(v);
        }
        void At(double x)
        {
            double v = Math.Round((min + Math.Clamp((x - 9) / (W - 18), 0, 1) * (max - min)) / step) * step;
            if (Math.Abs(v - value) < step / 2) return;
            value = v;
            Show(v);
            Try(() => set(v));
        }
        track.MouseLeftButtonDown += (_, e) => { track.CaptureMouse(); At(e.GetPosition(track).X); e.Handled = true; };
        track.MouseMove += (_, e) => { if (track.IsMouseCaptured) At(e.GetPosition(track).X); };
        track.MouseLeftButtonUp += (_, _) => track.ReleaseMouseCapture();
        Show(value);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(track);
        row.Children.Add(text);
        return row;
    }

    Border Button(string? glyph, string text, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null)
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 13, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border
        {
            Child = row, Padding = new Thickness(12, 5, 12, 6), CornerRadius = new CornerRadius(7),
            BorderBrush = P.Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0),
        };
        Clickable(b, P.Card, P.Hover, click);
        return b;
    }

    UIElement IconButton(string glyph, string tip, bool enabled, Action click)
    {
        var b = new Border
        {
            Width = 30, Height = 28, CornerRadius = new CornerRadius(6), ToolTip = tip,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 12, Foreground = P.Sub,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
            Opacity = enabled ? 1 : 0.3,
        };
        if (enabled) Clickable(b, Brushes.Transparent, P.Hover, click);
        else b.Background = Brushes.Transparent;
        AutomationProperties(b, tip);
        return b;
    }

    static void AutomationProperties(UIElement el, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(el, name);

    static void Clickable(Border b, Brush normal, Brush hover, Action click)
    {
        b.Background = normal;
        b.Cursor = Cursors.Hand;
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = normal;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Try(click);
        };
    }

    static void Try(Action a)
    {
        try { a(); } catch (Exception ex) { App.Log(ex); }
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }
}
