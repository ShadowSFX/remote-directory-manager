namespace RDMan.Core.Models;

/// <summary>
/// A saved search (RDCMan's "smart group"): every server matching its rules, wherever it lives in the tree.
/// Stored next to the tree, not inside it, so older RDMan builds simply ignore it. Field/operator values are plain
/// strings on purpose: an unknown value degrades to a default instead of failing the whole connections file.
/// </summary>
public sealed class SmartGroup
{
    public const int MaxRules = 20;

    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>"all" (every rule must match) or "any".</summary>
    public string Match { get; set; } = SmartMatch.All;
    public List<SmartRule> Rules { get; set; } = new();
    /// <summary>Only servers inside this group (any depth); null = the whole tree. A missing group also means the whole tree.</summary>
    public Guid? ScopeId { get; set; }

    public SmartGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        Match = Match,
        ScopeId = ScopeId,
        Rules = Rules.Select(r => r.Clone()).ToList(),
    };
}

public sealed class SmartRule
{
    public string Field { get; set; } = SmartField.Any;
    public string Op { get; set; } = SmartOp.Contains;
    public string Value { get; set; } = "";

    public SmartRule Clone() => (SmartRule)MemberwiseClone();
}

public static class SmartMatch
{
    public const string All = "all";
    public const string Any = "any";
}

public static class SmartField
{
    /// <summary>Name, host, tags, group path or notes.</summary>
    public const string Any = "any";
    public const string Name = "name";
    public const string Host = "host";
    public const string Tag = "tag";
    public const string Group = "group";
    public const string Notes = "notes";

    public static readonly string[] All = [Any, Name, Host, Tag, Group, Notes];
}

public static class SmartOp
{
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string Is = "is";
    public const string NotContains = "notContains";

    public static readonly string[] All = [Contains, StartsWith, Is, NotContains];
}
