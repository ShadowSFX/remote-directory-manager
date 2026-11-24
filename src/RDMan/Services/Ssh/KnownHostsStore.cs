using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using RDMan.Core.Ssh;

namespace RDMan.Services.Ssh;

/// <summary>
/// The user's known_hosts files, shared with OpenSSH: read on every check (they're small and OpenSSH may have
/// changed them), written atomically. New entries go to the first file, like ssh does.
/// </summary>
public static class KnownHostsStore
{
    private static readonly object Gate = new();

    public static HostKeyCheck Check(IReadOnlyList<string> files, string host, int port, string keyType, byte[] blob)
    {
        var best = new HostKeyCheck(HostKeyStatus.Unknown);
        foreach (var file in files)
        {
            var r = KnownHosts.Parse(ReadText(file)).Check(host, port, keyType, blob);
            switch (r.Status)
            {
                case HostKeyStatus.Revoked:
                case HostKeyStatus.Trusted:
                    return r;
                case HostKeyStatus.Changed:
                    best = r;
                    break;
                case HostKeyStatus.Unknown when r.OtherTypesKnown && best.Status == HostKeyStatus.Unknown:
                    best = r;
                    break;
            }
        }
        return best;
    }

    /// <summary>Records (or, with <paramref name="replace"/>, replaces) the host's key in the first known_hosts file.</summary>
    public static void Save(IReadOnlyList<string> files, string host, int port, string keyType, byte[] blob, bool hash, bool replace)
    {
        if (files.Count == 0) return;
        var path = files[0];
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) CreatePrivateDirectory(dir);

            var kh = KnownHosts.Parse(ReadForUpdate(path));
            kh.Add(host, port, keyType, blob, hash, replace);
            // Replacing also has to clear stale lines in the other files, or the next check still says "changed".
            if (replace)
            {
                foreach (var other in files.Skip(1).Where(File.Exists))
                {
                    var o = KnownHosts.Parse(ReadForUpdate(other));
                    if (o.Check(host, port, keyType, blob).Status != HostKeyStatus.Changed) continue;
                    o.Add(host, port, keyType, blob, hash, replace: true);
                    Write(other, o.ToString());
                }
            }
            Write(path, kh.ToString());
        }
    }

    /// <summary>
    /// The current text before rewriting a file. Unlike <see cref="ReadText"/>, a file that exists but can't be read
    /// (ssh appending, antivirus) is retried and then reported — never treated as empty, which would wipe it.
    /// </summary>
    private static string? ReadForUpdate(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return null;
        }
    }

    private static void Write(string path, string text)
    {
        var tmp = path + ".rdman-tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>~/.ssh readable only by the user and SYSTEM/Administrators (what OpenSSH for Windows expects).</summary>
    private static void CreatePrivateDirectory(string dir)
    {
        var info = Directory.CreateDirectory(dir);
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[]
                     {
                         WindowsIdentity.GetCurrent().User!,
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     })
                sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            App.Log(ex);
        }
    }
}
