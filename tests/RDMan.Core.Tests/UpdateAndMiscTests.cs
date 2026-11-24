using System.Globalization;
using System.Text.RegularExpressions;
using RDMan.Core.Localization;
using RDMan.Core.Models;
using RDMan.Core.Search;
using RDMan.Core.Updates;

namespace RDMan.Core.Tests;

public class UpdateAndMiscTests
{
    private static SemVersion V(string s) => SemVersion.TryParse(s, out var v) ? v! : throw new ArgumentException(s);

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.9", "1.10.0")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("v2.0.0", "v10.0.0")]
    public void SemVer_ordering(string lower, string higher)
    {
        Assert.True(V(higher) > V(lower));
        Assert.True(V(lower) < V(higher));
    }

    [Theory]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("1.2.3+sha.abc", "1.2.3")]
    public void SemVer_parsing_variants(string input, string expected) => Assert.Equal(expected, V(input).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3-")]
    [InlineData("-1.0.0")]
    public void SemVer_rejects_garbage(string input) => Assert.False(SemVersion.TryParse(input, out _));

    private const string ReleaseJson = """
    {
      "tag_name": "v1.2.0", "name": "RDMan 1.2.0", "body": "* nuevo", "html_url": "https://github.com/x/y/releases/tag/v1.2.0",
      "draft": false, "prerelease": false,
      "assets": [
        { "name": "RDMan.exe", "browser_download_url": "https://example/RDMan.exe", "size": 100 },
        { "name": "RDMan.exe.sha256", "browser_download_url": "https://example/RDMan.exe.sha256", "size": 64 }
      ]
    }
    """;

    [Fact]
    public void Plans_update_when_newer_with_both_assets()
    {
        var release = UpdatePlanner.ParseRelease(ReleaseJson);
        Assert.NotNull(release);
        var plan = UpdatePlanner.Plan(V("1.1.0"), release);
        Assert.NotNull(plan);
        Assert.Equal("RDMan.exe", plan!.Executable.Name);
    }

    [Fact]
    public void No_plan_for_same_older_dev_or_missing_assets()
    {
        var release = UpdatePlanner.ParseRelease(ReleaseJson)!;
        Assert.Null(UpdatePlanner.Plan(V("1.2.0"), release));
        Assert.Null(UpdatePlanner.Plan(V("2.0.0"), release));
        Assert.Null(UpdatePlanner.Plan(V("0.0.0-dev"), release));
        Assert.Null(UpdatePlanner.Plan(V("1.0.0"), release with { Assets = release.Assets.Take(1).ToList() }));
        Assert.Null(UpdatePlanner.Plan(V("1.0.0"), release with { Prerelease = true }));
        Assert.Null(UpdatePlanner.Plan(V("1.0.0"), null));
    }

    [Fact]
    public void ParseRelease_rejects_bad_json_or_tag()
    {
        Assert.Null(UpdatePlanner.ParseRelease("not json"));
        Assert.Null(UpdatePlanner.ParseRelease("""{ "tag_name": "latest" }"""));
        Assert.Null(UpdatePlanner.ParseRelease("[]"));
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  RDMan.exe\n", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("tooshort", null)]
    [InlineData("", null)]
    public void Parses_checksum_files(string content, string? expected) => Assert.Equal(expected, UpdatePlanner.ParseChecksum(content));

    [Fact]
    public async Task Computes_sha256()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await UpdatePlanner.ComputeSha256Async(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Every_string_has_both_languages_and_matching_placeholders()
    {
        var placeholder = new Regex(@"\{\d+\}");
        foreach (var key in Strings.Keys)
        {
            var es = Strings.Get(key, "es");
            var en = Strings.Get(key, "en");
            Assert.False(string.IsNullOrWhiteSpace(es), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            var pes = placeholder.Matches(es).Select(m => m.Value).Distinct().OrderBy(x => x);
            var pen = placeholder.Matches(en).Select(m => m.Value).Distinct().OrderBy(x => x);
            Assert.True(pes.SequenceEqual(pen), $"Placeholder mismatch in {key}");
        }
    }

    [Fact]
    public void Language_resolution()
    {
        Assert.Equal("es", Strings.ResolveLanguage("system", new CultureInfo("es-MX")));
        Assert.Equal("en", Strings.ResolveLanguage("system", new CultureInfo("de-DE")));
        Assert.Equal("en", Strings.ResolveLanguage("en", new CultureInfo("es-ES")));
        // English display language but Spanish regional format → Spanish.
        Assert.Equal("es", Strings.ResolveLanguage("system", new CultureInfo("en-US"), new CultureInfo("es-MX")));
        Assert.Equal("en", Strings.ResolveLanguage("system", new CultureInfo("en-US"), new CultureInfo("en-GB")));
        Assert.Equal("Unknown.Key", Strings.Get("Unknown.Key", "es"));
    }

    [Fact]
    public void Search_ranks_name_then_host_and_folds_accents()
    {
        var root = new GroupNode();
        var prod = new GroupNode { Name = "Producción", Children =
        {
            new ServerNode { Name = "Web principal", Host = "10.0.0.5", Tags = { "nginx" } },
            new ServerNode { Name = "Backups", Host = "10.0.0.50" },
        } };
        root.Children.Add(prod);
        root.Children.Add(new ServerNode { Name = "Laboratorio", Host = "192.168.1.20" });

        Assert.Equal("Web principal", ConnectionSearch.Search(root, "web").First().Server.Name);
        Assert.Equal(2, ConnectionSearch.Search(root, "produccion").Count);
        Assert.Equal("Web principal", ConnectionSearch.Search(root, "10.0.0.5").First().Server.Name);
        Assert.Single(ConnectionSearch.Search(root, "nginx"));
        Assert.Empty(ConnectionSearch.Search(root, "zzz"));
        Assert.Equal(3, ConnectionSearch.Search(root, "").Count);
        Assert.Single(ConnectionSearch.Search(root, "prod back"));
    }

    [Fact]
    public void TreeIndex_paths_and_descendants()
    {
        var server = new ServerNode { Name = "s" };
        var inner = new GroupNode { Name = "B", Children = { server } };
        var outer = new GroupNode { Name = "A", Children = { inner } };
        var root = new GroupNode { Children = { outer } };
        var idx = new TreeIndex(root);
        Assert.Equal("A / B", idx.GroupPath(server));
        Assert.Same(inner, idx.ParentOf(server.Id));
        Assert.True(TreeIndex.IsDescendantOf(outer, server));
        Assert.False(TreeIndex.IsDescendantOf(inner, outer));
    }

    [Fact]
    public void Tag_colors_lookup()
    {
        Assert.Equal("#22A699", TagColors.HexFor("TEAL"));
        Assert.Null(TagColors.HexFor("nope"));
        Assert.Null(TagColors.HexFor(null));
    }
}
