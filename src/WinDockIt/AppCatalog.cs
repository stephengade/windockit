using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WinDockIt;

/// <summary>One app from the Start menu's All apps list.</summary>
internal sealed class CatalogApp
{
    public required string Name { get; init; }
    /// <summary>A shell:AppsFolder\… location the dock can launch and pin.</summary>
    public required string Location { get; init; }
    public ImageSource? Icon { get; set; }
}

/// <summary>
/// Every app in the Start menu's All apps list (desktop and Store apps), read from the shell's
/// Applications folder on a background thread, with icons.
/// </summary>
internal static class AppCatalog
{
    static List<CatalogApp> s_apps = new();
    static readonly Dictionary<string, ImageSource?> s_icons = new(StringComparer.OrdinalIgnoreCase);
    static bool s_loading;

    /// <summary>Sorted by name. Empty until the first load finishes.</summary>
    public static IReadOnlyList<CatalogApp> Apps => s_apps;

    /// <summary>Fires on the background thread whenever <see cref="Apps"/> has been replaced.</summary>
    public static event Action? Changed;

    /// <summary>Re-reads the list in the background. Icons already loaded are reused.</summary>
    public static void Refresh()
    {
        if (s_loading) return;
        s_loading = true;
        var t = new Thread(() =>
        {
            try
            {
                var apps = Enumerate();
                // Publish names straight away, then fill in icons.
                lock (s_icons) foreach (var a in apps) a.Icon = s_icons.GetValueOrDefault(a.Location);
                s_apps = apps;
                Changed?.Invoke();
                int pending = 0;
                foreach (var a in apps)
                {
                    if (a.Icon != null) continue;
                    a.Icon = Shell.GetIcon(a.Location, 96);
                    lock (s_icons) s_icons[a.Location] = a.Icon;
                    // Show icons as they arrive, a batch at a time, rather than all at the end.
                    if (++pending == 40) { pending = 0; Changed?.Invoke(); }
                }
                if (pending > 0) Changed?.Invoke();
            }
            catch (Exception ex) { App.Log(ex); }
            finally { s_loading = false; }
        }) { IsBackground = true, Name = "WinDockIt app catalog" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ───────── shell enumeration ─────────

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IEnumShellItems ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    /// <summary>Links in All apps that aren't apps: uninstallers, help files, readme and web links.</summary>
    static bool IsJunk(string name, string parse)
    {
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        string ext = Path.GetExtension(parse).ToLowerInvariant();
        return ext is ".url" or ".chm" or ".txt" or ".pdf" or ".htm" or ".html" or ".rtf" or ".md" or ".ini" or ".log";
    }

    static List<CatalogApp> Enumerate()
    {
        var result = new List<CatalogApp>();
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(Shell.AppsFolder.TrimEnd('\\'), IntPtr.Zero, ref iid, out var folder) != 0) return result;
        try
        {
            var bhid = BHID_EnumItems;
            var eiid = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref eiid, out var items);
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (items.Next(1, out var item, out uint got) == 0 && got == 1)
                {
                    try
                    {
                        string? name = Text(item, SIGDN_NORMALDISPLAY), parse = Text(item, SIGDN_PARENTRELATIVEPARSING);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parse) || IsJunk(name, parse)) continue;
                        if (!seen.Add(parse)) continue;
                        result.Add(new CatalogApp { Name = name.Trim(), Location = Shell.AppsFolder + parse });
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(folder); }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    static string? Text(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}
