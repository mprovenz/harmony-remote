using Avalonia.Media;

namespace HarmonyRemote.App.ViewModels;

/// <summary>Filled 24×24 glyphs for hub functions that have a conventional symbol; everything else shows its label.</summary>
public static class IconCatalog
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DirectionUp"] = "M12,7 L18,15 L6,15 Z",
        ["DirectionDown"] = "M12,17 L18,9 L6,9 Z",
        ["DirectionLeft"] = "M7,12 L15,6 L15,18 Z",
        ["DirectionRight"] = "M17,12 L9,6 L9,18 Z",
        ["Play"] = "M8,5 L19,12 L8,19 Z",
        ["Pause"] = "M7,5 H10.5 V19 H7 Z M13.5,5 H17 V19 H13.5 Z",
        ["Stop"] = "M6.5,6.5 H17.5 V17.5 H6.5 Z",
        ["Rewind"] = "M11,6 L3,12 L11,18 Z M20,6 L12,12 L20,18 Z",
        ["FastForward"] = "M4,6 L12,12 L4,18 Z M13,6 L21,12 L13,18 Z",
        ["SkipBackward"] = "M5,6 H7.5 V18 H5 Z M19,6 L9,12 L19,18 Z",
        ["SkipForward"] = "M16.5,6 H19 V18 H16.5 Z M5,6 L15,12 L5,18 Z",
        ["Record"] = "M12,6 A6,6 0 1 1 11.99,6 Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Geometry? For(string functionName)
    {
        if (!Paths.TryGetValue(functionName, out string? data))
            return null;
        if (!Cache.TryGetValue(functionName, out var geometry))
            Cache[functionName] = geometry = Geometry.Parse(data);
        return geometry;
    }
}
