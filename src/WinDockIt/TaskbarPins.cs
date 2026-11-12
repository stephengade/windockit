using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinDockIt;

/// <summary>
/// Reads the apps pinned to the Windows taskbar, in taskbar order.
///
/// Only some pins exist as shortcut files; Store apps and others are stored only in Explorer's
/// "Taskband\Favorites" registry value. That value is a 0x00 byte, a 4-byte length and a shell
/// item ID list for each pin, ending with 0xFF. Asking the shell for each item's parsing name
/// gives either a shortcut path or a Store app ID.
/// </summary>
internal static class TaskbarPins
{
    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

    static string PinFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>Copies of imported shortcuts, so unpinning from the taskbar later doesn't break the dock.</summary>
    static string ShortcutFolder => Path.Combine(DockConfig.Folder, "Shortcuts");

    public static List<DockItem> Read()
    {
        var pins = FromRegistry();
        if (pins.Count == 0)
            pins = Directory.Exists(PinFolder)
                ? Directory.GetFiles(PinFolder, "*.lnk").Select(f => (Path.GetFileNameWithoutExtension(f), f)).ToList()
                : new();

        var items = new List<DockItem>();
        foreach (var (name, parse) in pins)
        {
            try
            {
                if (ToItem(name, parse) is { } it) items.Add(it);
            }
            catch (Exception ex) { App.Log(ex); }
        }
        return items;
    }

    static DockItem? ToItem(string name, string parse)
    {
        if (parse.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(parse))
        {
            // The built-in File Explorer pin points at a shell location rather than a file.
            if (Shell.ResolveShortcut(parse) == null && Path.GetFileName(parse).Equals("File Explorer.lnk", StringComparison.OrdinalIgnoreCase))
                return new DockItem { Name = name, Location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") };

            Directory.CreateDirectory(ShortcutFolder);
            string copy = Path.Combine(ShortcutFolder, Path.GetFileName(parse));
            for (int n = 2; File.Exists(copy) && !SameFile(copy, parse); n++)
                copy = Path.Combine(ShortcutFolder, $"{Path.GetFileNameWithoutExtension(parse)} ({n}).lnk");
            File.Copy(parse, copy, overwrite: true);
            return new DockItem { Name = name, Location = copy };
        }

        // Store / packaged apps come back as an app ID like "Microsoft.ScreenSketch_8wekyb3d8bbwe!App".
        if (parse.Contains('!') && !parse.Contains('\\'))
            return new DockItem { Name = name, Location = @"shell:AppsFolder\" + parse };

        if (File.Exists(parse) || Directory.Exists(parse))
            return new DockItem { Name = name, Location = parse };

        return null;
    }

    static bool SameFile(string a, string b)
    {
        try { return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)); }
        catch { return false; }
    }

    static List<(string Name, string Parse)> FromRegistry()
    {
        var list = new List<(string, string)>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (key?.GetValue("Favorites") is not byte[] b) return list;

            int i = 0;
            while (i + 5 <= b.Length && b[i] == 0)
            {
                int len = BitConverter.ToInt32(b, i + 1);
                if (len <= 0 || i + 5 + len > b.Length) break;

                IntPtr pidl = Marshal.AllocCoTaskMem(len);
                try
                {
                    Marshal.Copy(b, i + 5, pidl, len);
                    string? parse = Name(pidl, SIGDN_DESKTOPABSOLUTEPARSING);
                    string? display = Name(pidl, SIGDN_NORMALDISPLAY);
                    if (!string.IsNullOrEmpty(parse))
                        list.Add((string.IsNullOrEmpty(display) ? Path.GetFileNameWithoutExtension(parse) : display, parse));
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
                i += 5 + len;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        return list;
    }

    static string? Name(IntPtr pidl, uint sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}
