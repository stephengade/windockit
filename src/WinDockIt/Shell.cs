using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDockIt;

/// <summary>Windows shell helpers: high-resolution icons, shortcut targets, Recycle Bin.</summary>
internal static class Shell
{
    public const string RecycleBinPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint c0, c1, c2, c3; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    const int SIIGBF_ICONONLY = 0x4;

    /// <summary>Gets the shell's icon for any path (exe, lnk, folder, shell namespace) at up to 256px.</summary>
    public static ImageSource? GetIcon(string path, int size = 256)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            foreach (var flags in new[] { SIIGBF_ICONONLY, 0 })
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbm) != 0 || hbm == IntPtr.Zero) continue;
                try
                {
                    var img = FromHBitmap(hbm);
                    if (img != null) return img;
                }
                finally { DeleteObject(hbm); }
            }
        }
        catch { }
        finally
        {
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
        return null;
    }

    // CreateBitmapSourceFromHBitmap drops alpha for these DIBs, so copy the pixels ourselves.
    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            },
        };
        var px = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, px, ref bi, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A tidy rounded tile with the item's initial, for things with no usable icon.</summary>
    static readonly Dictionary<string, ImageSource> s_letters = new();

    public static ImageSource LetterIcon(string name)
    {
        string letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
        lock (s_letters)
            if (s_letters.TryGetValue(letter, out var cached)) return cached;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x6C, 0xB4, 0xFF), Color.FromRgb(0x2F, 0x6B, 0xF0), 90);
            dc.DrawRoundedRectangle(bg, null, new Rect(16, 16, 224, 224), 52, 52);
            var ft = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                120, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(128 - ft.Width / 2, 128 - ft.Height / 2));
        }
        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        lock (s_letters) s_letters[letter] = rtb;
        return rtb;
    }

    /// <summary>The dock's overflow tile: a frosted square with "+N".</summary>
    public static ImageSource MoreIcon(int count, bool light)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new SolidColorBrush(light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            var edge = new Pen(new SolidColorBrush(light ? Color.FromArgb(0x1F, 0, 0, 0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), 4);
            dc.DrawRoundedRectangle(bg, edge, new Rect(18, 18, 220, 220), 52, 52);
            var ft = new FormattedText("+" + count, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                count > 99 ? 72 : 92, light ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)) : Brushes.White, 1.0);
            dc.DrawText(ft, new Point(128 - ft.Width / 2, 128 - ft.Height / 2));
        }
        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static BitmapImage? s_appIcon;

    /// <summary>The WinDockIt icon. Used for the app itself and for the dock's and status bar's Start buttons.</summary>
    public static ImageSource AppIcon()
    {
        if (s_appIcon != null) return s_appIcon;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri("pack://application:,,,/Assets/WinDockIt.png");
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return s_appIcon = bi;
    }

    public static string DisplayName(string path)
    {
        if (path.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) && ShellName(path) is { Length: > 0 } shellName)
            return shellName;
        try
        {
            if (Directory.Exists(path))
            {
                var n = new DirectoryInfo(path).Name;
                return string.IsNullOrEmpty(n) ? path : n;
            }
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public static string? ResolveShortcut(string lnk) => ReadShortcut(lnk)?.Target;

    static (string Target, string Args)? ReadShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic sh = Activator.CreateInstance(type)!;
            dynamic sc = sh.CreateShortcut(lnk);
            string target = sc.TargetPath, args = sc.Arguments;
            return string.IsNullOrEmpty(target) ? null : (target, args ?? "");
        }
        catch { return null; }
    }

    /// <summary>
    /// What an item actually opens: a shortcut's target and arguments, otherwise its location.
    /// Used to avoid pinning the same app twice.
    /// </summary>
    public static string IdentityKey(string location)
    {
        if (location.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ReadShortcut(location) is { } s)
        {
            // Windows tags taskbar shortcuts with where they came from; that doesn't change what they open.
            string args = System.Text.RegularExpressions.Regex.Replace(s.Args, @"--source-shortcut-location=\S*", "").Trim();
            return $"{s.Target}|{args}".ToLowerInvariant();
        }
        return $"{location}|".ToLowerInvariant();
    }

    // ───── App identity (AppUserModelID), the key Windows uses to group taskbar buttons ─────

    public const string AppsFolder = @"shell:AppsFolder\";

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, out IPropertyStore ps);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid, out IPropertyStore ps);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

    // System.AppUserModel.* property IDs.
    const uint PID_RelaunchCommand = 2, PID_RelaunchIcon = 3, PID_RelaunchName = 4, PID_AppId = 5;

    static string? ReadAppId(IPropertyStore ps, uint pid = PID_AppId)
    {
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = pid };
            if (ps.GetValue(ref key, out var v) != 0) return null;
            try { return v.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(v.p) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(ps); }
    }

    /// <summary>The app ID a window set for itself, if any.</summary>
    public static string? WindowAppId(IntPtr h) => WindowProperty(h, PID_AppId);

    static string? WindowProperty(IntPtr h, uint pid)
    {
        var iid = typeof(IPropertyStore).GUID;
        try { return SHGetPropertyStoreForWindow(h, ref iid, out var ps) == 0 ? ReadAppId(ps, pid) : null; }
        catch { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static string? Indirect(string? s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '@') return s;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    /// <summary>Loads an icon resource written as "path,index" (index may be negative, meaning a resource ID).</summary>
    static ImageSource? IconResource(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        spec = Environment.ExpandEnvironmentVariables(spec.Trim().Trim('"'));
        int comma = spec.LastIndexOf(',');
        int index = 0;
        if (comma > 0 && int.TryParse(spec[(comma + 1)..], out int i)) { index = i; spec = spec[..comma].Trim().Trim('"'); }
        if (!File.Exists(spec)) return null;
        if (SHDefExtractIcon(spec, index, 0, out IntPtr large, out IntPtr small, 256) != 0 || large == IntPtr.Zero) return null;
        try
        {
            var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img.Freeze();
            return img;
        }
        catch { return null; }
        finally
        {
            DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    /// <summary>Splits a command line into the program and its arguments.</summary>
    static (string File, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    static string? ShortcutAppId(string lnk)
    {
        var iid = typeof(IPropertyStore).GUID;
        try { return SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 0, ref iid, out var ps) == 0 ? ReadAppId(ps) : null; }
        catch { return null; }
    }

    /// <summary>What a pinned item's windows look like: its app ID and/or the exe it runs.</summary>
    public static (string? Aumid, string? Exe) PinIdentity(string location)
    {
        if (location.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
            return (location[AppsFolder.Length..], null);
        if (location.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return (ShortcutAppId(location), ResolveShortcut(location));
        if (location.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (null, location);
        return (null, null);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    /// <summary>The name Explorer shows for a path (works for shell:AppsFolder entries), or null if it doesn't exist.</summary>
    public static string? ShellName(string path)
    {
        var iid = typeof(IShellItem).GUID;
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0) return null;
            try
            {
                if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out IntPtr p) != 0) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    /// <summary>
    /// How to show and relaunch a running app that isn't pinned: through its Start menu entry
    /// when it has one (right name, right icon, launches Store apps), otherwise its exe.
    /// </summary>
    public static (string Name, string Location, string? Args, ImageSource? Icon) DescribeRunning(AppWindow w)
    {
        if (w.Aumid != null && ShellName(AppsFolder + w.Aumid) is { Length: > 0 } n)
            return (n, AppsFolder + w.Aumid, null, null);

        // Apps launched from a shortcut (Git Bash, for one) tell the taskbar how to show and relaunch
        // them through properties on their window.
        string? name = Indirect(WindowProperty(w.Handle, PID_RelaunchName));
        var icon = IconResource(WindowProperty(w.Handle, PID_RelaunchIcon));
        string? cmd = WindowProperty(w.Handle, PID_RelaunchCommand);
        string location = w.ExePath ?? w.ProcessName;
        string? args = null;
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            var (file, a) = SplitCommand(Environment.ExpandEnvironmentVariables(cmd));
            if (File.Exists(file)) { location = file; args = a.Length > 0 ? a : null; }
        }
        if (string.IsNullOrWhiteSpace(name)) name = w.ExePath != null ? DisplayName(w.ExePath) : w.ProcessName;
        return (name, location, args, icon);
    }

    public static long RecycleBinCount()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : 0;
    }
}
