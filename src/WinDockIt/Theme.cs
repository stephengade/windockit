using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinDockIt;

internal static class Theme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsLight => Read("AppsUseLightTheme", 1) == 1;

    /// <summary>Respects Settings › Personalization › Colors › Transparency effects.</summary>
    public static bool TransparencyEnabled => Read("EnableTransparency", 1) == 1;

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
