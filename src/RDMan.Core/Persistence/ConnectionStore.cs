using System.Text.Json;
using RDMan.Core.Models;

namespace RDMan.Core.Persistence;

public static class ConnectionStore
{
    public static LoadResult<ConnectionDocument> Load(string path)
    {
        var result = JsonFileStore.Load(path, () => new ConnectionDocument());
        Normalize(result.Value);
        return result;
    }

    public static void Save(string path, ConnectionDocument doc) => JsonFileStore.Save(path, doc);

    /// <summary>Repairs nulls/duplicates from hand-edited or older files and upgrades the schema.</summary>
    public static void Normalize(ConnectionDocument doc)
    {
        doc.Root ??= new GroupNode { Name = "root" };
        doc.Profiles ??= new List<CredentialProfile>();
        var seen = new HashSet<Guid>();
        NormalizeNode(doc.Root, seen);

        var seenProfiles = new HashSet<Guid>();
        doc.Profiles.RemoveAll(p => p is null);
        foreach (var p in doc.Profiles)
        {
            if (p.Id == Guid.Empty || !seenProfiles.Add(p.Id)) { p.Id = Guid.NewGuid(); seenProfiles.Add(p.Id); }
            p.Name ??= "";
            p.Username ??= "";
        }

        // Schema migrations go here (SchemaVersion < CurrentSchemaVersion).
        doc.SchemaVersion = ConnectionDocument.CurrentSchemaVersion;
    }

    private static void NormalizeNode(TreeNode node, HashSet<Guid> seen)
    {
        if (node.Id == Guid.Empty || !seen.Add(node.Id))
        {
            node.Id = Guid.NewGuid();
            seen.Add(node.Id);
        }
        node.Name ??= "";
        node.Credentials ??= new CredentialSettings();
        switch (node)
        {
            case GroupNode g:
                g.Children ??= new List<TreeNode>();
                g.Children.RemoveAll(c => c is null);
                foreach (var c in g.Children) NormalizeNode(c, seen);
                break;
            case ServerNode s:
                s.Host = (s.Host ?? "").Trim().Trim('\\', '/');
                s.Share = string.IsNullOrWhiteSpace(s.Share) ? null : s.Share.Trim().Trim('\\', '/');
                s.StartFolder = string.IsNullOrWhiteSpace(s.StartFolder) ? null : s.StartFolder.Trim().Trim('\\', '/');
                s.Tags ??= new List<string>();
                s.Tags = s.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                break;
        }
    }

    /// <summary>
    /// Deep copy for export. When <paramref name="includeSecrets"/> is false every password is stripped
    /// (DPAPI blobs are useless on other machines anyway).
    /// </summary>
    public static ConnectionDocument CloneForExport(ConnectionDocument doc, bool includeSecrets)
    {
        var json = JsonSerializer.Serialize(doc, JsonFileStore.Options);
        var copy = JsonSerializer.Deserialize<ConnectionDocument>(json, JsonFileStore.Options)!;
        if (!includeSecrets)
        {
            foreach (var p in copy.Profiles) p.ProtectedPassword = null;
            StripSecrets(copy.Root);
        }
        return copy;
    }

    private static void StripSecrets(TreeNode node)
    {
        node.Credentials.ProtectedPassword = null;
        if (node is GroupNode g) foreach (var c in g.Children) StripSecrets(c);
    }

    /// <summary>
    /// Re-identifies every node/profile of an imported document so it can be merged without id collisions.
    /// Profile references are remapped accordingly.
    /// </summary>
    public static void ReassignIds(ConnectionDocument doc)
    {
        var profileMap = new Dictionary<Guid, Guid>();
        foreach (var p in doc.Profiles)
        {
            var newId = Guid.NewGuid();
            profileMap[p.Id] = newId;
            p.Id = newId;
        }
        Reassign(doc.Root, profileMap);
    }

    private static void Reassign(TreeNode node, Dictionary<Guid, Guid> profileMap)
    {
        node.Id = Guid.NewGuid();
        if (node.Credentials.ProfileId is { } pid)
            node.Credentials.ProfileId = profileMap.TryGetValue(pid, out var mapped) ? mapped : null;
        if (node is GroupNode g) foreach (var c in g.Children) Reassign(c, profileMap);
    }
}
