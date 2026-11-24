using System.Security.Cryptography;
using System.Text;

namespace RDMan.Core.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Returns null when the blob cannot be decrypted (different Windows user/PC, or corrupt).</summary>
    string? TryUnprotect(string? protectedValue);
}

/// <summary>
/// DPAPI (CurrentUser scope). The encrypted blob is only readable by the same Windows account on the same machine,
/// which is what keeps the portable config file safe if it is copied elsewhere.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi1:";
    private static readonly byte[] Entropy = "RDMan.RemoteDirectoryManager.v1"u8.ToArray();

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var data = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string? TryUnprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue) || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        try
        {
            var blob = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            var data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(data); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (FormatException) { return null; }
        catch (CryptographicException) { return null; }
    }
}
