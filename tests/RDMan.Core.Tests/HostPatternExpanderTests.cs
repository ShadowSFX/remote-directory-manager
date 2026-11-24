using RDMan.Core.Bulk;

namespace RDMan.Core.Tests;

public sealed class HostPatternExpanderTests
{
    private static IReadOnlyList<string> Hosts(string text)
    {
        var r = HostPatternExpander.Expand(text);
        Assert.Null(r.ErrorKey);
        return r.Hosts;
    }

    [Fact]
    public void Numeric_ranges_keep_zero_padding()
    {
        Assert.Equal(["web1", "web2", "web3"], Hosts("web[1-3]"));
        Assert.Equal(["db08", "db09", "db10"], Hosts("db[08-10]"));
        Assert.Equal(["10.0.0.20", "10.0.0.21"], Hosts("10.0.0.[20-21]"));
    }

    [Fact]
    public void Alternatives_lists_letters_and_combinations()
    {
        Assert.Equal(["api.corp", "auth.corp"], Hosts("{api,auth}.corp"));
        Assert.Equal(["n1", "n3", "n7"], Hosts("n[1,3,7]"));
        Assert.Equal(["ra", "rb", "rc"], Hosts("r[a-c]"));
        Assert.Equal(["eu-web1", "eu-web2", "us-web1", "us-web2"], Hosts("{eu,us}-web[1-2]"));
    }

    [Fact]
    public void Lines_commas_and_spaces_separate_patterns_and_duplicates_go()
    {
        Assert.Equal(["a", "b", "c", "d"], Hosts("a, b\nc d\n\n a"));
        Assert.Equal(["x1", "x2"], Hosts("x[1-2],x1"));
    }

    [Theory]
    [InlineData("web[1-3", "Bulk.Error.Bracket")]
    [InlineData("web]1", "Bulk.Error.Bracket")]
    [InlineData("web[5-1]", "Bulk.Error.Reversed")]
    [InlineData("web[1-x]", "Bulk.Error.Range")]
    [InlineData("{a,}", "Bulk.Error.Empty")]
    [InlineData("bad/host", "Bulk.Error.Host")]
    public void Errors_name_the_problem_and_line(string pattern, string key)
    {
        var r = HostPatternExpander.Expand("ok\n" + pattern);
        Assert.Equal(key, r.ErrorKey);
        Assert.Equal(2, r.ErrorLine);
        Assert.Equal(["ok"], r.Hosts);
    }

    [Fact]
    public void Caps_the_total()
    {
        var r = HostPatternExpander.Expand("h[1-900]");
        Assert.Null(r.ErrorKey);
        Assert.Equal(HostPatternExpander.MaxHosts, r.Hosts.Count);
        Assert.True(r.Truncated);
        Assert.Equal("Bulk.Error.TooMany", HostPatternExpander.Expand("h[1-99999]").ErrorKey);
    }

    [Fact]
    public void Empty_input_is_empty()
    {
        var r = HostPatternExpander.Expand("  \n ");
        Assert.Empty(r.Hosts);
        Assert.Null(r.ErrorKey);
    }
}
