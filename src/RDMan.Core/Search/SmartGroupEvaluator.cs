using RDMan.Core.Models;

namespace RDMan.Core.Search;

/// <summary>A server that matched, with the path of groups it lives in ("Cliente / Producción").</summary>
public sealed record SmartMatchHit(ServerNode Server, string GroupPath);

/// <summary>
/// Evaluates smart groups against the tree. Case- and accent-insensitive, like the palette search. Every rule value
/// and every server field is folded once per evaluation (folding is the expensive part).
/// </summary>
public static class SmartGroupEvaluator
{
    private sealed record PreparedRule(string Field, string Op, string Value);

    /// <summary>A server's searchable fields, folded once.</summary>
    private sealed class Folded(ServerNode s, string path)
    {
        public readonly string Name = ConnectionSearch.Fold(s.Name);
        public readonly string Host = ConnectionSearch.Fold(s.Host);
        public readonly string Notes = ConnectionSearch.Fold(s.Description ?? "");
        public readonly string Path = ConnectionSearch.Fold(path);
        public readonly string[] Tags = s.Tags.Select(ConnectionSearch.Fold).ToArray();
        public readonly string[] PathParts = path.Length == 0 ? [""] : path.Split(" / ").Select(ConnectionSearch.Fold).ToArray();

        public IEnumerable<string> Candidates(string field) => field switch
        {
            SmartField.Name => [Name],
            SmartField.Host => [Host],
            SmartField.Tag => Tags,
            SmartField.Group => PathParts.Prepend(Path),
            SmartField.Notes => [Notes],
            _ => Tags.Concat([Name, Host, Notes, Path]),
        };
    }

    /// <summary>Servers of <paramref name="root"/> matching <paramref name="group"/>, in tree order. No rules = no servers.</summary>
    public static IReadOnlyList<SmartMatchHit> Evaluate(GroupNode root, SmartGroup group)
    {
        var hits = new List<SmartMatchHit>();
        var rules = Prepare(group);
        if (rules.Count == 0) return hits;
        var any = group.Match == SmartMatch.Any;
        foreach (var (server, path) in Scope(root, group))
            if (IsMatch(new Folded(server, path), rules, any)) hits.Add(new SmartMatchHit(server, path));
        return hits;
    }

    /// <summary>How many servers each group matches, folding every server once for all groups (sidebar badges).</summary>
    public static IReadOnlyList<int> Count(GroupNode root, IReadOnlyList<SmartGroup> groups)
    {
        var counts = new int[groups.Count];
        if (groups.Count == 0) return counts;
        var prepared = groups.Select(Prepare).ToList();
        var scoped = groups.Select(g => g.ScopeId is { } id ? ScopeIds(root, id) : null).ToList();
        foreach (var (server, path) in Walk(root, ""))
        {
            Folded? folded = null;
            for (var i = 0; i < groups.Count; i++)
            {
                if (prepared[i].Count == 0) continue;
                if (scoped[i] is { } inScope && !inScope.Contains(server)) continue;
                folded ??= new Folded(server, path);
                if (IsMatch(folded, prepared[i], groups[i].Match == SmartMatch.Any)) counts[i]++;
            }
        }
        return counts;
    }

    public static bool Matches(ServerNode server, string groupPath, SmartRule rule)
    {
        var prepared = Prepare(new SmartGroup { Rules = { rule } });
        return prepared.Count > 0 && IsMatch(new Folded(server, groupPath), prepared, any: false);
    }

    private static List<PreparedRule> Prepare(SmartGroup group)
        => group.Rules.Select(r => new PreparedRule(r.Field, r.Op, ConnectionSearch.Fold((r.Value ?? "").Trim())))
            .Where(r => r.Value.Length > 0).ToList();

    private static bool IsMatch(Folded f, List<PreparedRule> rules, bool any)
        => any ? rules.Any(r => RuleMatches(f, r)) : rules.All(r => RuleMatches(f, r));

    private static bool RuleMatches(Folded f, PreparedRule r)
    {
        var candidates = f.Candidates(r.Field);
        return r.Op switch
        {
            SmartOp.StartsWith => candidates.Any(c => c.StartsWith(r.Value, StringComparison.Ordinal)),
            SmartOp.Is => candidates.Any(c => c == r.Value),
            SmartOp.NotContains => candidates.All(c => !c.Contains(r.Value, StringComparison.Ordinal)),
            _ => candidates.Any(c => c.Contains(r.Value, StringComparison.Ordinal)),
        };
    }

    // ───────────── Tree walking ─────────────
    private static IEnumerable<(ServerNode Server, string Path)> Walk(GroupNode g, string path)
    {
        foreach (var child in g.Children)
        {
            switch (child)
            {
                case ServerNode s:
                    yield return (s, path);
                    break;
                case GroupNode sub:
                    foreach (var x in Walk(sub, path.Length == 0 ? sub.Name : $"{path} / {sub.Name}")) yield return x;
                    break;
            }
        }
    }

    /// <summary>The servers a group searches: below its scope group when set (and found), otherwise the whole tree.</summary>
    private static IEnumerable<(ServerNode Server, string Path)> Scope(GroupNode root, SmartGroup group)
        => group.ScopeId is { } id && FindGroup(root, id, "") is { } found ? Walk(found.Group, found.Path) : Walk(root, "");

    private static HashSet<ServerNode>? ScopeIds(GroupNode root, Guid id)
        => FindGroup(root, id, "") is { } found ? Walk(found.Group, found.Path).Select(x => x.Server).ToHashSet() : null;

    /// <summary>The group with <paramref name="id"/> and its path ("A / B"), searching below <paramref name="g"/>.</summary>
    private static (GroupNode Group, string Path)? FindGroup(GroupNode g, Guid id, string path)
    {
        foreach (var sub in g.Children.OfType<GroupNode>())
        {
            var p = path.Length == 0 ? sub.Name : $"{path} / {sub.Name}";
            if (sub.Id == id) return (sub, p);
            if (FindGroup(sub, id, p) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>Repairs a smart group read from disk: unknown fields/operators fall back to defaults, limits apply.</summary>
    public static void Normalize(SmartGroup g)
    {
        g.Name = (g.Name ?? "").Trim();
        if (g.Match is not (SmartMatch.All or SmartMatch.Any)) g.Match = SmartMatch.All;
        g.Rules ??= new List<SmartRule>();
        g.Rules.RemoveAll(r => r is null);
        foreach (var r in g.Rules)
        {
            if (!SmartField.All.Contains(r.Field)) r.Field = SmartField.Any;
            if (!SmartOp.All.Contains(r.Op)) r.Op = SmartOp.Contains;
            r.Value ??= "";
        }
        if (g.Rules.Count > SmartGroup.MaxRules) g.Rules = g.Rules.Take(SmartGroup.MaxRules).ToList();
        if (g.ScopeId == Guid.Empty) g.ScopeId = null;
    }
}
