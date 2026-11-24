using RDMan.Core.Models;

namespace RDMan.Core.Search;

/// <summary>A server that matched, with the path of groups it lives in ("Cliente / Producción").</summary>
public sealed record SmartMatchHit(ServerNode Server, string GroupPath);

/// <summary>Evaluates smart groups against the tree. Case- and accent-insensitive, like the palette search.</summary>
public static class SmartGroupEvaluator
{
    /// <summary>Servers of <paramref name="root"/> matching <paramref name="group"/>, in tree order. No rules = no servers.</summary>
    public static IReadOnlyList<SmartMatchHit> Evaluate(GroupNode root, SmartGroup group)
    {
        var hits = new List<SmartMatchHit>();
        var rules = group.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (rules.Count == 0) return hits;
        var any = group.Match == SmartMatch.Any;
        Walk(root, "", isRoot: true);
        return hits;

        void Walk(GroupNode g, string path, bool isRoot)
        {
            foreach (var child in g.Children)
            {
                switch (child)
                {
                    case ServerNode s:
                        var matched = any ? rules.Any(r => Matches(s, path, r)) : rules.All(r => Matches(s, path, r));
                        if (matched) hits.Add(new SmartMatchHit(s, path));
                        break;
                    case GroupNode sub:
                        Walk(sub, isRoot && path.Length == 0 ? sub.Name : $"{path} / {sub.Name}", false);
                        break;
                }
            }
        }
    }

    public static bool Matches(ServerNode server, string groupPath, SmartRule rule)
    {
        var value = ConnectionSearch.Fold(rule.Value.Trim());
        if (value.Length == 0) return false;
        var candidates = rule.Field switch
        {
            SmartField.Name => [server.Name],
            SmartField.Host => [server.Host],
            SmartField.Tag => server.Tags,
            SmartField.Group => GroupCandidates(groupPath),
            SmartField.Notes => [server.Description ?? ""],
            _ => new[] { server.Name, server.Host, server.Description ?? "", groupPath }.Concat(server.Tags).ToList(),
        };
        var folded = candidates.Select(c => ConnectionSearch.Fold(c ?? "")).ToList();
        return rule.Op switch
        {
            SmartOp.StartsWith => folded.Any(c => c.StartsWith(value, StringComparison.Ordinal)),
            SmartOp.Is => folded.Any(c => c == value),
            SmartOp.NotContains => folded.All(c => !c.Contains(value, StringComparison.Ordinal)),
            _ => folded.Any(c => c.Contains(value, StringComparison.Ordinal)),
        };
    }

    /// <summary>"Cliente / Producción" also answers to "Cliente" and "Producción" for equals/starts-with.</summary>
    private static IReadOnlyList<string> GroupCandidates(string path)
        => path.Length == 0 ? [""] : new[] { path }.Concat(path.Split(" / ")).ToList();

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
    }
}
