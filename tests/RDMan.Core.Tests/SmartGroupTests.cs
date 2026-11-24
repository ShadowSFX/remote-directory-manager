using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Search;

namespace RDMan.Core.Tests;

public sealed class SmartGroupTests
{
    private static GroupNode Tree()
    {
        var root = new GroupNode { Name = "root" };
        var client = new GroupNode { Name = "Cliente Aurora" };
        var prod = new GroupNode { Name = "Producción" };
        prod.Children.Add(new ServerNode { Name = "Web principal", Host = "10.0.0.5", Tags = { "nginx", "prod" } });
        prod.Children.Add(new ServerNode { Name = "Base de datos", Host = "10.0.0.6", Tags = { "postgres", "prod" }, Description = "Réplica nocturna" });
        client.Children.Add(prod);
        client.Children.Add(new ServerNode { Name = "Staging API", Host = "stg.local", Tags = { "staging" } });
        root.Children.Add(client);
        root.Children.Add(new ServerNode { Name = "NAS casa", Host = "192.168.77.50" });
        return root;
    }

    private static string[] Names(GroupNode root, SmartGroup g) => SmartGroupEvaluator.Evaluate(root, g).Select(h => h.Server.Name).ToArray();

    [Fact]
    public void Tag_rule_finds_servers_anywhere_in_the_tree()
    {
        var g = new SmartGroup { Rules = { new SmartRule { Field = SmartField.Tag, Op = SmartOp.Is, Value = "PROD" } } };
        Assert.Equal(["Web principal", "Base de datos"], Names(Tree(), g));
    }

    [Fact]
    public void All_and_any_combine_rules()
    {
        var rules = new List<SmartRule>
        {
            new() { Field = SmartField.Host, Op = SmartOp.StartsWith, Value = "10.0." },
            new() { Field = SmartField.Name, Op = SmartOp.Contains, Value = "api" },
        };
        Assert.Empty(Names(Tree(), new SmartGroup { Match = SmartMatch.All, Rules = rules }));
        Assert.Equal(["Web principal", "Base de datos", "Staging API"], Names(Tree(), new SmartGroup { Match = SmartMatch.Any, Rules = rules }));
    }

    [Fact]
    public void Matching_ignores_case_and_accents_and_reports_group_path()
    {
        var g = new SmartGroup { Rules = { new SmartRule { Field = SmartField.Notes, Value = "replica" } } };
        var hit = Assert.Single(SmartGroupEvaluator.Evaluate(Tree(), g));
        Assert.Equal("Base de datos", hit.Server.Name);
        Assert.Equal("Cliente Aurora / Producción", hit.GroupPath);
    }

    [Fact]
    public void Group_rule_matches_any_level_of_the_path()
    {
        var g = new SmartGroup { Rules = { new SmartRule { Field = SmartField.Group, Op = SmartOp.Is, Value = "produccion" } } };
        Assert.Equal(["Web principal", "Base de datos"], Names(Tree(), g));
    }

    [Fact]
    public void Not_contains_excludes_and_any_field_searches_everything()
    {
        var g = new SmartGroup { Rules = { new SmartRule { Field = SmartField.Any, Op = SmartOp.NotContains, Value = "prod" } } };
        Assert.Equal(["Staging API", "NAS casa"], Names(Tree(), g));
    }

    [Fact]
    public void No_rules_or_blank_values_match_nothing()
    {
        Assert.Empty(Names(Tree(), new SmartGroup()));
        Assert.Empty(Names(Tree(), new SmartGroup { Rules = { new SmartRule { Value = "  " } } }));
    }

    [Fact]
    public void Normalize_repairs_unknown_values_and_limits_rules()
    {
        var g = new SmartGroup { Name = "  x ", Match = "weird", Rules = { new SmartRule { Field = "zzz", Op = "??", Value = null! }, null! } };
        for (var i = 0; i < 30; i++) g.Rules.Add(new SmartRule { Value = "a" });
        SmartGroupEvaluator.Normalize(g);
        Assert.Equal("x", g.Name);
        Assert.Equal(SmartMatch.All, g.Match);
        Assert.Equal(SmartField.Any, g.Rules[0].Field);
        Assert.Equal(SmartOp.Contains, g.Rules[0].Op);
        Assert.Equal("", g.Rules[0].Value);
        Assert.Equal(SmartGroup.MaxRules, g.Rules.Count);
    }

    [Fact]
    public void Store_normalizes_and_reassigns_smart_groups()
    {
        var doc = new ConnectionDocument();
        var id = Guid.NewGuid();
        doc.SmartGroups = [new SmartGroup { Id = id, Name = "a" }, new SmartGroup { Id = id, Name = "b" }, null!];
        ConnectionStore.Normalize(doc);
        Assert.Equal(2, doc.SmartGroups.Count);
        Assert.NotEqual(doc.SmartGroups[0].Id, doc.SmartGroups[1].Id);
        var before = doc.SmartGroups[0].Id;
        ConnectionStore.ReassignIds(doc);
        Assert.NotEqual(before, doc.SmartGroups[0].Id);
    }

    [Fact]
    public void Documents_without_smart_groups_or_favorites_still_load()
    {
        var json = """{"schemaVersion":2,"root":{"kind":"group","name":"root","children":[{"kind":"server","name":"a","host":"h"}]},"profiles":[]}""";
        var doc = System.Text.Json.JsonSerializer.Deserialize<ConnectionDocument>(json, JsonFileStore.Options)!;
        ConnectionStore.Normalize(doc);
        Assert.Empty(doc.SmartGroups);
        Assert.False(((ServerNode)doc.Root.Children[0]).IsFavorite);
    }
}
