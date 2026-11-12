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

    // ---- Runtime only (fields are not serialized) ----
    [JsonIgnore] public bool IsSeparator { get; init; }
    [JsonIgnore] public bool IsTrash { get; init; }
    [JsonIgnore] public bool IsPinned => !IsSeparator && !IsTrash;

    internal string? MatchName;
    internal ImageSource? Icon;
    internal Image? Img;
    internal Ellipse? Dot;
    internal bool IsRunning;

    internal double Size;        // current rendered icon size (DIPs)
    internal double GoalSize;    // size the magnification wants
    internal double CenterM;     // centre along the dock axis, from the last frame
    internal long BounceStart;   // Stopwatch timestamp, 0 = not bouncing
}
