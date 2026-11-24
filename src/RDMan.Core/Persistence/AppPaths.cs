namespace RDMan.Core.Persistence;

/// <summary>
/// Where RDMan keeps its data. Portable by default (a "RDMan.data" folder next to the exe);
/// falls back to %APPDATA%\RDMan when the exe folder is read-only (e.g. Program Files, a read-only USB).
/// </summary>
public sealed class AppPaths
{
    public const string PortableFolderName = "RDMan.data";
    public const string ConnectionsFileName = "connections.rdm";
    public const string SettingsFileName = "settings.json";

    private AppPaths(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    public string DataDirectory { get; }
    public bool IsPortable { get; }
    public string ConnectionsFile => Path.Combine(DataDirectory, ConnectionsFileName);
    public string SettingsFile => Path.Combine(DataDirectory, SettingsFileName);

    public static AppPaths Resolve(string exeDirectory, string appDataRoot, Func<string, bool>? canWrite = null)
    {
        canWrite ??= ProbeWritable;
        var portable = Path.Combine(exeDirectory, PortableFolderName);
        if (canWrite(portable))
            return new AppPaths(portable, isPortable: true);

        var roaming = Path.Combine(appDataRoot, "RDMan");
        Directory.CreateDirectory(roaming);
        return new AppPaths(roaming, isPortable: false);
    }

    public static bool ProbeWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
