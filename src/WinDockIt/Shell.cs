using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    public static ImageSource LetterIcon(string name)
    {
        string letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
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
        return rtb;
    }

    public static string DisplayName(string path)
    {
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

    public static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic sh = Activator.CreateInstance(type)!;
            dynamic sc = sh.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }

    /// <summary>Process name used to decide whether a pinned item is running.</summary>
    public static string? ResolveProcessName(DockItem it)
    {
        if (!string.IsNullOrWhiteSpace(it.ProcessName)) return it.ProcessName;
        var target = it.Location;
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) target = ResolveShortcut(target) ?? target;
        return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(target) : null;
    }

    public static long RecycleBinCount()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : 0;
    }
}
