using System.Text.Json.Serialization;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace WinDockIt;

/// <summary>One slot on the dock: a pinned app/file/folder, the separator, or the Recycle Bin.</summary>
public sealed class DockItem
{
    // ---- Persisted in config.json ----
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string? Arguments { get; set; }
    /// <summary>Optional override for running-app detection (process name without ".exe").</summary>
    public string? ProcessName { get; set; }
    /// <summary>Optional app ID (AppUserModelID) its windows carry, when the location alone doesn't say.</summary>
    public string? AppUserModelId { get; set; }

    // ---- Runtime only (fields are not serialized) ----
    [JsonIgnore] public bool IsSeparator { get; init; }
    [JsonIgnore] public bool IsTrash { get; init; }
    /// <summary>The Start button.</summary>
    [JsonIgnore] public bool IsStart { get; init; }
    /// <summary>The "+N" tile standing in for pinned apps that don't fit; opens Launchpad.</summary>
    [JsonIgnore] public bool IsMore { get; init; }
    /// <summary>A running app that isn't pinned; it leaves the dock when its last window closes.</summary>
    [JsonIgnore] public bool IsTransient { get; init; }
    [JsonIgnore] public bool IsPinned => !IsSeparator && !IsTrash && !IsStart && !IsMore && !IsTransient;
    /// <summary>Something that can have windows: pinned or running apps.</summary>
    [JsonIgnore] public bool IsApp => IsPinned || IsTransient;

    internal string? AppId, ExePath;          // identity used to match windows (see WindowList)
    internal string? GroupKey;                // transient items: which window group they stand for
    internal List<IntPtr> Windows = new();    // open windows, top-most first
    internal bool Attention;                  // a window flashed and hasn't been looked at yet
    internal int BouncesLeft;
    internal System.Windows.Controls.Border? SepLine;
    internal ImageSource? Icon;
    internal Image? Img;
    internal Ellipse? Dot;
    internal bool IsRunning;

    internal double Size;        // current rendered icon size (DIPs)
    internal double GoalSize;    // size the magnification wants
    internal double CenterM;     // centre along the dock axis, from the last frame
    internal long BounceStart;   // Stopwatch timestamp, 0 = not bouncing
}
