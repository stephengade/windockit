using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDockIt;

public enum DockPosition { Bottom, Top, Left, Right }

public sealed class DockConfig
{
    public DockPosition Position { get; set; } = DockPosition.Bottom;
    public double IconSize { get; set; } = 46;
    public double Magnification { get; set; } = 1.75;
    public bool AutoHide { get; set; }
    public List<DockItem> Items { get; set; } = new();

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
                    c.Items ??= new();
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
