using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RDMan.Core.Ssh;

/// <summary>Host/user key fingerprints exactly as OpenSSH prints them (ssh-keygen -lf, -lv).</summary>
public static class Fingerprint
{
    /// <summary>"SHA256:" + unpadded base64 of SHA-256(public key blob).</summary>
    public static string Sha256(byte[] publicKeyBlob)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');

    /// <summary>
    /// Key type and size from an SSH public key blob (RFC 4253 encoding): "ssh-ed25519"/256,
    /// "ssh-rsa"/modulus bits, "ecdsa-sha2-nistp384"/384…
    /// </summary>
    public static (string Type, int Bits) Describe(byte[] blob)
    {
        try
        {
            ReadOnlySpan<byte> span = blob;
            var type = Encoding.ASCII.GetString(ReadString(ref span));
            var bits = type switch
            {
                "ssh-ed25519" or "ssh-ed25519-cert-v01@openssh.com" => 256,
                "ssh-ed448" => 456,
                "ssh-rsa" or "ssh-rsa-cert-v01@openssh.com" => RsaBits(span),
                "ssh-dss" => 1024,
                _ when type.StartsWith("ecdsa-sha2-nistp", StringComparison.Ordinal)
                    && int.TryParse(type.AsSpan("ecdsa-sha2-nistp".Length).ToString().Split('-')[0], out var b) => b,
                _ => 0,
            };
            return (type, bits);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ("unknown", 0);
        }
    }

    private static int RsaBits(ReadOnlySpan<byte> rest)
    {
        _ = ReadString(ref rest); // e
        var n = ReadString(ref rest);
        while (n.Length > 0 && n[0] == 0) n = n[1..];
        if (n.Length == 0) return 0;
        var bits = (n.Length - 1) * 8;
        for (var top = n[0]; top != 0; top >>= 1) bits++;
        return bits;
    }

    private static ReadOnlySpan<byte> ReadString(ref ReadOnlySpan<byte> span)
    {
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
        var value = span.Slice(4, len);
        span = span[(4 + len)..];
        return value;
    }

    /// <summary>Short name OpenSSH shows in randomart headers: ED25519, RSA, ECDSA, DSA.</summary>
    public static string ShortType(string keyType) => keyType switch
    {
        _ when keyType.StartsWith("ssh-ed25519", StringComparison.Ordinal) => "ED25519",
        _ when keyType.StartsWith("ssh-rsa", StringComparison.Ordinal) || keyType.StartsWith("rsa-sha2", StringComparison.Ordinal) => "RSA",
        _ when keyType.StartsWith("ecdsa", StringComparison.Ordinal) => "ECDSA",
        _ when keyType.StartsWith("ssh-dss", StringComparison.Ordinal) => "DSA",
        _ => keyType.ToUpperInvariant(),
    };

    /// <summary>
    /// OpenSSH "drunken bishop" randomart of the SHA-256 digest (VisualHostKey), 17×9 with the same
    /// header/footer as <c>ssh-keygen -lv</c>.
    /// </summary>
    public static string RandomArt(byte[] publicKeyBlob, string keyType, int bits)
    {
        const int sizeX = 17, sizeY = 9;
        const string augment = " .o+=*BOX@%&#/^SE";
        var len = augment.Length - 1;
        var field = new int[sizeX, sizeY];
        var digest = SHA256.HashData(publicKeyBlob);
        int x = sizeX / 2, y = sizeY / 2;

        foreach (var b in digest)
        {
            var input = b;
            for (var i = 0; i < 4; i++)
            {
                x += (input & 0x1) != 0 ? 1 : -1;
                y += (input & 0x2) != 0 ? 1 : -1;
                x = Math.Clamp(x, 0, sizeX - 1);
                y = Math.Clamp(y, 0, sizeY - 1);
                if (field[x, y] < len - 2) field[x, y]++;
                input >>= 2;
            }
        }
        field[sizeX / 2, sizeY / 2] = len - 1;
        field[x, y] = len;

        var sb = new StringBuilder();
        sb.Append(Frame($"[{ShortType(keyType)} {bits}]", sizeX)).Append('\n');
        for (var row = 0; row < sizeY; row++)
        {
            sb.Append('|');
            for (var col = 0; col < sizeX; col++) sb.Append(augment[Math.Min(field[col, row], len)]);
            sb.Append("|\n");
        }
        sb.Append(Frame("[SHA256]", sizeX));
        return sb.ToString();
    }

    private static string Frame(string title, int width)
    {
        if (title.Length > width) title = title[..width];
        var left = (width - title.Length) / 2;
        return "+" + new string('-', left) + title + new string('-', width - left - title.Length) + "+";
    }
}
