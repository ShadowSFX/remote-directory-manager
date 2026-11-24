using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RDMan.Core.Ssh;

/// <summary>
/// What <c>ssh &lt;alias&gt;</c> would use for a host according to an OpenSSH client config
/// (first obtained value wins; IdentityFile, CertificateFile and the *Forward lines accumulate).
/// </summary>
public sealed class SshConfigResult
{
    /// <summary>The name that was looked up (what the user typed after <c>ssh</c>).</summary>
    public required string Alias { get; init; }
    /// <summary>HostName with %h expanded; null when the config doesn't set one (connect to <see cref="Alias"/>).</summary>
    public string? HostName { get; set; }
    public string? User { get; set; }
    public int? Port { get; set; }
    public List<string> IdentityFiles { get; } = new();
    public List<string> CertificateFiles { get; } = new();
    public bool? IdentitiesOnly { get; set; }
    /// <summary>Raw ProxyJump value; "none" disables jumping.</summary>
    public string? ProxyJump { get; set; }
    public int? ServerAliveInterval { get; set; }
    public int? ServerAliveCountMax { get; set; }
    public int? ConnectTimeout { get; set; }
    /// <summary>yes / no / ask / accept-new / off.</summary>
    public string? StrictHostKeyChecking { get; set; }
    public List<string> UserKnownHostsFiles { get; } = new();
    public bool? HashKnownHosts { get; set; }
    public bool? ForwardAgent { get; set; }
    public string? RemoteCommand { get; set; }
    public List<string> LocalForwards { get; } = new();
    public List<string> RemoteForwards { get; } = new();
    public List<string> DynamicForwards { get; } = new();
    /// <summary>True when at least one block other than a catch-all (<c>Host *</c>) matched.</summary>
    public bool MatchedSpecificHost { get; set; }
    /// <summary>Things RDMan ignored or couldn't follow (Match blocks, missing includes…), for the editor's preview.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>The address actually dialed.</summary>
    public string EffectiveHost => string.IsNullOrWhiteSpace(HostName) ? Alias : HostName!;
}

/// <summary>
/// OpenSSH client configuration (~/.ssh/config). Supports Host blocks with patterns (*, ?, !negation, comma or
/// space lists), Include (relative to ~/.ssh, globs, depth-limited), "keyword value" and "keyword=value",
/// quoted arguments and the %-tokens OpenSSH expands. Match blocks are not evaluated (they never apply).
/// </summary>
public sealed class SshConfig
{
    private const int MaxIncludeDepth = 16;

    private sealed record Entry(string[]? Patterns, bool IsMatchBlock, string Keyword, string[] Args, string Source);

    private readonly List<Entry> _entries = new();
    private readonly List<string> _notes = new();

    public string HomeDirectory { get; }
    public string? SourcePath { get; }

    /// <summary>Concrete host names (no wildcards, no negation) declared in Host lines, in file order.</summary>
    public IReadOnlyList<string> DeclaredHosts { get; private set; } = [];

    /// <summary>Problems found while reading (bad includes, Match blocks).</summary>
    public IReadOnlyList<string> Notes => _notes;

    private SshConfig(string home, string? source)
    {
        HomeDirectory = home;
        SourcePath = source;
    }

    public static string DefaultHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DefaultPath(string home) => Path.Combine(home, ".ssh", "config");

    /// <summary>An empty config (no file): every lookup returns the alias as-is.</summary>
    public static SshConfig Empty(string? home = null) => new(home ?? DefaultHome, null);

    /// <summary>Reads ~/.ssh/config (or <paramref name="path"/>); a missing or unreadable file gives an empty config.</summary>
    public static SshConfig Load(string? path = null, string? home = null)
    {
        home ??= DefaultHome;
        path ??= DefaultPath(home);
        var cfg = new SshConfig(home, path);
        if (File.Exists(path))
        {
            try { cfg.ReadFile(path, null, false, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cfg._notes.Add($"{path}: {ex.Message}");
            }
        }
        cfg.Finish();
        return cfg;
    }

    /// <summary>Parses config text (tests, previews). Include paths resolve against <paramref name="home"/>.</summary>
    public static SshConfig Parse(string text, string? home = null)
    {
        var cfg = new SshConfig(home ?? DefaultHome, null);
        cfg.ReadText(text, "config", null, false, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        cfg.Finish();
        return cfg;
    }

    private void Finish()
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[]? last = null;
        foreach (var e in _entries)
        {
            if (e.Patterns is null || ReferenceEquals(e.Patterns, last)) continue;
            last = e.Patterns;
            foreach (var p in e.Patterns)
                if (p.Length > 0 && p.IndexOfAny(['*', '?', '!']) < 0 && seen.Add(p)) hosts.Add(p);
        }
        DeclaredHosts = hosts;
    }

    private void ReadFile(string path, string[]? patterns, bool inMatch, int depth, HashSet<string> visiting)
    {
        var full = Path.GetFullPath(path);
        if (!visiting.Add(full))
        {
            _notes.Add($"Include cycle: {full}");
            return;
        }
        try { ReadText(File.ReadAllText(full), full, patterns, inMatch, depth, visiting); }
        finally { visiting.Remove(full); }
    }

    private void ReadText(string text, string source, string[]? patterns, bool inMatch, int depth, HashSet<string> visiting)
    {
        // A block opened inside an included file ends with that file (the caller's block continues after it).
        var currentPatterns = patterns;
        var currentMatch = inMatch;

        using var reader = new StringReader(text);
        string? raw;
        var lineNo = 0;
        while ((raw = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (!TrySplit(raw, out var keyword, out var args)) continue;
            var where = $"{Path.GetFileName(source)}:{lineNo}";

            switch (keyword)
            {
                case "host":
                    currentPatterns = args.SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                    currentMatch = false;
                    _entries.Add(new Entry(currentPatterns, false, "host", [], where));
                    continue;
                case "match":
                    // "Match all" is the only form we can honor without running commands or knowing the user.
                    if (args.Length == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        currentPatterns = ["*"];
                        currentMatch = false;
                    }
                    else
                    {
                        currentPatterns = [];
                        currentMatch = true;
                        _notes.Add($"{where}: Match blocks are not supported (ignored)");
                    }
                    continue;
                case "include":
                    if (depth >= MaxIncludeDepth)
                    {
                        _notes.Add($"{where}: Include nested too deeply");
                        continue;
                    }
                    foreach (var arg in args)
                        foreach (var file in ExpandInclude(arg))
                            ReadFile(file, currentPatterns, currentMatch, depth + 1, visiting);
                    continue;
            }

            _entries.Add(new Entry(currentPatterns, currentMatch, keyword, args, where));
        }
    }

    private IEnumerable<string> ExpandInclude(string arg)
    {
        var path = ExpandTilde(arg);
        if (!Path.IsPathRooted(path)) path = Path.Combine(HomeDirectory, ".ssh", path);
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        if (name.IndexOfAny(['*', '?']) < 0)
            return File.Exists(path) ? [path] : [];
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory.EnumerateFiles(dir, name).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notes.Add($"Include {arg}: {ex.Message}");
            return [];
        }
    }

    /// <summary>"Keyword value…" or "Keyword=value"; comments and blank lines return false. Keyword is lower-case.</summary>
    internal static bool TrySplit(string line, out string keyword, out string[] args)
    {
        keyword = "";
        args = [];
        var s = line.Trim();
        if (s.Length == 0 || s[0] == '#') return false;

        var i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '=') i++;
        keyword = s[..i].ToLowerInvariant();
        if (keyword.Length == 0) return false;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i < s.Length && s[i] == '=') i++;

        var list = new List<string>();
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            if (s[i] == '#') break; // trailing comment
            sb.Clear();
            if (s[i] == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"') sb.Append(s[i++]);
                i++; // closing quote (or end)
            }
            else
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i])) sb.Append(s[i++]);
            }
            list.Add(sb.ToString());
        }
        args = [.. list];
        return true;
    }

    /// <summary>The settings <c>ssh &lt;alias&gt;</c> would use.</summary>
    public SshConfigResult Resolve(string alias, string? localUser = null)
    {
        var r = new SshConfigResult { Alias = alias };
        r.Notes.AddRange(_notes);
        localUser ??= Environment.UserName;

        foreach (var e in _entries)
        {
            if (e.Keyword == "host" || e.IsMatchBlock) continue;
            if (e.Patterns is not null && !HostMatches(e.Patterns, alias)) continue;
            if (e.Patterns is not null && !e.Patterns.All(p => p == "*")) r.MatchedSpecificHost = true;
            Apply(r, e);
        }

        if (r.HostName is not null) r.HostName = ExpandTokens(r.HostName, alias, r, localUser, hostOnly: true);

        // Tokens in paths use the final HostName/User/Port, so expand after everything is known.
        for (var i = 0; i < r.IdentityFiles.Count; i++) r.IdentityFiles[i] = ExpandPath(r.IdentityFiles[i], alias, r, localUser);
        for (var i = 0; i < r.CertificateFiles.Count; i++) r.CertificateFiles[i] = ExpandPath(r.CertificateFiles[i], alias, r, localUser);
        for (var i = 0; i < r.UserKnownHostsFiles.Count; i++) r.UserKnownHostsFiles[i] = ExpandPath(r.UserKnownHostsFiles[i], alias, r, localUser);
        return r;
    }

    private static void Apply(SshConfigResult r, Entry e)
    {
        var a = e.Args;
        var first = a.Length > 0 ? a[0] : "";
        switch (e.Keyword)
        {
            case "hostname": r.HostName ??= first; break;
            case "user": r.User ??= first; break;
            case "port": if (r.Port is null && TryInt(first, out var port) && port is > 0 and <= 65535) r.Port = port; break;
            case "identityfile":
                if (first.Length > 0 && !first.Equals("none", StringComparison.OrdinalIgnoreCase)) r.IdentityFiles.Add(first);
                break;
            case "certificatefile": if (first.Length > 0) r.CertificateFiles.Add(first); break;
            case "identitiesonly": r.IdentitiesOnly ??= YesNo(first); break;
            case "proxyjump": r.ProxyJump ??= first; break;
            case "serveraliveinterval": if (r.ServerAliveInterval is null && TryInt(first, out var sai) && sai >= 0) r.ServerAliveInterval = sai; break;
            case "serveralivecountmax": if (r.ServerAliveCountMax is null && TryInt(first, out var sac) && sac > 0) r.ServerAliveCountMax = sac; break;
            case "connecttimeout": if (r.ConnectTimeout is null && TryInt(first, out var ct) && ct > 0) r.ConnectTimeout = ct; break;
            case "stricthostkeychecking": r.StrictHostKeyChecking ??= first.ToLowerInvariant(); break;
            case "userknownhostsfile":
                if (r.UserKnownHostsFiles.Count == 0)
                    r.UserKnownHostsFiles.AddRange(a.Where(x => !x.Equals("none", StringComparison.OrdinalIgnoreCase)));
                break;
            case "hashknownhosts": r.HashKnownHosts ??= YesNo(first); break;
            case "forwardagent": r.ForwardAgent ??= YesNo(first); break;
            case "remotecommand": r.RemoteCommand ??= string.Join(' ', a); break;
            case "localforward": if (a.Length > 0) r.LocalForwards.Add(string.Join(' ', a)); break;
            case "remoteforward": if (a.Length > 0) r.RemoteForwards.Add(string.Join(' ', a)); break;
            case "dynamicforward": if (a.Length > 0) r.DynamicForwards.Add(first); break;
        }
    }

    private static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);

    private static bool? YesNo(string s) => s.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };

    /// <summary>
    /// OpenSSH pattern-list semantics: the list matches when at least one positive pattern matches and no negated
    /// pattern (<c>!pattern</c>) does. Matching is case-insensitive with * and ? wildcards.
    /// </summary>
    public static bool HostMatches(IEnumerable<string> patterns, string host)
    {
        var positive = false;
        foreach (var raw in patterns)
        {
            if (raw.Length == 0) continue;
            if (raw[0] == '!')
            {
                if (Wildcard(raw[1..], host)) return false;
            }
            else if (Wildcard(raw, host))
            {
                positive = true;
            }
        }
        return positive;
    }

    public static bool Wildcard(string pattern, string text)
    {
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string ExpandTilde(string path)
    {
        if (path == "~") return HomeDirectory;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(HomeDirectory, path[2..]);
        return path;
    }

    private string ExpandPath(string path, string alias, SshConfigResult r, string localUser)
    {
        var p = ExpandTilde(ExpandTokens(path, alias, r, localUser, hostOnly: false));
        // OpenSSH on Windows accepts forward slashes; normalize for File APIs.
        return Path.IsPathRooted(p) || p.Length == 0 ? p.Replace('/', Path.DirectorySeparatorChar) : Path.Combine(HomeDirectory, ".ssh", p);
    }

    /// <summary>%h host, %p port, %r remote user, %u local user, %d home, %n original alias, %%.</summary>
    private string ExpandTokens(string value, string alias, SshConfigResult r, string localUser, bool hostOnly)
    {
        if (value.IndexOf('%') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '%' || i + 1 >= value.Length) { sb.Append(c); continue; }
            var t = value[++i];
            switch (t)
            {
                case '%': sb.Append('%'); break;
                case 'h': sb.Append(hostOnly ? alias : r.EffectiveHost); break;
                case 'n': sb.Append(alias); break;
                case 'p' when !hostOnly: sb.Append((r.Port ?? 22).ToString(CultureInfo.InvariantCulture)); break;
                case 'r' when !hostOnly: sb.Append(r.User ?? localUser); break;
                case 'u' when !hostOnly: sb.Append(localUser); break;
                case 'd' when !hostOnly: sb.Append(HomeDirectory); break;
                default: sb.Append('%').Append(t); break;
            }
        }
        return sb.ToString();
    }
}
