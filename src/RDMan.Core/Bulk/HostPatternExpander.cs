using System.Globalization;
using System.Text;

namespace RDMan.Core.Bulk;

/// <summary>Result of expanding host patterns: the hosts, or the first problem found (with the line it's on).</summary>
public sealed record ExpandResult(IReadOnlyList<string> Hosts, string? ErrorKey, int ErrorLine, bool Truncated);

/// <summary>
/// Expands "bulk add" patterns, one per line (commas also separate):
/// <c>web[1-5]</c> → web1…web5, <c>db[01-10]</c> keeps zero padding, <c>10.0.0.[20-24]</c>, <c>{api,auth,cdn}.corp.local</c>,
/// and combinations (cartesian). Duplicates are removed, order is kept, the total is capped.
/// </summary>
public static class HostPatternExpander
{
    public const int MaxHosts = 500;
    private const int MaxRangeSpan = 10000;

    public static ExpandResult Expand(string? text)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        var lines = (text ?? "").Replace("\r", "").Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            foreach (var raw in SplitTopLevel(lines[li]))
            {
                var pattern = raw.Trim();
                if (pattern.Length == 0) continue;
                if (!TryExpand(pattern, out var expanded, out var error))
                    return new ExpandResult(hosts, error, li + 1, false);
                foreach (var h in expanded)
                {
                    if (!IsPlausibleHost(h)) return new ExpandResult(hosts, "Bulk.Error.Host", li + 1, false);
                    if (!seen.Add(h)) continue;
                    if (hosts.Count >= MaxHosts)
                    {
                        truncated = true;
                        break;
                    }
                    hosts.Add(h);
                }
            }
        }
        return new ExpandResult(hosts, null, 0, truncated);
    }

    /// <summary>Commas separate patterns, except inside {…} and […] where they separate alternatives.</summary>
    private static IEnumerable<string> SplitTopLevel(string line)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is '{' or '[') depth++;
            else if (line[i] is '}' or ']') depth = Math.Max(0, depth - 1);
            else if ((line[i] == ',' || line[i] == ';' || line[i] == ' ' || line[i] == '\t') && depth == 0)
            {
                yield return line[start..i];
                start = i + 1;
            }
        }
        yield return line[start..];
    }

    private static bool TryExpand(string pattern, out List<string> result, out string? error)
    {
        result = [""];
        error = null;
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0) { error = "Bulk.Error.Bracket"; return false; }
                if (!TryRange(pattern[(i + 1)..end], out var items, out error)) return false;
                result = Combine(result, items);
                i = end + 1;
            }
            else if (c == '{')
            {
                var end = pattern.IndexOf('}', i + 1);
                if (end < 0) { error = "Bulk.Error.Bracket"; return false; }
                var items = pattern[(i + 1)..end].Split(',').Select(s => s.Trim()).ToList();
                if (items.Any(s => s.Length == 0)) { error = "Bulk.Error.Empty"; return false; }
                result = Combine(result, items);
                i = end + 1;
            }
            else if (c is ']' or '}')
            {
                error = "Bulk.Error.Bracket";
                return false;
            }
            else
            {
                var sb = new StringBuilder();
                while (i < pattern.Length && pattern[i] is not ('[' or '{' or ']' or '}')) sb.Append(pattern[i++]);
                result = result.Select(r => r + sb).ToList();
                continue;
            }
            if (result.Count > MaxHosts * 4) { error = "Bulk.Error.TooMany"; return false; }
        }
        return true;
    }

    /// <summary>"1-5", "01-10" (keeps the width), "a-e" (letters), or a comma list "1,3,7".</summary>
    private static bool TryRange(string body, out List<string> items, out string? error)
    {
        items = [];
        error = null;
        foreach (var part in body.Split(','))
        {
            var p = part.Trim();
            var dash = p.IndexOf('-');
            if (dash <= 0)
            {
                if (p.Length == 0) { error = "Bulk.Error.Empty"; return false; }
                items.Add(p);
                continue;
            }
            var a = p[..dash].Trim();
            var b = p[(dash + 1)..].Trim();
            if (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var to))
            {
                if (to < from) { error = "Bulk.Error.Reversed"; return false; }
                if (to - from > MaxRangeSpan) { error = "Bulk.Error.TooMany"; return false; }
                var width = a.Length > 1 && a[0] == '0' ? a.Length : 0;
                for (var n = from; n <= to; n++) items.Add(width > 0 ? n.ToString(new string('0', width), CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture));
            }
            else if (a.Length == 1 && b.Length == 1 && char.IsLetter(a[0]) && char.IsLetter(b[0]))
            {
                if (b[0] < a[0]) { error = "Bulk.Error.Reversed"; return false; }
                for (var ch = a[0]; ch <= b[0]; ch++) items.Add(ch.ToString());
            }
            else
            {
                error = "Bulk.Error.Range";
                return false;
            }
        }
        return true;
    }

    private static List<string> Combine(List<string> prefixes, List<string> items)
    {
        var list = new List<string>(prefixes.Count * items.Count);
        foreach (var p in prefixes)
            foreach (var it in items) list.Add(p + it);
        return list;
    }

    /// <summary>Letters, digits, dots, dashes, underscores, colons (IPv6) — what can be a host name or address.</summary>
    private static bool IsPlausibleHost(string h)
        => h.Length is > 0 and <= 253 && h.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':');
}
