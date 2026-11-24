using RDMan.Core.Credentials;
using RDMan.Core.Models;

namespace RDMan.Core.Tests;

public class CredentialResolverTests
{
    private static (ConnectionDocument doc, GroupNode client, GroupNode env, ServerNode server) Build()
    {
        var server = new ServerNode { Name = "web-01", Host = "10.0.0.5" };
        var env = new GroupNode { Name = "Producción", Children = { server } };
        var client = new GroupNode { Name = "Cliente A", Children = { env } };
        var doc = new ConnectionDocument();
        doc.Root.Children.Add(client);
        return (doc, client, env, server);
    }

    [Fact]
    public void Defaults_to_current_user_when_nothing_is_configured()
    {
        var (doc, _, _, server) = Build();
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.CurrentUser, r.Kind);
        Assert.Null(r.SourceNode);
        Assert.False(r.IsMisconfigured);
    }

    [Fact]
    public void Inherits_from_nearest_configured_ancestor()
    {
        var (doc, client, env, server) = Build();
        client.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "root-user", ProtectedPassword = "x" };
        env.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = " deploy ", ProtectedPassword = "y" };
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.Explicit, r.Kind);
        Assert.Equal("deploy", r.Username);
        Assert.Same(env, r.SourceNode);
        Assert.True(r.IsInherited(server));
    }

    [Fact]
    public void Server_override_wins_over_group()
    {
        var (doc, client, _, server) = Build();
        client.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "a" };
        server.Credentials = new CredentialSettings { Mode = CredentialMode.Guest };
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.Guest, r.Kind);
        Assert.Equal("guest", r.Username);
        Assert.False(r.IsInherited(server));
    }

    [Fact]
    public void Profile_is_resolved_by_id()
    {
        var (doc, client, _, server) = Build();
        var profile = new CredentialProfile { Name = "deploy@A", Username = "deploy", ProtectedPassword = "p" };
        doc.Profiles.Add(profile);
        client.Credentials = new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = profile.Id };
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.Explicit, r.Kind);
        Assert.Same(profile, r.Profile);
        Assert.Equal("p", r.ProtectedPassword);
    }

    [Fact]
    public void Missing_profile_falls_back_and_flags_misconfiguration()
    {
        var (doc, _, _, server) = Build();
        server.Credentials = new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = Guid.NewGuid() };
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.CurrentUser, r.Kind);
        Assert.True(r.IsMisconfigured);
    }

    [Fact]
    public void Custom_without_username_is_misconfigured()
    {
        var (doc, _, _, server) = Build();
        server.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "  " };
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.True(r.IsMisconfigured);
    }

    [Fact]
    public void Explicit_current_user_on_group_stops_inheritance()
    {
        var (doc, client, env, server) = Build();
        client.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "a" };
        env.Credentials = new CredentialSettings { Mode = CredentialMode.CurrentUser };
        var r = CredentialResolver.Resolve(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.CurrentUser, r.Kind);
        Assert.Same(env, r.SourceNode);
    }
}
