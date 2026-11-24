using System.IO;
using System.IO.Enumeration;

namespace RDMan.Services;

public sealed record FsEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified, FileAttributes Attributes)
{
    public bool IsHidden => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}

public static class FileSystemService
{
    /// <summary>
    /// Lists a folder on a background thread. Uses FileSystemEnumerable directly (one FindFirstFile pass,
    /// no per-item stat calls) which matters a lot over SMB with thousands of entries.
    /// Throws IOException/UnauthorizedAccessException with the original HResult.
    /// </summary>
    public static Task<List<FsEntry>> ListAsync(string folder, bool includeHidden, CancellationToken ct)
        => Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false,
            };
            // FileSystemEnumerable opens the handle eagerly and throws with the precise Win32 error
            // (access denied, network name not found…) instead of silently returning an empty list.
            var enumerable = new FileSystemEnumerable<FsEntry>(folder,
                (ref FileSystemEntry e) => new FsEntry(
                    e.FileName.ToString(),
                    e.ToFullPath(),
                    e.IsDirectory,
                    e.IsDirectory ? -1 : e.Length,
                    e.LastWriteTimeUtc.LocalDateTime,
                    e.Attributes),
                options);

            var list = new List<FsEntry>();
            foreach (var entry in enumerable)
            {
                if ((list.Count & 255) == 0) ct.ThrowIfCancellationRequested();
                list.Add(entry);
            }
            return list;
        }, ct);

    public static string UniqueChildName(string folder, string baseName)
    {
        var candidate = baseName;
        for (var i = 2; i < 10_000; i++)
        {
            var full = Path.Combine(folder, candidate);
            if (!Directory.Exists(full) && !File.Exists(full)) return candidate;
            candidate = $"{baseName} ({i})";
        }
        return $"{baseName} ({Guid.NewGuid():N})";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes:N0} {units[0]}" : $"{v:0.#} {units[u]}";
    }
}
