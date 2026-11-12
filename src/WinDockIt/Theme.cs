using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinDockIt;

internal static class Theme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>The user's choice in WinDockIt's settings; System follows Windows.</summary>
    public static ThemeMode Mode { get; set; } = ThemeMode.System;

    /// <summary>WinDockIt's own frosted glass switch.</summary>
    public static bool GlassOn { get; set; } = true;

    public static GlassTint Tint { get; set; } = GlassTint.Clear;

    /// <summary>0–1.</summary>
    public static double TintStrength { get; set; } = 0.35;

    public static AccentColor AccentChoice { get; set; } = AccentColor.Windows;

    /// <summary>The RGB of a glass tint; Clear has none.</summary>
    public static uint? TintRgb(GlassTint t) => t switch
    {
        GlassTint.Graphite => 0x3C4048u,
        GlassTint.Ocean => 0x1E5AAAu,
        GlassTint.Plum => 0x6E3C96u,
        GlassTint.Rose => 0xBE466Eu,
        GlassTint.Forest => 0x287850u,
        GlassTint.Sunset => 0xD26E28u,
        _ => null,
    };

    /// <summary>
    /// A glass colour with the user's tint mixed in. The colour moves toward the tint and gets a
    /// little more opaque with strength, so a tint still reads over a busy wallpaper.
    /// </summary>
    public static uint Tinted(uint argb)
    {
        if (TintRgb(Tint) is not uint t || TintStrength <= 0) return argb;
        double s = Math.Clamp(TintStrength, 0, 1);
        byte Mix(int shift) => (byte)Math.Round(((argb >> shift) & 0xFF) * (1 - s) + ((t >> shift) & 0xFF) * s);
        uint a = argb >> 24;
        if (a < 0xB4) a = (uint)Math.Round(a + (0xB4 - a) * s * 0.5);
        return (a << 24) | ((uint)Mix(16) << 16) | ((uint)Mix(8) << 8) | Mix(0);
    }

    /// <summary>The accent as opaque RGB (0xRRGGBB).</summary>
    public static uint AccentRgb(AccentColor a) => a switch
    {
        AccentColor.Blue => 0x0A84FFu,
        AccentColor.Purple => 0xBF5AF2u,
        AccentColor.Pink => 0xFF375Fu,
        AccentColor.Red => 0xFF453Au,
        AccentColor.Orange => 0xFF9F0Au,
        AccentColor.Green => 0x30D158u,
        AccentColor.Graphite => 0x8E8E93u,
        _ => WindowsAccent(),
    };

    /// <summary>The user's accent colour, with the given alpha.</summary>
    public static uint Accent(byte alpha = 0xFF) => ((uint)alpha << 24) | AccentRgb(AccentChoice);

    public static SolidColorBrush AccentBrush(byte alpha = 0xFF) => Brush(Accent(alpha));

    /// <summary>Settings › Personalization › Colors › Accent colour (stored as 0xAABBGGRR).</summary>
    static uint WindowsAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int v)
            {
                uint abgr = unchecked((uint)v);
                return ((abgr & 0xFF) << 16) | (abgr & 0xFF00) | ((abgr >> 16) & 0xFF);
            }
        }
        catch { }
        return 0x0078D4u;
    }

    public static bool IsLight => Mode switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => Read("AppsUseLightTheme", 1) == 1,
    };

    /// <summary>WinDockIt's glass switch, and Settings › Personalization › Colors › Transparency effects.</summary>
    public static bool TransparencyEnabled => GlassOn && Read("EnableTransparency", 1) == 1;

    static int Read(string name, int fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Personalize);
            return k?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    public static Color C(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static SolidColorBrush Brush(uint argb)
    {
        var b = new SolidColorBrush(C(argb));
        b.Freeze();
        return b;
    }

    public static LinearGradientBrush Vertical(uint top, uint bottom)
    {
        var b = new LinearGradientBrush(C(top), C(bottom), 90);
        b.Freeze();
        return b;
    }

    public static void ApplyMenus(bool light)
    {
        var r = Application.Current.Resources;
        r["Menu.Bg"] = Brush(light ? 0xF7F9F9FBu : 0xF5202024u);
        r["Menu.Border"] = Brush(light ? 0x1F000000u : 0x33FFFFFFu);
        r["Menu.Fg"] = Brush(light ? 0xFF1C1C1Eu : 0xFFF2F2F5u);
        r["Menu.Hover"] = Brush(light ? 0x14000000u : 0x1FFFFFFFu);
        r["Menu.Sep"] = Brush(light ? 0x1A000000u : 0x26FFFFFFu);
    }
}
