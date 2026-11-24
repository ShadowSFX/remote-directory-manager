namespace RDMan.Core.Models;

/// <summary>
/// Identity colors, modelled after patch-cable tags. Keys are persisted; hex values may be tuned per theme by the UI.
/// </summary>
public static class TagColors
{
    public static readonly IReadOnlyList<(string Key, string Hex)> All = new[]
    {
        ("blue", "#4C8DF6"),
        ("teal", "#22A699"),
        ("green", "#4CAF50"),
        ("amber", "#E0A526"),
        ("orange", "#E8743B"),
        ("red", "#E5484D"),
        ("pink", "#D6409F"),
        ("violet", "#8E6CEF"),
        ("slate", "#7A8699"),
    };

    public static string? HexFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var (k, hex) in All)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return hex;
        return null;
    }
}
