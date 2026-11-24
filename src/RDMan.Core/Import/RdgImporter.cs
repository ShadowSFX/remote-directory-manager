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
    /// <summary>Credentials RDMan will ask for: undecryptable passwords, or profiles that live outside the file.</summary>
    public int PasswordsToReenter { get; set; }
    /// <summary>Smart-group rules that used regular expressions and were turned into plain text.</summary>
    public int RulesSimplified { get; set; }
    /// <summary>Settings RDMan doesn't have (gateway, start program…) found and skipped.</summary>
    public int SettingsSkipped { get; set; }
    /// <summary>The file was bigger or deeper than RDMan reads; the rest was left out.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Reads Remote Desktop Connection Manager (.rdg) files — schema 3 (RDCMan 2.7+, Sysinternals) and the older 2.2
/// layout. Parsing is hardened: no DTDs, no external entities, a size cap. Passwords (DPAPI, current user) are handed
/// to <c>unprotect</c> and re-protected with <c>protect</c>; nothing is kept in clear text.
/// </summary>
public static class RdgImporter
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    private const int MaxServersAndGroups = 5000;
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
        // Credential profiles stored in the file. Passwords are decrypted only for profiles something uses.
        foreach (var p in file.Element("credentialsProfiles")?.Elements("credentialsProfile") ?? [])
        {
            var name = p.Element("profileName")?.Value?.Trim();
            if (string.IsNullOrEmpty(name) || ctx.FileProfiles.ContainsKey(name)) continue; // first definition wins
            ctx.FileProfiles[name] = p;
        }

        var root = new GroupNode { Name = NameOf(file) ?? fallbackName };
        var result = new RdgImportResult { Root = root };
        var rootPort = ApplySettings(file, root, ctx, inheritedPort: null);
        ReadChildren(file, root, ctx, result, 0, rootPort);
        result.Profiles.AddRange(ctx.Profiles.Values);
        // Smart groups from this file only search the servers that came with it.
        foreach (var g in result.SmartGroups) g.ScopeId = root.Id;
        result.PasswordsRecovered = ctx.Recovered;
        result.PasswordsToReenter = ctx.Lost;
        result.RulesSimplified = ctx.Simplified;
        result.SettingsSkipped = ctx.Skipped;
        result.Truncated = ctx.Truncated;
        return result;
    }

    private sealed class Context(Func<byte[], string?> unprotect, Func<string, string> protect)
    {
        public Dictionary<string, XElement> FileProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Profiles turned into RDMan credential profiles, by name (created on first use).</summary>
        public Dictionary<string, CredentialProfile> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Recovered, Lost, Simplified, Skipped, Nodes;
        public bool Truncated;

        public CredentialProfile Profile(string name)
        {
            if (Profiles.TryGetValue(name, out var existing)) return existing;
            var p = FileProfiles[name];
            var profile = new CredentialProfile
            {
                Name = name,
                Username = Join(p.Element("domain")?.Value, p.Element("userName")?.Value) ?? "",
                ProtectedPassword = Password(p.Element("password")),
            };
            Profiles[name] = profile;
            return profile;
        }

        /// <summary>
        /// RDCMan stores Base64(DPAPI(UTF-16 text)), or the plain text when <c>storeAsClearText="True"</c> (2.2).
        /// Returns RDMan's protected form, or null (counted as "re-enter").
        /// </summary>
        public string? Password(XElement? el)
        {
            var value = el?.Value;
            if (string.IsNullOrEmpty(value)) return null;
            string? clear = null;
            if (string.Equals(el!.Attribute("storeAsClearText")?.Value, "True", StringComparison.OrdinalIgnoreCase))
                clear = value;
            else
            {
                try { clear = unprotect(Convert.FromBase64String(value.Trim())); }
                catch (FormatException) { }
            }
            if (clear is null)
            {
                Lost++;
                return null;
            }
            Recovered++;
            return protect(clear);
        }
    }

    private static void ReadChildren(XElement parent, GroupNode into, Context ctx, RdgImportResult result, int depth, int? inheritedPort)
    {
        if (depth > MaxDepth)
        {
            ctx.Truncated = true;
            return;
        }
        foreach (var el in parent.Elements())
        {
            var kind = el.Name.LocalName;
            if (kind is not ("group" or "server" or "smartGroup")) continue;
            if (++ctx.Nodes > MaxServersAndGroups)
            {
                ctx.Truncated = true;
                return;
            }
            switch (kind)
            {
                case "group":
                    var group = new GroupNode
                    {
                        Name = NameOf(el) ?? "Group",
                        IsExpanded = !string.Equals(PropertyOf(el, "expanded"), "False", StringComparison.OrdinalIgnoreCase),
                    };
                    var groupPort = ApplySettings(el, group, ctx, inheritedPort);
                    into.Children.Add(group);
                    result.Groups++;
                    ReadChildren(el, group, ctx, result, depth + 1, groupPort);
                    break;
                case "server":
                    var host = PropertyOf(el, "name");
                    if (string.IsNullOrWhiteSpace(host)) break;
                    var (h, namePort) = SplitPort(host.Trim());
                    var server = new ServerNode
                    {
                        Name = PropertyOf(el, "displayName") is { Length: > 0 } display ? display : h,
                        Host = h,
                        Description = PropertyOf(el, "comment") is { Length: > 0 } c ? c : null,
                        Files = false,
                        Desktop = new DesktopEndpoint { Enabled = true },
                        DefaultAction = ServerAction.Desktop,
                    };
                    // Port: the server's own connection settings, else "host:port", else what its groups set.
                    var port = ApplySettings(el, server, ctx, inheritedPort: null) ?? namePort ?? inheritedPort;
                    if (port is { } p) server.Desktop.Port = p;
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
    /// <summary>Applies the node's own settings; returns the RDP port in effect for its children.</summary>
    private static int? ApplySettings(XElement el, TreeNode node, Context ctx, int? inheritedPort)
    {
        var port = inheritedPort;
        if (Own(el, "logonCredentials") is { } cred) node.Credentials = Credentials(cred, ctx);

        if (Own(el, "connectionSettings") is { } conn)
        {
            if (int.TryParse(conn.Element("port")?.Value, out var p) && p is > 0 and <= 65535) port = p;
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
            node.Rdp.ServerCheck = (sec.Element("authentication")?.Value ?? "").Trim() switch
            {
                "None" or "0" => RdpServerCheck.Skip,
                "Required" or "Require" or "1" => RdpServerCheck.Require,
                "Warn" or "2" => RdpServerCheck.Warn,
                _ => node.Rdp.ServerCheck,
            };
        }
        if (Own(el, "gatewaySettings") is { } gw && Bool(gw.Element("enabled")) == true) ctx.Skipped++;
        return node is ServerNode ? (port != inheritedPort ? port : null) : port;
    }

    private static CredentialSettings Credentials(XElement cred, Context ctx)
    {
        var nameEl = cred.Element("profileName");
        var profileName = nameEl?.Value?.Trim() ?? "";
        var scope = nameEl?.Attribute("scope")?.Value ?? "";
        var isCustom = profileName.Length == 0 || string.Equals(profileName, "Custom", StringComparison.OrdinalIgnoreCase);
        if (!isCustom)
        {
            // Scope "File" (or none, 2.2) = a profile defined in this file. "Local"/"Global" profiles live in the
            // user's RDCMan settings, not in the file.
            if (!string.Equals(scope, "Local", StringComparison.OrdinalIgnoreCase) && !string.Equals(scope, "Global", StringComparison.OrdinalIgnoreCase)
                && ctx.FileProfiles.ContainsKey(profileName))
                return new CredentialSettings { Mode = CredentialMode.Profile, ProfileId = ctx.Profile(profileName).Id };
            // A profile RDMan can't see. Never fall back to the parent's account for this server: ask instead.
            ctx.Lost++;
            return new CredentialSettings { Mode = CredentialMode.Custom, Username = "" };
        }
        var user = Join(cred.Element("domain")?.Value, cred.Element("userName")?.Value);
        if (user is null) return new CredentialSettings { Mode = CredentialMode.Inherit };
        return new CredentialSettings
        {
            Mode = CredentialMode.Custom,
            Username = user,
            ProtectedPassword = ctx.Password(cred.Element("password")),
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
        var rules = (ruleGroup?.Elements("rule") ?? []).ToList();
        foreach (var rule in rules)
        {
            var value = rule.Element("value")?.Value ?? "";
            var field = (rule.Element("property")?.Value ?? "").Trim() switch
            {
                "DisplayName" => SmartField.Name,
                "Server" or "ServerName" => SmartField.Host,
                "Comment" => SmartField.Notes,
                "Parent" => SmartField.Group,
                _ => SmartField.Any,
            };
            var op = string.Equals(rule.Element("operator")?.Value?.Trim(), "DoesNotMatch", StringComparison.OrdinalIgnoreCase) ? SmartOp.NotContains : SmartOp.Contains;
            // "web|db": alternatives become separate rules when that keeps the meaning (a single "matches" rule).
            var alternatives = SplitAlternatives(value);
            if (alternatives.Count > 1 && rules.Count == 1 && op == SmartOp.Contains)
            {
                group.Match = SmartMatch.Any;
                ctx.Simplified++;
            }
            else if (alternatives.Count > 1)
            {
                alternatives = [alternatives[0]];
                ctx.Simplified++;
            }
            foreach (var alt in alternatives)
            {
                if (group.Rules.Count >= SmartGroup.MaxRules) break;
                var plain = Unregex(alt);
                if (plain != alt) ctx.Simplified++;
                if (plain.Length == 0) continue;
                group.Rules.Add(new SmartRule { Field = field, Op = op, Value = plain });
            }
        }
        return group;
    }

    private static List<string> SplitAlternatives(string pattern)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\') { i++; continue; }
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == '|' && depth == 0)
            {
                parts.Add(pattern[start..i]);
                start = i + 1;
            }
        }
        parts.Add(pattern[start..]);
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    /// <summary>
    /// RDCMan rules are regular expressions; keep the literal text: "^web.*" → "web", "srv01.contoso.com" stays,
    /// "web[0-9]+" → "web", "db\.prod" → "db.prod".
    /// </summary>
    public static string Unregex(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\' when i + 1 < pattern.Length:
                    var next = pattern[++i];
                    if (!char.IsLetterOrDigit(next)) sb.Append(next); // \. → .   (\d, \w… are classes: dropped)
                    break;
                case '[':
                    // A character class matches one unknown character: drop it (and its quantifier).
                    var close = pattern.IndexOf(']', i + 1);
                    i = close < 0 ? pattern.Length : close;
                    break;
                case '.' when i + 1 < pattern.Length && pattern[i + 1] is '*' or '+' or '?':
                    break; // ".*" = anything
                case '.':
                    sb.Append('.'); // a literal dot in a host name, most of the time
                    break;
                case '{':
                    var end = pattern.IndexOf('}', i + 1);
                    i = end < 0 ? pattern.Length : end; // {2,3} quantifier
                    break;
                case '^' or '$' or '*' or '+' or '?' or '(' or ')' or '|' or ']' or '}':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    // ───────────── XML helpers (schema 3 keeps fields under <properties>, 2.2 puts them inline) ─────────────
    private static string? PropertyOf(XElement el, string name)
        => (el.Element("properties")?.Element(name) ?? el.Element(name))?.Value?.Trim();

    private static string? NameOf(XElement el) => PropertyOf(el, "name") is { Length: > 0 } n ? n : null;

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

    /// <summary>"host:3390" or "[fe80::1]:3390" (RDCMan allows the port in the name); bare IPv6 keeps its colons.</summary>
    private static (string Host, int? Port) SplitPort(string host)
    {
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close > 1)
            {
                var inner = host[1..close];
                var rest = host[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var bp) && bp is > 0 and <= 65535) return (inner, bp);
                return (inner, null);
            }
        }
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p) && p is > 0 and <= 65535)
            return (host[..colon], p);
        return (host, null);
    }
}
