using System.Globalization;
using System.Text;
using RDMan.Core.Models;

namespace RDMan.Core.Search;

public sealed record SearchHit(ServerNode Server, string GroupPath, int Score);

/// <summary>Ranks servers for the Ctrl+K palette by name, host, share, tags, group path and description.</summary>
public static class ConnectionSearch
{
    public static IReadOnlyList<SearchHit> Search(GroupNode root, string? query, int max = 50)
    {
        var index = new TreeIndex(root);
        var q = Fold(query ?? "").Trim();
        var results = new List<SearchHit>();
        foreach (var s in TreeIndex.AllServers(root))
        {
            var groupPath = index.GroupPath(s);
            var score = q.Length == 0 ? 1 : Score(s, groupPath, q);
            if (score > 0) results.Add(new SearchHit(s, groupPath, score));
        }
        return results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Server.LastConnected ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.Server.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();
    }

    internal static int Score(ServerNode s, string groupPath, string foldedQuery)
    {
        var terms = foldedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = 0;
        foreach (var term in terms)
        {
            var best = Max(
                FieldScore(s.Name, term, 100),
                FieldScore(s.Host, term, 90),
                FieldScore(s.Share, term, 60),
                FieldScore(groupPath, term, 50),
                s.Tags.Select(t => FieldScore(t, term, 70)).DefaultIfEmpty(0).Max(),
                FieldScore(s.Description, term, 25));
            if (best == 0) return 0; // every term must match something
            total += best;
        }
        return total;
    }

    private static int Max(params int[] values) => values.Max();

    private static int FieldScore(string? field, string term, int weight)
    {
        if (string.IsNullOrEmpty(field)) return 0;
        var f = Fold(field);
        if (f == term) return weight * 3;
        if (f.StartsWith(term, StringComparison.Ordinal)) return weight * 2;
        var idx = f.IndexOf(term, StringComparison.Ordinal);
        if (idx > 0)
        {
            var prev = f[idx - 1];
            return char.IsLetterOrDigit(prev) ? weight : (int)(weight * 1.5); // word boundary bonus
        }
        return IsSubsequence(term, f) && term.Length >= 2 ? weight / 3 : 0;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var i = 0;
        foreach (var c in haystack)
            if (i < needle.Length && c == needle[i]) i++;
        return i == needle.Length;
    }

    /// <summary>Lower-case and strip diacritics so "producción" matches "produccion".</summary>
    public static string Fold(string s)
    {
        var normalized = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
