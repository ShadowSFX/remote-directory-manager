using RDMan.Core.Credentials;
using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Rdp;

namespace RDMan.Core.Tests;

public sealed class DesktopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdman-desktop-tests-" + Guid.NewGuid().ToString("N"));

    public DesktopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static (ConnectionDocument doc, GroupNode group, ServerNode server) Tree()
    {
        var server = new ServerNode { Name = "dc01", Host = "10.0.0.9", Desktop = new DesktopEndpoint() };
        var group = new GroupNode { Name = "Producción", Children = { server } };
        var doc = new ConnectionDocument();
        doc.Root.Children.Add(group);
        return (doc, group, server);
    }

    [Fact]
    public void Desktop_credential_falls_back_to_files_credential()
    {
        var (doc, group, server) = Tree();
        group.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "smbuser", ProtectedPassword = "p" };
        var r = CredentialResolver.ResolveDesktop(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.Explicit, r.Kind);
        Assert.Equal("smbuser", r.Username);
        Assert.Same(group, r.SourceNode);
    }

    [Fact]
    public void Desktop_credential_on_group_beats_files_credential_on_server()
    {
        var (doc, group, server) = Tree();
        server.Credentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "smbuser" };
        group.DesktopCredentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = @"CORP\admin", ProtectedPassword = "x" };
        var r = CredentialResolver.ResolveDesktop(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(@"CORP\admin", r.Username);
        Assert.Same(group, r.SourceNode);
    }

    [Fact]
    public void Desktop_current_user_means_windows_saved_credential()
    {
        var (doc, _, server) = Tree();
        server.DesktopCredentials = new CredentialSettings { Mode = CredentialMode.CurrentUser };
        var r = CredentialResolver.ResolveDesktop(server, new TreeIndex(doc.Root), doc.Profiles);
        Assert.Equal(EffectiveCredentialKind.CurrentUser, r.Kind);
        Assert.Null(r.Username);
    }

    [Fact]
    public void Desktop_profile_is_resolved_and_missing_profile_is_flagged()
    {
        var (doc, group, server) = Tree();
        var p = new CredentialProfile { Name = "admins", Username = "administrator", ProtectedPassword = "z" };
        doc.Profiles.Add(p);
        group.DesktopCredentials = new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = p.Id };
        Assert.Same(p, CredentialResolver.ResolveDesktop(server, new TreeIndex(doc.Root), doc.Profiles).Profile);

        group.DesktopCredentials.ProfileId = Guid.NewGuid();
        Assert.True(CredentialResolver.ResolveDesktop(server, new TreeIndex(doc.Root), doc.Profiles).IsMisconfigured);
    }

    [Theory]
    [InlineData(@"CORP\admin", "admin", "CORP")]
    [InlineData("admin@corp.local", "admin@corp.local", null)]
    [InlineData("administrator", "administrator", null)]
    [InlineData(" ", "", null)]
    public void Splits_domain_users(string input, string user, string? domain)
    {
        var (u, d) = CredentialResolver.SplitUser(input);
        Assert.Equal(user, u);
        Assert.Equal(domain, d);
    }

    [Fact]
    public void Rdp_settings_inherit_per_field_with_origin()
    {
        var (doc, group, server) = Tree();
        group.Rdp.RedirectDrives = true;
        group.Rdp.Audio = RdpAudio.Off;
        server.Rdp.Audio = RdpAudio.Remote;
        var idx = new TreeIndex(doc.Root);
        var e = RdpSettingsResolver.Resolve(server, idx);
        Assert.True(e.RedirectDrives.Value);
        Assert.Same(group, e.RedirectDrives.Source);
        Assert.Equal(RdpAudio.Remote, e.Audio.Value);
        Assert.Same(server, e.Audio.Source);
        Assert.True(e.RedirectClipboard.Value);
        Assert.True(e.RedirectClipboard.IsDefault);
        Assert.True(e.NetworkLevelAuthentication.Value);
        Assert.Equal(32, e.ColorDepth.Value);

        var parent = RdpSettingsResolver.ResolveParent(server, idx);
        Assert.Equal(RdpAudio.Off, parent.Audio.Value);
        Assert.Same(group, parent.Audio.Source);
    }

    [Fact]
    public void Invalid_color_depth_falls_back_to_32()
    {
        var (doc, _, server) = Tree();
        server.Rdp.ColorDepth = 7;
        Assert.Equal(32, RdpSettingsResolver.Resolve(server, new TreeIndex(doc.Root)).ColorDepth.Value);
    }

    [Theory]
    [InlineData(2055, 0, RdpErrorAction.EditCredentials, true)]
    [InlineData(516, 0, RdpErrorAction.Reconnect, true)]
    [InlineData(260, 0, RdpErrorAction.CheckHost, true)]
    [InlineData(2308, 0, RdpErrorAction.Reconnect, true)]
    [InlineData(1, 0, RdpErrorAction.Reconnect, false)]
    [InlineData(2, 12, RdpErrorAction.Reconnect, false)]
    [InlineData(3, 5, RdpErrorAction.Reconnect, true)]
    [InlineData(8455, 0, RdpErrorAction.EditCredentials, true)]
    [InlineData(99999, 0, RdpErrorAction.Reconnect, true)]
    public void Translates_disconnect_reasons(int code, int ext, RdpErrorAction action, bool isError)
    {
        var t = RdpErrorTranslator.Translate(code, ext);
        Assert.Equal(action, t.Action);
        Assert.Equal(isError, t.IsError);
        Assert.True(Localization.Strings.Contains(t.TitleKey), t.TitleKey);
        Assert.True(Localization.Strings.Contains(t.DetailKey), t.DetailKey);
    }

    [Fact]
    public void Every_rdp_translation_key_exists()
    {
        int[] codes = [0, 1, 2, 3, 260, 264, 516, 520, 1028, 1288, 1540, 1796, 2055, 2056, 2308, 2312, 2567, 2823, 3079, 3335, 3591, 3847, 4615, 5639, 6151, 6919, 8455, 777];
        int[] exts = [0, 1, 2, 3, 4, 5, 7, 9, 10, 11, 12, 266, 4100];
        foreach (var c in codes)
            foreach (var x in exts)
            {
                var t = RdpErrorTranslator.Translate(c, x);
                Assert.True(Localization.Strings.Contains(t.TitleKey), t.TitleKey);
                Assert.True(Localization.Strings.Contains(t.DetailKey), t.DetailKey);
            }
    }

    [Theory]
    [InlineData("fileserver", "fileserver", null)]
    [InlineData("10.0.0.5:3390", "10.0.0.5", 3390)]
    [InlineData("rdp://host.example.com:4000/", "host.example.com", 4000)]
    [InlineData("[fe80::1]:3389", "fe80::1", 3389)]
    [InlineData("[fe80::1]", "fe80::1", null)]
    [InlineData("fe80::1", "fe80::1", null)] // bare IPv6 = host without port
    public void Parses_rdp_targets(string input, string host, int? port)
    {
        Assert.True(HostPort.TryParse(input, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:abc")]
    [InlineData("bad host")]
    [InlineData("[fe80::1]:99999")]
    public void Rejects_bad_rdp_targets(string input) => Assert.False(HostPort.TryParse(input, out _, out _));

    [Fact]
    public void Default_action_respects_capabilities()
    {
        var s = new ServerNode { Files = true, Desktop = null, DefaultAction = ServerAction.Desktop };
        Assert.Equal(ServerAction.Files, s.EffectiveDefaultAction);
        s.Files = false;
        s.Desktop = new DesktopEndpoint();
        Assert.Equal(ServerAction.Desktop, s.EffectiveDefaultAction);
        s.Files = true;
        Assert.Equal(ServerAction.Desktop, s.EffectiveDefaultAction);
        s.DefaultAction = ServerAction.Files;
        Assert.Equal(ServerAction.Files, s.EffectiveDefaultAction);
    }

    [Fact]
    public void Schema_v1_file_migrates_to_files_only_servers()
    {
        var path = Path.Combine(_dir, "v1.rdm");
        File.WriteAllText(path, """
        { "schemaVersion": 1, "root": { "kind": "group", "name": "root", "children": [
          { "kind": "server", "name": "web", "host": "10.0.0.5", "share": "www", "credentials": { "mode": "inherit" } } ] } }
        """);
        var loaded = ConnectionStore.Load(path);
        Assert.Equal(ConnectionDocument.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        var s = Assert.IsType<ServerNode>(Assert.Single(loaded.Value.Root.Children));
        Assert.True(s.Files);
        Assert.False(s.HasDesktop);
        Assert.NotNull(s.DesktopCredentials);
        Assert.NotNull(s.Rdp);
        Assert.Equal(CredentialMode.Inherit, s.DesktopCredentials.Mode);
    }

    [Fact]
    public void Round_trip_keeps_desktop_settings_and_skips_computed_properties()
    {
        var (doc, group, server) = Tree();
        server.Desktop!.Port = 3390;
        server.DefaultAction = ServerAction.Desktop;
        group.Rdp.RedirectPrinters = true;
        group.DesktopCredentials = new CredentialSettings { Mode = CredentialMode.CurrentUser };
        var path = Path.Combine(_dir, "v2.rdm");
        ConnectionStore.Save(path, doc);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("hasDesktop", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effectiveDefaultAction", json, StringComparison.OrdinalIgnoreCase);

        var back = ConnectionStore.Load(path).Value;
        var g = Assert.IsType<GroupNode>(back.Root.Children[0]);
        var s = Assert.IsType<ServerNode>(g.Children[0]);
        Assert.Equal(3390, s.Desktop!.Port);
        Assert.Equal(ServerAction.Desktop, s.DefaultAction);
        Assert.True(g.Rdp.RedirectPrinters);
        Assert.Equal(CredentialMode.CurrentUser, g.DesktopCredentials.Mode);
    }

    [Fact]
    public void Normalize_repairs_invalid_desktop_values()
    {
        var doc = new ConnectionDocument();
        doc.Root.Children.Add(new ServerNode { Name = "x", Host = "h", Files = false, Desktop = null });
        doc.Root.Children.Add(new ServerNode { Name = "y", Host = "h", Desktop = new DesktopEndpoint { Port = 0 }, Rdp = new RdpProfile { ColorDepth = 12 } });
        ConnectionStore.Normalize(doc);
        Assert.True(((ServerNode)doc.Root.Children[0]).Files);
        var y = (ServerNode)doc.Root.Children[1];
        Assert.Equal(DesktopEndpoint.DefaultPort, y.Desktop!.Port);
        Assert.Null(y.Rdp.ColorDepth);
    }

    [Fact]
    public void Export_strips_desktop_passwords_and_reassign_remaps_desktop_profiles()
    {
        var (doc, group, server) = Tree();
        var p = new CredentialProfile { Name = "a", Username = "u", ProtectedPassword = "s" };
        doc.Profiles.Add(p);
        server.DesktopCredentials = new CredentialSettings { Mode = CredentialMode.Custom, Username = "u", ProtectedPassword = "secret" };
        group.DesktopCredentials = new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = p.Id };

        var export = ConnectionStore.CloneForExport(doc, includeSecrets: false);
        Assert.Null(((ServerNode)((GroupNode)export.Root.Children[0]).Children[0]).DesktopCredentials.ProtectedPassword);

        ConnectionStore.ReassignIds(doc);
        Assert.Equal(doc.Profiles[0].Id, group.DesktopCredentials.ProfileId);
    }

    [Fact]
    public void Saved_document_contains_only_settings_not_computed_flags()
    {
        var (doc, _, server) = Tree();
        server.Rdp = new RdpProfile { RedirectDrives = true };
        var json = System.Text.Json.JsonSerializer.Serialize(doc, JsonFileStore.Options);
        Assert.DoesNotContain("isEmpty", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasDesktop", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redirectDrives", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gateway_is_inherited_as_a_whole_and_off_overrides()
    {
        var (doc, group, server) = Tree();
        var index = new TreeIndex(doc.Root);
        Assert.Null(RdpSettingsResolver.Resolve(server, index).Gateway.Value);
        group.Rdp.Gateway = new RdpGateway { Host = "gw.corp", Usage = RdpGatewayUsage.BypassLocal };
        var eff = RdpSettingsResolver.Resolve(server, index).Gateway;
        Assert.Equal("gw.corp", eff.Value!.Host);
        Assert.Same(group, eff.Source);
        server.Rdp.Gateway = new RdpGateway { Usage = RdpGatewayUsage.Off };
        var off = RdpSettingsResolver.Resolve(server, index).Gateway;
        Assert.Null(off.Value);
        Assert.Same(server, off.Source);
    }

    [Fact]
    public void Gateway_is_normalized_cloned_and_its_password_never_exported()
    {
        var (doc, group, server) = Tree();
        group.Rdp.Gateway = new RdpGateway { Host = "  gw  ", Logon = RdpGatewayLogon.Custom, Username = "u", ProtectedPassword = "secret" };
        server.Rdp.Gateway = new RdpGateway { Host = " ", Usage = RdpGatewayUsage.Always };
        ConnectionStore.Normalize(doc);
        Assert.Equal("gw", group.Rdp.Gateway!.Host);
        Assert.Null(server.Rdp.Gateway); // "always" without a host means nothing
        var clone = group.Rdp.Clone();
        clone.Gateway!.Host = "changed";
        Assert.Equal("gw", group.Rdp.Gateway.Host);
        var export = ConnectionStore.CloneForExport(doc, includeSecrets: false);
        Assert.Null(((GroupNode)export.Root.Children[0]).Rdp.Gateway!.ProtectedPassword);
    }
}
