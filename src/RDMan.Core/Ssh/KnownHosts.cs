using System.Security.Cryptography;
using System.Text;

namespace RDMan.Core.Ssh;

public enum HostKeyStatus
{
    /// <summary>The host is known with exactly this key.</summary>
    Trusted,
    /// <summary>Nothing recorded for this host and key type.</summary>
    Unknown,
    /// <summary>The host is recorded with a different key of the same type: possible man-in-the-middle.</summary>
    Changed,
    /// <summary>The key is marked @revoked.</summary>
    Revoked,
}

public sealed record HostKeyCheck(HostKeyStatus Status, int? LineNumber = null, bool OtherTypesKnown = false);

/// <summary>
/// OpenSSH known_hosts file: plain and hashed (<c>|1|salt|hmac</c>) host patterns, <c>[host]:port</c> for
/// non-standard ports, wildcards and negation, and the @revoked marker (@cert-authority lines are kept but not
/// used). Lines are preserved verbatim so rewriting the file never loses anything RDMan doesn't understand.
/// </summary>
public sealed class KnownHosts
{
    private sealed record Line(string Text, string? Marker, string[] Patterns, string? KeyType, string? KeyBase64);

    private readonly List<Line> _lines = new();

    public static string DefaultPath(string home) => Path.Combine(home, ".ssh", "known_hosts");

    public int Count => _lines.Count(l => l.KeyType is not null);

    public static KnownHosts Parse(string? text)
    {
        var kh = new KnownHosts();
        if (string.IsNullOrEmpty(text)) return kh;
        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) is not null) kh._lines.Add(ParseLine(raw));
        return kh;
    }

    private static Line ParseLine(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || s[0] == '#') return new Line(raw, null, [], null, null);
        var parts = s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        string? marker = null;
        if (parts.Length > 0 && parts[0].StartsWith('@')) marker = parts[i++].ToLowerInvariant();
        if (parts.Length - i < 3) return new Line(raw, null, [], null, null);
        return new Line(raw, marker, parts[i].Split(','), parts[i + 1], parts[i + 2]);
    }

    /// <summary>"host" for port 22, "[host]:port" otherwise — how OpenSSH names a host in known_hosts.</summary>
    public static string HostEntryName(string host, int port)
        => port == 22 ? host.ToLowerInvariant() : $"[{host.ToLowerInvariant()}]:{port}";

    /// <param name="host">The address actually dialed (config HostName), not the alias.</param>
    public HostKeyCheck Check(string host, int port, string keyType, byte[] keyBlob)
    {
        var name = HostEntryName(host, port);
        var key = Convert.ToBase64String(keyBlob);
        HostKeyCheck? changed = null;
        var otherTypes = false;
        var trustedLine = (int?)null;

        for (var n = 0; n < _lines.Count; n++)
        {
            var l = _lines[n];
            if (l.KeyType is null || !Matches(l.Patterns, name)) continue;
            if (l.Marker == "@cert-authority") continue;
            var sameKey = l.KeyBase64 == key;
            if (l.Marker == "@revoked")
            {
                if (sameKey) return new HostKeyCheck(HostKeyStatus.Revoked, n + 1);
                continue;
            }
            if (!string.Equals(l.KeyType, keyType, StringComparison.Ordinal))
            {
                otherTypes = true;
                continue;
            }
            if (sameKey) trustedLine ??= n + 1;
            else changed ??= new HostKeyCheck(HostKeyStatus.Changed, n + 1);
        }

        // A matching line wins over a stale one of the same type (OpenSSH accepts any matching key).
        if (trustedLine is not null) return new HostKeyCheck(HostKeyStatus.Trusted, trustedLine);
        return changed ?? new HostKeyCheck(HostKeyStatus.Unknown, null, otherTypes);
    }

    private static bool Matches(string[] patterns, string name)
    {
        var positive = false;
        foreach (var p in patterns)
        {
            if (p.StartsWith("|1|", StringComparison.Ordinal))
            {
                if (HashedMatches(p, name)) positive = true;
            }
            else if (p.StartsWith('!'))
            {
                if (SshConfig.Wildcard(p[1..], name)) return false;
            }
            else if (SshConfig.Wildcard(p, name))
            {
                positive = true;
            }
        }
        return positive;
    }

    private static bool HashedMatches(string pattern, string name)
    {
        var parts = pattern.Split('|');
        // ["", "1", salt, hash]
        if (parts.Length != 4) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(name));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records <paramref name="keyType"/> for the host. With <paramref name="replace"/>, existing lines for this
    /// host and key type are removed first (lines that also name other hosts lose only this host).
    /// </summary>
    public void Add(string host, int port, string keyType, byte[] keyBlob, bool hash, bool replace = false)
    {
        var name = HostEntryName(host, port);
        if (replace)
        {
            for (var n = _lines.Count - 1; n >= 0; n--)
            {
                var l = _lines[n];
                if (l.KeyType != keyType || l.Marker is not null || !Matches(l.Patterns, name)) continue;
                var remaining = l.Patterns.Where(p => !PatternIsExactly(p, name)).ToArray();
                // Matched only through a wildcard: leave it (the exact line added below takes precedence in Check).
                if (remaining.Length == l.Patterns.Length) continue;
                if (remaining.Length == 0) _lines.RemoveAt(n);
                else _lines[n] = ParseLine($"{string.Join(',', remaining)} {l.KeyType} {l.KeyBase64}");
            }
        }

        var hostField = hash ? HashName(name, RandomNumberGenerator.GetBytes(20)) : name;
        _lines.Add(ParseLine($"{hostField} {keyType} {Convert.ToBase64String(keyBlob)}"));
    }

    private static bool PatternIsExactly(string pattern, string name)
        => pattern.StartsWith("|1|", StringComparison.Ordinal) ? HashedMatches(pattern, name)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    public static string HashName(string name, byte[] salt)
    {
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(name));
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
    }

    /// <summary>The file's text (LF line endings, trailing newline), unknown lines preserved.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l.Text).Append('\n');
        return sb.ToString();
    }
}
