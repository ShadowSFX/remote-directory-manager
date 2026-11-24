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

    public const int MaxSnippets = 500;
    public const int MaxForwardsPerServer = 32;

    /// <summary>Repairs nulls/duplicates from hand-edited or older files and upgrades the schema.</summary>
    public static void Normalize(ConnectionDocument doc)
    {
        doc.Root ??= new GroupNode { Name = "root" };
        doc.Profiles ??= new List<CredentialProfile>();
        doc.SmartGroups ??= new List<SmartGroup>();
        doc.SmartGroups.RemoveAll(g => g is null);
        var seenSmart = new HashSet<Guid>();
        foreach (var g in doc.SmartGroups)
        {
            if (g.Id == Guid.Empty || !seenSmart.Add(g.Id)) { g.Id = Guid.NewGuid(); seenSmart.Add(g.Id); }
            Search.SmartGroupEvaluator.Normalize(g);
        }
        if (doc.SmartGroups.Count > 100) doc.SmartGroups = doc.SmartGroups.Take(100).ToList();
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

        doc.SshKeys ??= new List<SshKeyEntry>();
        doc.SshKeys.RemoveAll(k => k is null);
        var seenKeys = new HashSet<Guid>();
        foreach (var k in doc.SshKeys)
        {
            if (k.Id == Guid.Empty || !seenKeys.Add(k.Id)) { k.Id = Guid.NewGuid(); seenKeys.Add(k.Id); }
            k.Name ??= "";
            k.Algorithm ??= "";
            k.Fingerprint ??= "";
        }

        doc.Snippets ??= new List<Snippet>();
        doc.Snippets.RemoveAll(sn => sn is null);
        var seenSnippets = new HashSet<Guid>();
        foreach (var sn in doc.Snippets)
        {
            if (sn.Id == Guid.Empty || !seenSnippets.Add(sn.Id)) { sn.Id = Guid.NewGuid(); seenSnippets.Add(sn.Id); }
            sn.Name ??= "";
            sn.Body ??= "";
        }
        if (doc.Snippets.Count > MaxSnippets) doc.Snippets = doc.Snippets.Take(MaxSnippets).ToList();

        // v1 → v2: servers gain Files/Desktop/DefaultAction/DesktopCredentials/Rdp. The defaults of those
        // properties (Files = true, no desktop, inherit everything) are exactly the v1 behavior, so the
        // migration is the normalization above; nothing else to rewrite.
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
        node.DesktopCredentials ??= new CredentialSettings();
        node.Rdp ??= new RdpProfile();
        node.SshAuth ??= new SshAuthSettings();
        node.Ssh ??= new SshProfile();
        if (node.Rdp.ColorDepth is { } depth && depth is not (16 or 24 or 32)) node.Rdp.ColorDepth = null;
        if (node.Rdp.Gateway is { } gateway)
        {
            if (!Enum.IsDefined(gateway.Usage)) gateway.Usage = RdpGatewayUsage.Always;
            if (!Enum.IsDefined(gateway.Logon)) gateway.Logon = RdpGatewayLogon.SameAsServer;
            gateway.Host = (gateway.Host ?? "").Trim();
            gateway.Username = string.IsNullOrWhiteSpace(gateway.Username) ? null : gateway.Username.Trim();
            if (gateway.Usage != RdpGatewayUsage.Off && gateway.Host.Length == 0) node.Rdp.Gateway = null; // nothing to use
        }
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
                if (s.Desktop is { } d && !Rdp.HostPort.IsValidPort(d.Port)) d.Port = DesktopEndpoint.DefaultPort;
                if (s.Terminal is { } t)
                {
                    if (t.Port is { } tp && !Rdp.HostPort.IsValidPort(tp)) t.Port = null;
                    t.Forwards ??= new List<SshForward>();
                    t.Forwards.RemoveAll(f => f is null || !Rdp.HostPort.IsValidPort(f.BindPort)
                        || f.Kind != SshForwardKind.Dynamic && (string.IsNullOrWhiteSpace(f.TargetHost) || !Rdp.HostPort.IsValidPort(f.TargetPort)));
                    if (t.Forwards.Count > MaxForwardsPerServer) t.Forwards = t.Forwards.Take(MaxForwardsPerServer).ToList();
                }
                // A server must offer at least one thing; an older or hand-edited file may disable all of them.
                if (!s.Files && !s.HasDesktop && !s.HasTerminal) s.Files = true;
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
            foreach (var k in copy.SshKeys)
            {
                k.ProtectedKey = null;
                k.ProtectedPassphrase = null;
            }
            StripSecrets(copy.Root);
        }
        return copy;
    }

    private static void StripSecrets(TreeNode node)
    {
        node.Credentials.ProtectedPassword = null;
        node.DesktopCredentials.ProtectedPassword = null;
        if (node.Rdp?.Gateway is { } gw) gw.ProtectedPassword = null;
        node.SshAuth.ProtectedPassword = null;
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
        var keyMap = new Dictionary<Guid, Guid>();
        foreach (var k in doc.SshKeys)
        {
            var newId = Guid.NewGuid();
            keyMap[k.Id] = newId;
            k.Id = newId;
        }
        var nodeMap = new Dictionary<Guid, Guid>();
        Reassign(doc.Root, profileMap, keyMap, nodeMap);
        foreach (var g in doc.SmartGroups) g.Id = Guid.NewGuid();
        foreach (var sn in doc.Snippets)
        {
            sn.Id = Guid.NewGuid();
            if (sn.GroupId is { } gid) sn.GroupId = nodeMap.TryGetValue(gid, out var mg) ? mg : null;
        }
    }

    private static void Reassign(TreeNode node, Dictionary<Guid, Guid> profileMap, Dictionary<Guid, Guid> keyMap, Dictionary<Guid, Guid> nodeMap)
    {
        var id = Guid.NewGuid();
        nodeMap[node.Id] = id;
        node.Id = id;
        if (node.SshAuth.ProfileId is { } spid)
            node.SshAuth.ProfileId = profileMap.TryGetValue(spid, out var smapped) ? smapped : null;
        if (node.SshAuth.KeyId is { } kid)
            node.SshAuth.KeyId = keyMap.TryGetValue(kid, out var kmapped) ? kmapped : null;
        if (node.Credentials.ProfileId is { } pid)
            node.Credentials.ProfileId = profileMap.TryGetValue(pid, out var mapped) ? mapped : null;
        if (node.DesktopCredentials.ProfileId is { } dpid)
            node.DesktopCredentials.ProfileId = profileMap.TryGetValue(dpid, out var dmapped) ? dmapped : null;
        if (node is GroupNode g) foreach (var c in g.Children) Reassign(c, profileMap, keyMap, nodeMap);
    }
}
