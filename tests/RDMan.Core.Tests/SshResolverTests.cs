using RDMan.Core.Models;
using RDMan.Core.Ssh;

namespace RDMan.Core.Tests;

public class SshResolverTests
{
    private static (ConnectionDocument doc, GroupNode aws, ServerNode server, TreeIndex index) Tree()
    {
        var server = new ServerNode { Name = "pangya", Host = "pangya-vps", Files = false, Terminal = new SshEndpoint() };
        var aws = new GroupNode { Name = "AWS", Children = { server } };
        var doc = new ConnectionDocument();
        doc.Root.Children.Add(aws);
        return (doc, aws, server, new TreeIndex(doc.Root));
    }

    private static SshConfig OwnerConfig(string home = @"C:\Users\me") => SshConfig.Parse("""
        Host pangya-vps
            HostName 45.79.2.143
            User ubuntu
            IdentityFile ~/.ssh/pangya_vps
            IdentitiesOnly yes
        """, home);

    [Fact]
    public void Nothing_configured_means_auto_like_plain_ssh()
    {
        var (doc, _, server, index) = Tree();
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        Assert.Equal(SshAuthMode.Auto, auth.Mode);
        Assert.Null(auth.ModeSource);
        Assert.Null(auth.Username);
    }

    [Fact]
    public void Group_key_is_inherited_and_server_overrides_only_the_user()
    {
        var (doc, aws, server, index) = Tree();
        var key = new SshKeyEntry { Name = "aws.pem", ProtectedKey = "dpapi1:x" };
        doc.SshKeys.Add(key);
        aws.SshAuth = new SshAuthSettings { Mode = SshAuthMode.Key, KeyId = key.Id, Username = "ec2-user" };
        server.SshAuth = new SshAuthSettings { Username = "admin" };

        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        Assert.Equal(SshAuthMode.Key, auth.Mode);
        Assert.Same(aws, auth.ModeSource);
        Assert.Same(key, auth.Key);
        Assert.Equal("admin", auth.Username);
        Assert.Same(server, auth.UsernameSource);
    }

    [Fact]
    public void Missing_or_stripped_key_falls_back_to_auto_and_says_so()
    {
        var (doc, aws, server, index) = Tree();
        var stripped = new SshKeyEntry { Name = "exported" }; // no key material after an export without secrets
        doc.SshKeys.Add(stripped);
        aws.SshAuth = new SshAuthSettings { Mode = SshAuthMode.Key, KeyId = stripped.Id };
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        Assert.Equal(SshAuthMode.Auto, auth.Mode);
        Assert.True(auth.IsMisconfigured);

        aws.SshAuth.KeyId = Guid.NewGuid();
        Assert.True(SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys).IsMisconfigured);
    }

    [Fact]
    public void Password_profile_brings_its_own_user()
    {
        var (doc, _, server, index) = Tree();
        var profile = new CredentialProfile { Name = "ops", Username = "operator", ProtectedPassword = "dpapi1:p" };
        doc.Profiles.Add(profile);
        server.SshAuth = new SshAuthSettings { Mode = SshAuthMode.Password, ProfileId = profile.Id, Username = "ignored" };
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        Assert.Equal(SshAuthMode.Password, auth.Mode);
        Assert.Equal("operator", auth.Username);
        Assert.Equal("dpapi1:p", auth.ProtectedPassword);
    }

    [Fact]
    public void Target_uses_ssh_config_like_ssh_alias_does()
    {
        var (doc, _, server, index) = Tree();
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        var settings = SshSettingsResolver.Resolve(server, index);
        var t = SshTargetResolver.Resolve(server, auth, settings, OwnerConfig(), "relic");

        Assert.Equal("45.79.2.143", t.HostName);
        Assert.Equal(22, t.Port);
        Assert.Equal("ubuntu", t.User);
        Assert.True(t.IdentitiesOnly);
        Assert.Equal(@"C:\Users\me\.ssh\pangya_vps", Assert.Single(t.IdentityFiles));
        Assert.Equal(SshTargetResolver.DefaultKeepAlive, t.KeepAliveSeconds);
        Assert.Equal(@"C:\Users\me\.ssh\known_hosts", Assert.Single(t.KnownHostsFiles));
        Assert.Equal("ubuntu@45.79.2.143", t.Display);
        Assert.Equal("ssh pangya-vps", t.ToCommandLine());
    }

    [Fact]
    public void Explicit_values_win_over_the_config_and_config_can_be_off()
    {
        var (doc, _, server, index) = Tree();
        server.Terminal!.Port = 2222;
        server.SshAuth = new SshAuthSettings { Username = "root" };
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        var settings = SshSettingsResolver.Resolve(server, index);
        var t = SshTargetResolver.Resolve(server, auth, settings, OwnerConfig(), "relic");
        Assert.Equal(2222, t.Port);
        Assert.Equal("root", t.User);
        Assert.Equal("45.79.2.143", t.HostName);

        server.Terminal.UseSshConfig = false;
        var plain = SshTargetResolver.Resolve(server, auth, settings, OwnerConfig(), "relic");
        Assert.Equal("pangya-vps", plain.HostName);
        Assert.Null(plain.Config);
        Assert.Equal(3, plain.IdentityFiles.Count); // default id_ed25519, id_ecdsa, id_rsa
    }

    [Fact]
    public void Key_mode_does_not_try_config_identity_files()
    {
        var (doc, _, server, index) = Tree();
        var key = new SshKeyEntry { ProtectedKey = "dpapi1:x" };
        doc.SshKeys.Add(key);
        server.SshAuth = new SshAuthSettings { Mode = SshAuthMode.Key, KeyId = key.Id };
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        var t = SshTargetResolver.Resolve(server, auth, SshSettingsResolver.Resolve(server, index), OwnerConfig(), "relic");
        Assert.Empty(t.IdentityFiles);
    }

    [Fact]
    public void Settings_inherit_with_sources_and_proxyjump_none_disables_the_config_jump()
    {
        var (_, aws, server, index) = Tree();
        aws.Ssh = new SshProfile { InjectPrompt = false, KeepAliveSeconds = 10, ProxyJump = "none" };
        var s = SshSettingsResolver.Resolve(server, index);
        Assert.False(s.InjectPrompt.Value);
        Assert.Same(aws, s.InjectPrompt.Source);
        Assert.True(s.ShowStats.Value);
        Assert.True(s.ShowStats.IsDefault);
        Assert.Equal(10, s.KeepAliveSeconds.Value);
        Assert.Equal("auto", s.ColorScheme.Value);

        var cfg = SshConfig.Parse("Host pangya-vps\n  ProxyJump bastion\n", @"C:\h");
        var t = SshTargetResolver.Resolve(server, new EffectiveSshAuth(SshAuthMode.Auto, null, null, null, null, null, null), s, cfg);
        Assert.Empty(t.Jumps);
        Assert.False(t.JumpSpecInvalid);
    }

    [Fact]
    public void Jump_hosts_come_from_the_config_and_hops_resolve_through_it()
    {
        var (doc, _, server, index) = Tree();
        var cfg = SshConfig.Parse("""
            Host pangya-vps
              ProxyJump ops@bastion:2200,inner
            Host bastion
              HostName 1.2.3.4
            """, @"C:\h");
        var auth = SshAuthResolver.Resolve(server, index, doc.Profiles, doc.SshKeys);
        var t = SshTargetResolver.Resolve(server, auth, SshSettingsResolver.Resolve(server, index), cfg);
        Assert.Equal(2, t.Jumps.Count);
        Assert.Equal(new SshDestination("ops", "bastion", 2200), t.Jumps[0]);

        var hop = SshTargetResolver.ResolveHop(t.Jumps[0], cfg);
        Assert.Equal("1.2.3.4", hop.HostName);
        Assert.Equal(2200, hop.Port);
        Assert.Equal("ops", hop.User);
        Assert.Empty(hop.Forwards);
    }

    [Theory]
    [InlineData("host", null, "host", null)]
    [InlineData("ubuntu@1.2.3.4:2222", "ubuntu", "1.2.3.4", 2222)]
    [InlineData("ssh://me@box", "me", "box", null)]
    [InlineData("[fe80::1]:22", null, "fe80::1", 22)]
    [InlineData("fe80::1", null, "fe80::1", null)]
    [InlineData("first.last@corp@host", "first.last@corp", "host", null)]
    public void Destinations_parse(string text, string? user, string host, int? port)
    {
        Assert.True(SshDestination.TryParse(text, out var d));
        Assert.Equal(new SshDestination(user, host, port), d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("@host")]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData("a b")]
    [InlineData("[fe80::1")]
    public void Bad_destinations_are_rejected(string text) => Assert.False(SshDestination.TryParse(text, out _));

    [Theory]
    [InlineData("ssh ubuntu@box -p 2200", "ubuntu", "box", 2200)]
    [InlineData("ssh -l root -p 22 box", "root", "box", 22)]
    [InlineData("pangya-vps", null, "pangya-vps", null)]
    public void Quick_connect_command_lines_parse(string text, string? user, string host, int? port)
    {
        Assert.True(SshDestination.TryParseCommandLine(text, out var d));
        Assert.Equal(new SshDestination(user, host, port), d);
    }

    [Fact]
    public void Unsupported_ssh_options_are_not_silently_ignored()
        => Assert.False(SshDestination.TryParseCommandLine("ssh -A box", out _));

    [Theory]
    [InlineData(SshForwardKind.Local, "5432 db:5432", null, 5432, "db", 5432)]
    [InlineData(SshForwardKind.Local, "127.0.0.1:8080 web:80", "127.0.0.1", 8080, "web", 80)]
    [InlineData(SshForwardKind.Remote, "9000 localhost 3000", null, 9000, "localhost", 3000)]
    [InlineData(SshForwardKind.Local, "[::1]:8443 [fe80::2]:443", "::1", 8443, "fe80::2", 443)]
    [InlineData(SshForwardKind.Dynamic, "*:1080", "*", 1080, null, 0)]
    public void Forward_specs_parse(SshForwardKind kind, string text, string? bind, int bindPort, string? target, int targetPort)
    {
        Assert.True(SshTargetResolver.TryParseForward(kind, text, out var f));
        Assert.Equal(bind, f.BindAddress);
        Assert.Equal(bindPort, f.BindPort);
        Assert.Equal(target, f.TargetHost);
        Assert.Equal(targetPort, f.TargetPort);
    }

    [Theory]
    [InlineData(SshForwardKind.Local, "5432")]
    [InlineData(SshForwardKind.Local, "x db:5432")]
    [InlineData(SshForwardKind.Dynamic, "1080 extra")]
    public void Bad_forward_specs_are_rejected(SshForwardKind kind, string text)
        => Assert.False(SshTargetResolver.TryParseForward(kind, text, out _));
}
