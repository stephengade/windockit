using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDockIt;

public enum DockPosition { Bottom, Top, Left, Right }

public enum ThemeMode { System, Light, Dark }

public enum HideMode { KeepSpace, Smart, Always }

/// <summary>The dock's shape.</summary>
public enum DockStyle
{
    /// <summary>Rounded glass lifted off the screen edge.</summary>
    Floating,
    /// <summary>Glass along the whole edge, like a taskbar.</summary>
    FullWidth,
    /// <summary>Centred glass resting on the edge, rounded only on the inside.</summary>
    Attached,
    /// <summary>Smaller icons in a fully rounded capsule.</summary>
    Pill,
    /// <summary>Pinned apps in one piece of glass; open apps and the Recycle Bin in another.</summary>
    Split,
    /// <summary>No glass until the pointer is over the dock.</summary>
    Minimal,
}

/// <summary>A colour mixed into the frosted glass of the dock, status bar and Launchpad.</summary>
public enum GlassTint { Clear, Graphite, Ocean, Plum, Rose, Forest, Sunset }

/// <summary>The highlight colour for running dots, Launchpad's index and Settings' controls.</summary>
public enum AccentColor { Windows, Blue, Purple, Pink, Red, Orange, Green, Graphite }

public sealed class DockConfig
{
    public DockPosition Position { get; set; } = DockPosition.Bottom;
    public double IconSize { get; set; } = 46;
    public double Magnification { get; set; } = 1.75;
    /// <summary>Space between icons, in DIPs.</summary>
    public double IconSpacing { get; set; } = 12;
    /// <summary>Always slide away until the screen edge is touched.</summary>
    public bool AutoHide { get; set; }
    /// <summary>Reserve no space; step aside only while a window overlaps the dock. Ignored when <see cref="AutoHide"/> is on.</summary>
    public bool SmartHide { get; set; } = true;
    /// <summary>Running apps that aren't pinned get their own icons after a divider.</summary>
    public bool ShowRunning { get; set; } = true;
    /// <summary>The frosted blur behind the dock and status bar (also needs Windows' transparency effects).</summary>
    public bool Glass { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public DockStyle Style { get; set; } = DockStyle.Floating;
    public GlassTint Tint { get; set; } = GlassTint.Clear;
    /// <summary>How much of <see cref="Tint"/> goes into the glass, 0–100.</summary>
    public double TintStrength { get; set; } = 35;
    public AccentColor Accent { get; set; } = AccentColor.Windows;
    public bool ShowStart { get; set; } = true;
    public bool HideTaskbar { get; set; }
    /// <summary>The Mac-style bar along the top with Wi-Fi, Bluetooth, volume, battery and the clock.</summary>
    public bool ShowStatusBar { get; set; } = true;
    /// <summary>How the status bar gets out of the way: keep its strip, hide while a window covers it, or always hide.</summary>
    public HideMode StatusBarHide { get; set; } = HideMode.KeepSpace;
    /// <summary>The taskbar's own auto-hide setting before WinDockIt hid it; set only while it's hidden.</summary>
    public bool? TaskbarWasAutoHide { get; set; }
    public List<DockItem> Items { get; set; } = new();
    /// <summary>How often each app was opened from the dock or Launchpad, for Launchpad's "Frequently used".</summary>
    public Dictionary<string, int> LaunchCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HideMode Hide
    {
        get => AutoHide ? HideMode.Always : SmartHide ? HideMode.Smart : HideMode.KeepSpace;
        set { AutoHide = value == HideMode.Always; SmartHide = value == HideMode.Smart; }
    }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDockIt");

    public static string FilePath => Path.Combine(Folder, "config.json");

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static DockConfig Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var c = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(FilePath), Opts);
                if (c != null)
                {
                    c.IconSize = Math.Clamp(c.IconSize, 24, 128);
                    c.Magnification = Math.Clamp(c.Magnification, 1, 3);
                    c.IconSpacing = Math.Clamp(c.IconSpacing, 2, 32);
                    c.TintStrength = Math.Clamp(c.TintStrength, 0, 100);
                    c.Items ??= new();
                    c.LaunchCounts = new(c.LaunchCounts ?? new(), StringComparer.OrdinalIgnoreCase);
                    c.Items.RemoveAll(i => string.IsNullOrWhiteSpace(i.Location));
                    c.Migrate();
                    return c;
                }
            }
            catch (Exception ex)
            {
                // Keep the unreadable file instead of silently losing someone's dock.
                App.Log(ex);
                try { File.Copy(FilePath, Path.Combine(Folder, "config.broken.json"), true); } catch { }
            }
        }

        var d = new DockConfig { Items = Defaults() };
        d.Save();
        return d;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // Store apps have no real .exe to point at; their shell entry gives the proper icon and launches them.
    const string TerminalApp = @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

    static bool TerminalInstalled => Directory.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe"));

    /// <summary>Upgrades entries written by earlier builds.</summary>
    void Migrate()
    {
        foreach (var i in Items)
            if (i.Location.EndsWith(@"\Microsoft\WindowsApps\wt.exe", StringComparison.OrdinalIgnoreCase))
                i.Location = TerminalApp;
    }

    /// <summary>First run: start from the user's taskbar pins if there are any, otherwise a sensible set.</summary>
    static List<DockItem> Defaults()
    {
        var pins = TaskbarPins.Read();
        if (pins.Count > 0) return pins;
        return BuiltInDefaults();
    }

    static List<DockItem> BuiltInDefaults()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new (string Name, string[] Paths, string? Proc)[]
        {
            ("File Explorer", [Path.Combine(win, "explorer.exe")], null),
            ("Microsoft Edge", [Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")], null),
            ("Google Chrome", [Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"), Path.Combine(local, @"Google\Chrome\Application\chrome.exe")], null),
            ("Firefox", [Path.Combine(pf, @"Mozilla Firefox\firefox.exe")], null),
            ("Visual Studio Code", [Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"), Path.Combine(pf, @"Microsoft VS Code\Code.exe")], null),
            ("Terminal", [TerminalApp], "WindowsTerminal"),
            ("Downloads", [Path.Combine(home, "Downloads")], null),
        };

        var list = new List<DockItem>();
        foreach (var (name, paths, proc) in candidates)
        {
            var p = paths.FirstOrDefault(x => File.Exists(x) || Directory.Exists(x) || (x == TerminalApp && TerminalInstalled));
            if (p != null) list.Add(new DockItem { Name = name, Location = p, ProcessName = proc });
        }
        return list;
    }
}
