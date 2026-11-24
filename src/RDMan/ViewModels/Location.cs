using System.IO;
using RDMan.Core.Persistence;
using RDMan.Core.Smb;

namespace RDMan.ViewModels;

/// <summary>Where a tab is. Records give value equality, which the history stacks rely on.</summary>
public abstract record Location
{
    public static Location FromSaved(SavedTab t) => t.Kind switch
    {
        "group" when t.NodeId is { } id => new GroupLocation(id),
        "smart" when t.NodeId is { } sg => new SmartGroupLocation(sg),
        "view" when t.Path is MainViewModel.ViewFavorites or MainViewModel.ViewRecent or MainViewModel.ViewConnected => new ViewLocation(t.Path),
        "thispc" => new ThisPcLocation(),
        "desktop" when t.NodeId is { } sid => new DesktopLocation(sid),
        "folder" when !string.IsNullOrWhiteSpace(t.Path) => new FolderLocation(t.Path!),
        _ => new HomeLocation(),
    };

    public abstract SavedTab ToSaved();
}

public sealed record HomeLocation : Location
{
    public override SavedTab ToSaved() => new() { Kind = "home" };
}

public sealed record GroupLocation(Guid GroupId) : Location
{
    public override SavedTab ToSaved() => new() { Kind = "group", NodeId = GroupId };
}

/// <summary>Favorites, Recent or Connected: servers from anywhere in the tree.</summary>
public sealed record ViewLocation(string View) : Location
{
    public override SavedTab ToSaved() => new() { Kind = "view", Path = View };
}

/// <summary>A saved search (smart group).</summary>
public sealed record SmartGroupLocation(Guid SmartGroupId) : Location
{
    public override SavedTab ToSaved() => new() { Kind = "smart", NodeId = SmartGroupId };
}

/// <summary>The remote desktop (RDP) of a server in the tree.</summary>
public sealed record DesktopLocation(Guid ServerId) : Location
{
    public override SavedTab ToSaved() => new() { Kind = "desktop", NodeId = ServerId };
}

public sealed record ThisPcLocation : Location
{
    public override SavedTab ToSaved() => new() { Kind = "thispc" };
}

/// <summary>A local folder, a server root (\\host → its shares) or anything inside a share.</summary>
public sealed record FolderLocation : Location
{
    public FolderLocation(string path) => Path = Normalize(path);

    public string Path { get; }

    public bool IsUnc => Path.StartsWith(@"\\", StringComparison.Ordinal);

    public SmbTarget? Target => UncPath.TrySplit(Path, out var t) ? t : null;

    /// <summary>\\host (lists shares; nothing can be pasted here).</summary>
    public bool IsServerRoot => Target is { Share: null };

    public override SavedTab ToSaved() => new() { Kind = "folder", Path = Path };

    public static string Normalize(string path)
    {
        var p = path.Trim().Trim('"');
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (UncPath.TryParse(p, out var t, out _) && t is not null) return t.FullPath;
            return p.TrimEnd('\\');
        }
        if (p.Length == 2 && p[1] == ':') return p.ToUpperInvariant() + "\\";
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p.Length >= 2 && p[1] == ':' ? char.ToUpperInvariant(p[0]) + p[1..] : p;
    }

    /// <summary>The containing location, or null at the top (\\host, or "This PC" above a drive root).</summary>
    public Location? Parent()
    {
        if (IsUnc)
        {
            var t = Target;
            if (t is null || t.Share is null) return null;
            if (string.IsNullOrEmpty(t.SubPath)) return new FolderLocation(t.ServerRoot);
            var idx = t.SubPath.LastIndexOf('\\');
            return new FolderLocation(idx < 0 ? t.ShareRoot! : $@"{t.ShareRoot}\{t.SubPath[..idx]}");
        }
        var parent = System.IO.Path.GetDirectoryName(Path);
        return parent is null ? new ThisPcLocation() : new FolderLocation(parent);
    }

    public bool Equals(FolderLocation? other)
        => other is not null && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path);

    public string DisplayLeaf
    {
        get
        {
            var t = Target;
            if (t is not null)
            {
                if (t.Share is null) return t.Host;
                if (string.IsNullOrEmpty(t.SubPath)) return t.Share;
                return t.SubPath[(t.SubPath.LastIndexOf('\\') + 1)..];
            }
            var name = System.IO.Path.GetFileName(Path);
            return string.IsNullOrEmpty(name) ? Path.TrimEnd('\\') : name;
        }
    }
}

public static class LocationExtensions
{
    public static bool IsUnder(this string path, string folder)
    {
        var p = path.TrimEnd('\\') + "\\";
        var f = folder.TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    public static string ParentFolder(this string path) => Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
}

