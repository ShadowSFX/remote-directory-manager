using System.IO;
using System.Text;
using RDMan.Core.Models;
using RDMan.Core.Security;
using RDMan.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RDMan.Services.Ssh;

public enum KeyLoadResult { Ok, NeedsPassphrase, WrongPassphrase, Unreadable }

/// <summary>
/// Imports private keys into the connection file (DPAPI-encrypted) and turns vault entries or key files into
/// SSH.NET key sources. Decrypted key text only lives in memory for the duration of a load.
/// </summary>
public static class SshKeyVault
{
    /// <summary>Largest key file accepted (real keys are a few KB; this rejects picking a wrong, huge file).</summary>
    private const int MaxKeyFileBytes = 256 * 1024;

    /// <summary>Validates a key file and builds a vault entry. Asks nothing: the caller supplies the passphrase.</summary>
    public static KeyLoadResult TryImport(string path, string? passphrase, ISecretProtector secrets, out SshKeyEntry? entry)
    {
        entry = null;
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxKeyFileBytes) return KeyLoadResult.Unreadable;
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return KeyLoadResult.Unreadable;
        }

        string? certificate = null;
        var certPath = path + "-cert.pub";
        if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
            certPath = Path.ChangeExtension(path, null) + "-cert.pub";
        try
        {
            if (File.Exists(certPath)) certificate = File.ReadAllText(certPath).Trim();
        }
        catch (IOException) { }

        var result = TryLoad(text, passphrase, certificate, out var key);
        if (result != KeyLoadResult.Ok || key is null) return result;

        var blob = PublicBlob(key);
        var (type, bits) = Fingerprint.Describe(blob);
        entry = new SshKeyEntry
        {
            Name = Path.GetFileName(path),
            Algorithm = type,
            Bits = bits,
            Fingerprint = Fingerprint.Sha256(blob),
            Comment = key.Key.Comment,
            SourcePath = path,
            ImportedAt = DateTimeOffset.Now,
            ProtectedKey = secrets.Protect(text),
            ProtectedPassphrase = string.IsNullOrEmpty(passphrase) ? null : secrets.Protect(passphrase),
            Certificate = certificate,
        };
        return KeyLoadResult.Ok;
    }

    /// <summary>Opens a vault entry. <paramref name="passphrase"/> overrides the remembered one.</summary>
    public static KeyLoadResult TryLoad(SshKeyEntry entry, ISecretProtector secrets, string? passphrase, out PrivateKeyFile? key)
    {
        key = null;
        var text = secrets.TryUnprotect(entry.ProtectedKey);
        if (text is null) return KeyLoadResult.Unreadable;
        return TryLoad(text, passphrase ?? secrets.TryUnprotect(entry.ProtectedPassphrase), entry.Certificate, out key);
    }

    /// <summary>Opens a key file on disk (IdentityFile). A matching "-cert.pub" next to it is used when present.</summary>
    public static KeyLoadResult TryLoadFile(string path, string? passphrase, out PrivateKeyFile? key)
    {
        key = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxKeyFileBytes) return KeyLoadResult.Unreadable;
            var text = File.ReadAllText(path);
            var cert = File.Exists(path + "-cert.pub") ? File.ReadAllText(path + "-cert.pub").Trim() : null;
            return TryLoad(text, passphrase, cert, out key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KeyLoadResult.Unreadable;
        }
    }

    private static KeyLoadResult TryLoad(string text, string? passphrase, string? certificate, out PrivateKeyFile? key)
    {
        key = null;
        // AWS .pem files downloaded on Windows may carry a BOM or CRLF; SSH.NET wants clean PEM.
        text = text.TrimStart('﻿').Replace("\r\n", "\n");
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var cert = certificate is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(certificate));
            key = cert is null
                ? new PrivateKeyFile(stream, string.IsNullOrEmpty(passphrase) ? null : passphrase)
                : new PrivateKeyFile(stream, string.IsNullOrEmpty(passphrase) ? null : passphrase, cert);
            return KeyLoadResult.Ok;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return KeyLoadResult.NeedsPassphrase;
        }
        catch (Exception ex) when (ex is SshException or ArgumentException or FormatException or NotSupportedException
                                       or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            if (!string.IsNullOrEmpty(passphrase) && LooksEncrypted(text)) return KeyLoadResult.WrongPassphrase;
            if (string.IsNullOrEmpty(passphrase) && LooksEncrypted(text)) return KeyLoadResult.NeedsPassphrase;
            App.Log(ex);
            return KeyLoadResult.Unreadable;
        }
    }

    private static bool LooksEncrypted(string text)
        => text.Contains("ENCRYPTED", StringComparison.Ordinal) || text.Contains("Encryption: aes", StringComparison.Ordinal)
           || IsEncryptedOpenSsh(text);

    /// <summary>OpenSSH's new format says "none" as the cipher name inside the base64 body when unencrypted.</summary>
    private static bool IsEncryptedOpenSsh(string text)
    {
        const string begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        var i = text.IndexOf(begin, StringComparison.Ordinal);
        if (i < 0) return false;
        var body = text[(i + begin.Length)..];
        var end = body.IndexOf("-----END", StringComparison.Ordinal);
        if (end < 0) return false;
        try
        {
            var bytes = Convert.FromBase64String(body[..end].Replace("\n", "").Replace("\r", "").Trim());
            // "openssh-key-v1\0" + string ciphername
            var offset = 15;
            var len = (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
            var cipher = Encoding.ASCII.GetString(bytes, offset + 4, len);
            return cipher != "none";
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The public key blob (RFC 4253 wire format) of a loaded key.</summary>
    public static byte[] PublicBlob(PrivateKeyFile key)
    {
        // The first host algorithm's Data is the public key blob ("ssh-ed25519" + key, "ssh-rsa" + e + n…).
        var algorithms = key.HostKeyAlgorithms;
        return (algorithms.FirstOrDefault(a => !a.Name.Contains("-cert-", StringComparison.Ordinal)) ?? algorithms.First()).Data;
    }
}
