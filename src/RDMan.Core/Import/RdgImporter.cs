using System.Text;
using System.Xml;
using System.Xml.Linq;
using RDMan.Core.Models;

namespace RDMan.Core.Import;

/// <summary>What an RDCMan file turned into, plus what couldn't be carried over.</summary>
public sealed class RdgImportResult
{
    public required GroupNode Root { get; init; }
    public List<CredentialProfile> Profiles { get; } = new();
    public List<SmartGroup> SmartGroups { get; } = new();
    public int Servers { get; set; }
    public int Groups { get; set; }
    /// <summary>Credentials (on servers, groups or profiles) that came with a password RDMan could decrypt.</summary>
    public int PasswordsRecovered { get; set; }
    /// <summary>Credentials whose password couldn't be decrypted (other PC/user, certificate encryption): re-enter them.</summary>
    public int PasswordsToReenter { get; set; }
    /// <summary>Smart-group rules that used regular expressions and were turned into plain "contains" text.</summary>
    public int RulesSimplified { get; set; }
    /// <summary>Settings RDMan doesn't have (gateway, start program…) found and skipped.</summary>
    public int SettingsSkipped { get; set; }
}

/// <summary>
/// Reads Remote Desktop Connection Manager (.rdg) files — schema 3 (RDCMan 2.7+, Sysinternals) and the older 2.2
/// layout. Parsing is hardened: no DTDs, no external entities, a size cap. Passwords (DPAPI, current user) are handed
/// to <c>unprotect</c> and re-protected with <c>protect</c>; nothing is ever kept in clear text.
/// </summary>
public static class RdgImporter
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxNodes = 5000;
    private const int MaxDepth = 30;

    public static RdgImportResult Parse(Stream stream, string fallbackName, Func<byte[], string?> unprotect, Func<string, string> protect)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxFileBytes,
            IgnoreComments = true,
        };
        XDocument xml;
        using (var reader = XmlReader.Create(stream, settings)) xml = XDocument.Load(reader, LoadOptions.None);
        var rdc = xml.Root is { Name.LocalName: "RDCMan" } r ? r : throw new FormatException("Not an RDCMan file.");
        var file = rdc.Element("file") ?? throw new FormatException("The file has no <file> element.");

        var ctx = new Context(unprotect, protect);
        // Credential profiles stored in the file (scope "File").
        foreach (var p in file.Element("credentialsProfiles")?.Elements("credentialsProfile") ?? [])
        {
            var name = p.Element("profileName")?.Value?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var profile = new CredentialProfile
            {
                Name = name,
                Username = Join(p.Element("domain")?.Value, p.Element("userName")?.Value) ?? "",
                ProtectedPassword = ctx.Password(p.Element("password")?.Value),
            };
            ctx.Profiles[name] = profile;
        }

        var root = new GroupNode { Name = NameOf(file) ?? fallbackName };
        var result = new RdgImportResult { Root = root };
        ApplySettings(file, root, ctx);
        ReadChildren(file, root, ctx, result, 0);
        result.Profiles.AddRange(ctx.Profiles.Values.Where(p => ctx.UsedProfiles.Contains(p)));
        result.PasswordsRecovered = ctx.Recovered;
        result.PasswordsToReenter = ctx.Lost;
        result.RulesSimplified = ctx.Simplified;
        result.SettingsSkipped = ctx.Skipped;
        return result;
    }

    private sealed class Context(Func<byte[], string?> unprotect, Func<string, string> protect)
    {
        public Dictionary<string, CredentialProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<CredentialProfile> UsedProfiles { get; } = new();
        public int Recovered, Lost, Simplified, Skipped, Nodes;

        /// <summary>RDCMan stores Base64(DPAPI(UTF-16 text)). Returns RDMan's protected form, or null.</summary>
        public string? Password(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;
            string? clear = null;
            try
            {
                var bytes = Convert.FromBase64String(base64.Trim());
                clear = unprotect(bytes);
            }
            catch (FormatException) { }
            if (clear is null)
            {
                Lost++;
                return null;
            }
            Recovered++;
            return protect(clear);
        }
    }

    private static void ReadChildren(XElement parent, GroupNode into, Context ctx, RdgImportResult result, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var el in parent.Elements())
        {
            if (++ctx.Nodes > MaxNodes) return;
            switch (el.Name.LocalName)
            {
                case "group":
                    var group = new GroupNode
                    {
                        Name = NameOf(el) ?? "Group",
                        IsExpanded = !string.Equals(PropertyOf(el, "expanded"), "False", StringComparison.OrdinalIgnoreCase),
                    };
                    ApplySettings(el, group, ctx);
                    into.Children.Add(group);
                    result.Groups++;
                    ReadChildren(el, group, ctx, result, depth + 1);
                    break;
                case "server":
                    var host = HostOf(el);
                    if (string.IsNullOrWhiteSpace(host)) break;
                    var (h, port) = SplitPort(host.Trim());
                    var server = new ServerNode
                    {
                        Name = PropertyOf(el, "displayName") is { Length: > 0 } display ? display : h,
                        Host = h,
                        Description = PropertyOf(el, "comment") is { Length: > 0 } c ? c : null,
                        Files = false,
                        Desktop = new DesktopEndpoint { Enabled = true, Port = port ?? DesktopEndpoint.DefaultPort },
                        DefaultAction = ServerAction.Desktop,
                    };
                    ApplySettings(el, server, ctx);
                    into.Children.Add(server);
                    result.Servers++;
                    break;
                case "smartGroup":
                    result.SmartGroups.Add(ReadSmartGroup(el, ctx));
                    break;
            }
        }
    }

    // ───────────── Settings (inherit="None" means "set here") ─────────────
    private static void ApplySettings(XElement el, TreeNode node, Context ctx)
    {
        if (Own(el, "logonCredentials") is { } cred) node.Credentials = Credentials(cred, ctx);

        if (Own(el, "connectionSettings") is { } conn)
        {
            if (node is ServerNode s && int.TryParse(conn.Element("port")?.Value, out var port) && port is > 0 and <= 65535 && s.Desktop is not null)
                s.Desktop.Port = port;
            if (Bool(conn.Element("connectToConsole")) is { } console) node.Rdp.AdminSession = console;
            if (!string.IsNullOrWhiteSpace(conn.Element("startProgram")?.Value)) ctx.Skipped++;
        }
        if (Own(el, "localResources") is { } res)
        {
            node.Rdp.RedirectClipboard = Bool(res.Element("redirectClipboard")) ?? node.Rdp.RedirectClipboard;
            node.Rdp.RedirectDrives = Bool(res.Element("redirectDrives")) ?? node.Rdp.RedirectDrives;
            node.Rdp.RedirectPrinters = Bool(res.Element("redirectPrinters")) ?? node.Rdp.RedirectPrinters;
            node.Rdp.RedirectSmartCards = Bool(res.Element("redirectSmartCards")) ?? node.Rdp.RedirectSmartCards;
            node.Rdp.Audio = (res.Element("audioRedirection")?.Value ?? "").Trim() switch
            {
                "Client" or "0" => RdpAudio.Local,
                "Remote" or "1" => RdpAudio.Remote,
                "NoSound" or "2" => RdpAudio.Off,
                _ => node.Rdp.Audio,
            };
            node.Rdp.WindowsKeysToServer = (res.Element("keyboardHook")?.Value ?? "").Trim() switch
            {
                "Remote" or "1" => true,
                "Client" or "0" => false,
                _ => node.Rdp.WindowsKeysToServer, // full screen only: RDMan's default
            };
        }
        if (Own(el, "remoteDesktop") is { } desk && int.TryParse(desk.Element("colorDepth")?.Value, out var depth) && depth is 16 or 24 or 32)
            node.Rdp.ColorDepth = depth;
        if (Own(el, "securitySettings") is { } sec)
        {
            var auth = (sec.Element("authentication")?.Value ?? "").Trim();
            node.Rdp.ServerCheck = auth switch
            {
                "None" or "0" => RdpServerCheck.Skip,
                "Required" or "Require" or "1" => RdpServerCheck.Require,
                "Warn" or "2" => RdpServerCheck.Warn,
                _ => node.Rdp.ServerCheck,
            };
        }
        if (Own(el, "gatewaySettings") is { } gw && Bool(gw.Element("enabled")) == true) ctx.Skipped++;
    }

    private static CredentialSettings Credentials(XElement cred, Context ctx)
    {
        var profileName = cred.Element("profileName")?.Value?.Trim() ?? "";
        var scope = cred.Element("profileName")?.Attribute("scope")?.Value ?? "";
        var user = Join(cred.Element("domain")?.Value, cred.Element("userName")?.Value);
        // A named profile from this file.
        if (profileName.Length > 0 && !string.Equals(profileName, "Custom", StringComparison.OrdinalIgnoreCase)
            && ctx.Profiles.TryGetValue(profileName, out var profile))
        {
            ctx.UsedProfiles.Add(profile);
            return new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = profile.Id };
        }
        // Custom (or a profile from RDCMan's own global store, which isn't in the file): keep what's written here.
        if (user is null)
        {
            if (scope.Length > 0 && profileName.Length > 0 && !string.Equals(profileName, "Custom", StringComparison.OrdinalIgnoreCase)) ctx.Skipped++;
            return new CredentialSettings { Mode = CredentialMode.Inherit };
        }
        return new CredentialSettings
        {
            Mode = CredentialMode.Custom,
            Username = user,
            ProtectedPassword = ctx.Password(cred.Element("password")?.Value),
        };
    }

    // ───────────── Smart groups ─────────────
    private static SmartGroup ReadSmartGroup(XElement el, Context ctx)
    {
        var ruleGroup = el.Element("ruleGroup");
        var group = new SmartGroup
        {
            Name = NameOf(el) ?? "Smart group",
            Match = string.Equals(ruleGroup?.Attribute("operator")?.Value, "Any", StringComparison.OrdinalIgnoreCase) ? SmartMatch.Any : SmartMatch.All,
        };
        foreach (var rule in ruleGroup?.Elements("rule") ?? [])
        {
            if (group.Rules.Count >= SmartGroup.MaxRules) break;
            var value = rule.Element("value")?.Value ?? "";
            var plain = Unregex(value);
            if (plain != value) ctx.Simplified++;
            if (plain.Length == 0) continue;
            group.Rules.Add(new SmartRule
            {
                Field = (rule.Element("property")?.Value ?? "").Trim() switch
                {
                    "DisplayName" => SmartField.Name,
                    "Server" or "ServerName" => SmartField.Host,
                    "Comment" => SmartField.Notes,
                    "Parent" => SmartField.Group,
                    _ => SmartField.Any,
                },
                Op = string.Equals(rule.Element("operator")?.Value?.Trim(), "DoesNotMatch", StringComparison.OrdinalIgnoreCase) ? SmartOp.NotContains : SmartOp.Contains,
                Value = plain,
            });
        }
        return group;
    }

    /// <summary>RDCMan rules are regular expressions; keep the literal text ("^web.*" → "web").</summary>
    public static string Unregex(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[++i];
                if (!char.IsLetterOrDigit(next)) sb.Append(next); // \. → .   (\d, \w… are classes: dropped)
                continue;
            }
            if ("^$.*+?()[]{}|".IndexOf(c) >= 0) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // ───────────── XML helpers (schema 3 keeps fields under <properties>, 2.2 puts them inline) ─────────────
    private static string? PropertyOf(XElement el, string name)
        => (el.Element("properties")?.Element(name) ?? el.Element(name))?.Value?.Trim();

    private static string? NameOf(XElement el) => PropertyOf(el, "name") is { Length: > 0 } n ? n : null;

    private static string? HostOf(XElement server) => PropertyOf(server, "name");

    /// <summary>A setting block defined on this node (not inherited from its parent).</summary>
    private static XElement? Own(XElement el, string name)
    {
        var block = el.Element(name) ?? el.Element("properties")?.Element(name);
        if (block is null) return null;
        var inherit = block.Attribute("inherit")?.Value;
        return inherit is null || string.Equals(inherit, "None", StringComparison.OrdinalIgnoreCase) ? block : null;
    }

    private static bool? Bool(XElement? el) => el?.Value?.Trim() switch
    {
        "True" or "true" or "1" => true,
        "False" or "false" or "0" => false,
        _ => null,
    };

    private static string? Join(string? domain, string? user)
    {
        user = user?.Trim();
        domain = domain?.Trim();
        if (string.IsNullOrEmpty(user)) return null;
        if (string.IsNullOrEmpty(domain) || user.Contains('\\') || user.Contains('@')) return user;
        return $@"{domain}\{user}";
    }

    /// <summary>"host:3390" (RDCMan allows the port in the name); IPv6 literals keep their colons.</summary>
    private static (string Host, int? Port) SplitPort(string host)
    {
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p) && p is > 0 and <= 65535)
            return (host[..colon], p);
        return (host, null);
    }
}
