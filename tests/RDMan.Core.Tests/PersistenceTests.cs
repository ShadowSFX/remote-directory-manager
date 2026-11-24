using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Security;

namespace RDMan.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rdman-tests-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ConnectionDocument Sample()
    {
        var doc = new ConnectionDocument();
        var profile = new CredentialProfile { Name = "p", Username = "u", ProtectedPassword = "dpapi1:abc" };
        doc.Profiles.Add(profile);
        var group = new GroupNode
        {
            Name = "Cliente A",
            Color = "teal",
            Credentials = new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = profile.Id },
        };
        group.Children.Add(new ServerNode { Name = "web", Host = "10.0.0.5", Share = "www", Tags = { "prod" }, Credentials = new() { Mode = CredentialMode.Custom, Username = "x", ProtectedPassword = "dpapi1:zzz" } });
        group.Children.Add(new GroupNode { Name = "Vacío" });
        doc.Root.Children.Add(group);
        return doc;
    }

    [Fact]
    public void Round_trips_polymorphic_tree()
    {
        var path = Path.Combine(_dir, "c.rdm");
        var doc = Sample();
        ConnectionStore.Save(path, doc);
        var loaded = ConnectionStore.Load(path);
        Assert.Equal(LoadOutcome.Loaded, loaded.Outcome);
        var group = Assert.IsType<GroupNode>(Assert.Single(loaded.Value.Root.Children));
        Assert.Equal("Cliente A", group.Name);
        var server = Assert.IsType<ServerNode>(group.Children[0]);
        Assert.Equal("www", server.Share);
        Assert.Equal(CredentialMode.Custom, server.Credentials.Mode);
        Assert.IsType<GroupNode>(group.Children[1]);
        Assert.Equal(doc.Profiles[0].Id, group.Credentials.ProfileId);
    }

    [Fact]
    public void Second_save_keeps_backup_and_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir, "c.rdm");
        ConnectionStore.Save(path, Sample());
        ConnectionStore.Save(path, new ConnectionDocument());
        Assert.True(File.Exists(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Corrupt_file_recovers_from_backup()
    {
        var path = Path.Combine(_dir, "c.rdm");
        ConnectionStore.Save(path, Sample());
        ConnectionStore.Save(path, Sample()); // creates .bak with valid content
        File.WriteAllText(path, "{ not json");
        var loaded = ConnectionStore.Load(path);
        Assert.Equal(LoadOutcome.RecoveredFromBackup, loaded.Outcome);
        Assert.Single(loaded.Value.Root.Children);
    }

    [Fact]
    public void Corrupt_without_backup_returns_empty_and_preserves_file()
    {
        var path = Path.Combine(_dir, "c.rdm");
        File.WriteAllText(path, "garbage");
        var loaded = ConnectionStore.Load(path);
        Assert.Equal(LoadOutcome.Corrupt, loaded.Outcome);
        Assert.Empty(loaded.Value.Root.Children);
        Assert.NotEmpty(Directory.GetFiles(_dir, "c.rdm.corrupt-*"));
    }

    [Fact]
    public void Missing_file_returns_defaults()
    {
        var loaded = ConnectionStore.Load(Path.Combine(_dir, "none.rdm"));
        Assert.Equal(LoadOutcome.Missing, loaded.Outcome);
    }

    [Fact]
    public void Discriminator_may_appear_after_other_properties()
    {
        var path = Path.Combine(_dir, "hand.rdm");
        File.WriteAllText(path, """
        { "root": { "name": "root", "kind": "group", "children": [ { "host": "1.2.3.4", "name": "a", "kind": "server" } ] } }
        """);
        var loaded = ConnectionStore.Load(path);
        Assert.Equal(LoadOutcome.Loaded, loaded.Outcome);
        Assert.IsType<ServerNode>(Assert.Single(loaded.Value.Root.Children));
    }

    [Fact]
    public void Normalize_repairs_duplicate_ids_and_nulls()
    {
        var id = Guid.NewGuid();
        var doc = new ConnectionDocument();
        doc.Root.Children.Add(new ServerNode { Id = id, Name = "a", Host = @"\\10.0.0.1\", Share = "  " });
        doc.Root.Children.Add(new ServerNode { Id = id, Name = "b", Host = "x", Tags = { "a", "A", " " } });
        ConnectionStore.Normalize(doc);
        Assert.NotEqual(doc.Root.Children[0].Id, doc.Root.Children[1].Id);
        var a = (ServerNode)doc.Root.Children[0];
        Assert.Equal("10.0.0.1", a.Host);
        Assert.Null(a.Share);
        Assert.Single(((ServerNode)doc.Root.Children[1]).Tags);
    }

    [Fact]
    public void Export_without_secrets_strips_every_password()
    {
        var copy = ConnectionStore.CloneForExport(Sample(), includeSecrets: false);
        Assert.All(copy.Profiles, p => Assert.Null(p.ProtectedPassword));
        Assert.All(TreeIndex.AllServers(copy.Root), s => Assert.Null(s.Credentials.ProtectedPassword));
    }

    [Fact]
    public void Reassign_ids_remaps_profile_references()
    {
        var doc = Sample();
        var oldProfileId = doc.Profiles[0].Id;
        var oldGroupId = doc.Root.Children[0].Id;
        ConnectionStore.ReassignIds(doc);
        Assert.NotEqual(oldProfileId, doc.Profiles[0].Id);
        Assert.NotEqual(oldGroupId, doc.Root.Children[0].Id);
        Assert.Equal(doc.Profiles[0].Id, doc.Root.Children[0].Credentials.ProfileId);
    }

    [Fact]
    public void AppPaths_prefers_portable_folder_and_falls_back()
    {
        var portable = AppPaths.Resolve(_dir, Path.Combine(_dir, "appdata"));
        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(_dir, AppPaths.PortableFolderName), portable.DataDirectory);

        var fallback = AppPaths.Resolve(_dir, Path.Combine(_dir, "appdata"), _ => false);
        Assert.False(fallback.IsPortable);
        Assert.Equal(Path.Combine(_dir, "appdata", "RDMan"), fallback.DataDirectory);
        Assert.True(Directory.Exists(fallback.DataDirectory));
    }

    [Fact]
    public void Settings_normalize_clamps_values()
    {
        var s = new AppSettings { Language = "fr", HealthCheckIntervalSeconds = 1, SidebarWidth = 10 };
        s.Panes.Add(new SavedPane { ActiveIndex = 9, Tabs = { new SavedTab() } });
        s.Normalize();
        Assert.Equal("system", s.Language);
        Assert.Equal(10, s.HealthCheckIntervalSeconds);
        Assert.Equal(272, s.SidebarWidth);
        Assert.Equal(0, s.Panes[0].ActiveIndex);
    }

    [Fact]
    public void Dpapi_round_trip_and_garbage_is_null()
    {
        var p = new DpapiSecretProtector();
        var blob = p.Protect("s3cr3t ñ");
        Assert.StartsWith("dpapi1:", blob);
        Assert.DoesNotContain("s3cr3t", blob);
        Assert.Equal("s3cr3t ñ", p.TryUnprotect(blob));
        Assert.Equal("", p.TryUnprotect(p.Protect("")));
        Assert.Null(p.TryUnprotect("dpapi1:AAAA"));
        Assert.Null(p.TryUnprotect("plain"));
        Assert.Null(p.TryUnprotect(null));
    }
}
