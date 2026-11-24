using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDMan.Core.Persistence;

public enum LoadOutcome
{
    /// <summary>File did not exist; defaults returned.</summary>
    Missing,
    Loaded,
    /// <summary>Main file was unreadable; the .bak copy was used.</summary>
    RecoveredFromBackup,
    /// <summary>Main file and backup unreadable; defaults returned and the broken file was preserved as .corrupt-*.</summary>
    Corrupt,
}

public sealed record LoadResult<T>(T Value, LoadOutcome Outcome, string? Error = null);

/// <summary>Crash-safe JSON persistence: write temp → atomic replace, keeping a .bak of the previous version.</summary>
public static class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // Hand-edited files may not put the "kind" discriminator first.
        AllowOutOfOrderMetadataProperties = true,
        // Keep accents readable in the file ("Producción", not "Producción"); it's never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LoadResult<T> Load<T>(string path, Func<T> createDefault) where T : class
    {
        if (!File.Exists(path))
        {
            // A crash between "delete" and "move" in older writers could leave only the backup.
            var orphanBak = path + ".bak";
            if (File.Exists(orphanBak) && TryRead<T>(orphanBak, out var fromOrphan, out _))
                return new LoadResult<T>(fromOrphan!, LoadOutcome.RecoveredFromBackup);
            return new LoadResult<T>(createDefault(), LoadOutcome.Missing);
        }

        if (TryRead<T>(path, out var value, out var error))
            return new LoadResult<T>(value!, LoadOutcome.Loaded);

        var bak = path + ".bak";
        if (File.Exists(bak) && TryRead<T>(bak, out var fromBak, out _))
            return new LoadResult<T>(fromBak!, LoadOutcome.RecoveredFromBackup, error);

        try
        {
            File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new LoadResult<T>(createDefault(), LoadOutcome.Corrupt, error);
    }

    private static bool TryRead<T>(string path, out T? value, out string? error) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            value = JsonSerializer.Deserialize<T>(stream, Options);
            error = value is null ? "empty document" : null;
            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
