using System.Buffers.Binary;
using System.Text;

namespace RDMan.Core.Ssh;

/// <summary>A key offered by an SSH agent.</summary>
public sealed record AgentIdentity(byte[] PublicKeyBlob, string Comment)
{
    public string KeyType => Fingerprint.Describe(PublicKeyBlob).Type;
    public string Fingerprint256 => Fingerprint.Sha256(PublicKeyBlob);
}

[Flags]
public enum AgentSignFlags : uint
{
    None = 0,
    RsaSha2_256 = 2,
    RsaSha2_512 = 4,
}

public sealed class AgentException(string message) : Exception(message);

/// <summary>
/// Client side of the ssh-agent protocol (draft-miller-ssh-agent) over any duplex stream: the Windows OpenSSH
/// agent's named pipe or Pageant's. Only what authentication needs: list identities and sign.
/// </summary>
public sealed class AgentClient(Stream stream)
{
    private const byte Failure = 5;
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;
    private const int MaxMessage = 256 * 1024;

    public const string OpenSshPipe = @"\\.\pipe\openssh-ssh-agent";

    public async Task<IReadOnlyList<AgentIdentity>> RequestIdentitiesAsync(CancellationToken ct = default)
    {
        var reply = await RoundTripAsync([RequestIdentities], ct).ConfigureAwait(false);
        if (reply.Length == 0 || reply[0] != IdentitiesAnswer) throw new AgentException("The agent refused to list keys.");
        var span = reply.AsMemory(1);
        var count = ReadUInt32(ref span);
        if (count > 1024) throw new AgentException("The agent sent an invalid key list.");
        var list = new List<AgentIdentity>((int)count);
        for (var i = 0; i < count; i++)
        {
            var blob = ReadString(ref span);
            var comment = Encoding.UTF8.GetString(ReadString(ref span));
            list.Add(new AgentIdentity(blob, comment));
        }
        return list;
    }

    /// <summary>Returns the SSH signature blob (string algorithm, string signature) for <paramref name="data"/>.</summary>
    public async Task<byte[]> SignAsync(byte[] publicKeyBlob, byte[] data, AgentSignFlags flags, CancellationToken ct = default)
    {
        var payload = new List<byte>(1 + 8 + publicKeyBlob.Length + data.Length + 4) { SignRequest };
        WriteString(payload, publicKeyBlob);
        WriteString(payload, data);
        WriteUInt32(payload, (uint)flags);
        var reply = await RoundTripAsync([.. payload], ct).ConfigureAwait(false);
        if (reply.Length == 0 || reply[0] == Failure) throw new AgentException("The agent refused to sign (the key may need confirmation or be locked).");
        if (reply[0] != SignResponse) throw new AgentException("Unexpected agent reply.");
        var span = reply.AsMemory(1);
        return ReadString(ref span);
    }

    private async Task<byte[]> RoundTripAsync(byte[] message, CancellationToken ct)
    {
        var frame = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)message.Length);
        message.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaxMessage) throw new AgentException("Invalid agent reply length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static uint ReadUInt32(ref Memory<byte> span)
    {
        if (span.Length < 4) throw new AgentException("Truncated agent reply.");
        var v = BinaryPrimitives.ReadUInt32BigEndian(span.Span);
        span = span[4..];
        return v;
    }

    private static byte[] ReadString(ref Memory<byte> span)
    {
        var len = ReadUInt32(ref span);
        if (len > span.Length) throw new AgentException("Truncated agent reply.");
        var v = span[..(int)len].ToArray();
        span = span[(int)len..];
        return v;
    }

    internal static void WriteUInt32(List<byte> buffer, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        foreach (var x in b) buffer.Add(x);
    }

    internal static void WriteString(List<byte> buffer, byte[] value)
    {
        WriteUInt32(buffer, (uint)value.Length);
        buffer.AddRange(value);
    }
}
