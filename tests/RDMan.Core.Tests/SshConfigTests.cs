using RDMan.Core.Ssh;

namespace RDMan.Core.Tests;

public class SshConfigTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "rdman-sshcfg-" + Guid.NewGuid().ToString("N"));

    public SshConfigTests() => Directory.CreateDirectory(Path.Combine(_home, ".ssh"));

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    private const string OwnerConfig = """
        # PangYa Relic VPS
        Host pangya-vps 45.79.2.143
            HostName 45.79.2.143
            User ubuntu
            IdentityFile ~/.ssh/pangya_vps
            IdentitiesOnly yes

        Host apex-vps 45.33.28.61
            HostName 45.33.28.61
            User ubuntu
            IdentityFile ~/.ssh/apex_vps
            IdentitiesOnly yes
        """;

    [Fact]
    public void Resolves_an_alias_like_ssh_does()
    {
        var cfg = SshConfig.Parse(OwnerConfig, _home);
        var r = cfg.Resolve("pangya-vps", "relic");
        Assert.Equal("45.79.2.143", r.EffectiveHost);
        Assert.Equal("ubuntu", r.User);
        Assert.Null(r.Port);
        Assert.True(r.IdentitiesOnly);
        Assert.Equal(Path.Combine(_home, ".ssh", "pangya_vps"), Assert.Single(r.IdentityFiles));
        Assert.True(r.MatchedSpecificHost);
    }

    [Fact]
    public void Unknown_host_keeps_the_alias_and_has_no_settings()
    {
        var r = SshConfig.Parse(OwnerConfig, _home).Resolve("other.example.com");
        Assert.Equal("other.example.com", r.EffectiveHost);
        Assert.Null(r.User);
        Assert.Empty(r.IdentityFiles);
        Assert.False(r.MatchedSpecificHost);
    }

    [Fact]
    public void Declared_hosts_skip_wildcards_and_duplicates()
    {
        var cfg = SshConfig.Parse(OwnerConfig + "\nHost *.internal !bastion.internal\n  User ops\nHost pangya-vps\n  Port 2222\n", _home);
        Assert.Equal(["pangya-vps", "45.79.2.143", "apex-vps", "45.33.28.61"], cfg.DeclaredHosts);
    }

    [Fact]
    public void First_obtained_value_wins_and_identity_files_accumulate()
    {
        var cfg = SshConfig.Parse("""
            Host web
              User first
              IdentityFile ~/.ssh/a
            Host *
              User second
              Port 2200
              IdentityFile ~/.ssh/b
            """, _home);
        var r = cfg.Resolve("web");
        Assert.Equal("first", r.User);
        Assert.Equal(2200, r.Port);
        Assert.Equal(2, r.IdentityFiles.Count);
        Assert.EndsWith("a", r.IdentityFiles[0]);
        Assert.EndsWith("b", r.IdentityFiles[1]);
    }

    [Fact]
    public void Global_lines_before_any_host_apply_everywhere()
    {
        var r = SshConfig.Parse("ServerAliveInterval 45\nHost x\n  User u\n", _home).Resolve("anything");
        Assert.Equal(45, r.ServerAliveInterval);
    }

    [Theory]
    [InlineData("*.prod", "web.prod", true)]
    [InlineData("*.prod", "web.dev", false)]
    [InlineData("web?", "web1", true)]
    [InlineData("WEB1", "web1", true)]
    [InlineData("*.prod,!db.prod", "db.prod", false)]
    [InlineData("!db.prod", "web.prod", false)] // negation alone never matches
    public void Host_patterns_follow_openssh_rules(string patterns, string host, bool expected)
        => Assert.Equal(expected, SshConfig.HostMatches(patterns.Split(','), host));

    [Fact]
    public void Equals_syntax_quotes_and_comments_are_understood()
    {
        var cfg = SshConfig.Parse("""
            Host=box
              HostName = 10.1.1.1   # trailing comment
              IdentityFile "~/.ssh/my key"
            """, _home);
        var r = cfg.Resolve("box");
        Assert.Equal("10.1.1.1", r.HostName);
        Assert.Equal(Path.Combine(_home, ".ssh", "my key"), Assert.Single(r.IdentityFiles));
    }

    [Fact]
    public void Tokens_are_expanded()
    {
        var cfg = SshConfig.Parse("""
            Host db
              HostName %h.corp.local
              User admin
              Port 2022
              IdentityFile ~/.ssh/%r@%h-%p
            """, _home);
        var r = cfg.Resolve("db", "relic");
        Assert.Equal("db.corp.local", r.HostName);
        Assert.Equal(Path.Combine(_home, ".ssh", "admin@db.corp.local-2022"), r.IdentityFiles[0]);
    }

    [Fact]
    public void Match_blocks_are_ignored_with_a_note()
    {
        var cfg = SshConfig.Parse("Match host x exec \"true\"\n  User nope\nHost x\n  User yes\n", _home);
        var r = cfg.Resolve("x");
        Assert.Equal("yes", r.User);
        Assert.Contains(r.Notes, n => n.Contains("Match"));
    }

    [Fact]
    public void Include_reads_relative_globs_and_survives_cycles()
    {
        var ssh = Path.Combine(_home, ".ssh");
        Directory.CreateDirectory(Path.Combine(ssh, "conf.d"));
        File.WriteAllText(Path.Combine(ssh, "conf.d", "a.conf"), "Host fromA\n  User ua\n");
        File.WriteAllText(Path.Combine(ssh, "conf.d", "b.conf"), "Host fromB\n  User ub\nInclude ~/.ssh/config\n");
        File.WriteAllText(Path.Combine(ssh, "config"), "Include conf.d/*.conf\nHost main\n  User um\n");

        var cfg = SshConfig.Load(home: _home);
        Assert.Equal("ua", cfg.Resolve("fromA").User);
        Assert.Equal("ub", cfg.Resolve("fromB").User);
        Assert.Equal("um", cfg.Resolve("main").User);
        Assert.Contains(cfg.Notes, n => n.Contains("cycle"));
    }

    [Fact]
    public void Missing_file_gives_an_empty_config()
    {
        var cfg = SshConfig.Load(Path.Combine(_home, "nope"), _home);
        Assert.Empty(cfg.DeclaredHosts);
        Assert.Equal("h", cfg.Resolve("h").EffectiveHost);
    }

    [Fact]
    public void Forwards_and_known_hosts_files_are_collected()
    {
        var cfg = SshConfig.Parse("""
            Host t
              LocalForward 5432 db:5432
              LocalForward 127.0.0.1:8080 web:80
              DynamicForward 1080
              UserKnownHostsFile ~/.ssh/kh1 ~/.ssh/kh2
              ProxyJump ops@bastion:2222
            """, _home);
        var r = cfg.Resolve("t");
        Assert.Equal(2, r.LocalForwards.Count);
        Assert.Single(r.DynamicForwards);
        Assert.Equal(2, r.UserKnownHostsFiles.Count);
        Assert.Equal("ops@bastion:2222", r.ProxyJump);
    }
}
