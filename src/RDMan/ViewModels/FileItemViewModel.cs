using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Infrastructure;
using RDMan.Services;

namespace RDMan.ViewModels;

public enum FileItemKind { File, Folder, Share, Drive, KnownFolder }

/// <summary>One row in the file list.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(FileItemKind kind, string name, string fullPath, long size, DateTime? modified, string typeName, ImageSource? icon, bool isHidden, string? remark = null)
    {
        Kind = kind;
        _name = name;
        FullPath = fullPath;
        _size = size;
        _modified = modified;
        TypeName = typeName;
        Icon = icon;
        IsHidden = isHidden;
        Remark = remark;
    }

    public FileItemKind Kind { get; }
    public bool IsContainer => Kind != FileItemKind.File;
    public bool IsFolder => Kind == FileItemKind.Folder;
    public string FullPath { get; private set; }
    public string TypeName { get; }
    public ImageSource? Icon { get; }
    public bool IsHidden { get; }
    public string? Remark { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime? _modified;
    [ObservableProperty] private bool _isCut;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>Glyph used when there is no shell icon (shares, known folders).</summary>
    public string Glyph => Kind switch
    {
        FileItemKind.Share => "",
        FileItemKind.Drive => "",
        _ => "",
    };

    public string SizeText => Kind switch
    {
        FileItemKind.File => FileSystemService.FormatSize(Size),
        FileItemKind.Drive when Size >= 0 => Loc.F("Type.DriveFree", FileSystemService.FormatSize(Size)),
        _ => "",
    };

    public string ModifiedText => Modified is { } m ? FormatDate(m) : "";

    /// <summary>Short date + short time without seconds (what Explorer shows), following the user's regional settings.</summary>
    public static string FormatDate(DateTime m)
    {
        var f = CultureInfo.CurrentCulture.DateTimeFormat;
        var time = f.ShortTimePattern.Replace(":ss", "", StringComparison.Ordinal).Replace(".ss", "", StringComparison.Ordinal);
        return m.ToString(f.ShortDatePattern + " " + time, CultureInfo.CurrentCulture);
    }

    public string SecondaryText => Remark ?? "";

    partial void OnSizeChanged(long value) => OnPropertyChanged(nameof(SizeText));
    partial void OnModifiedChanged(DateTime? value) => OnPropertyChanged(nameof(ModifiedText));

    public void UpdateFrom(FsEntry e)
    {
        Size = e.IsDirectory ? -1 : e.Size;
        Modified = e.Modified;
    }

    public void RefreshCulture() => OnPropertyChanged(nameof(ModifiedText));

    public void BeginRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    /// <summary>Base name without extension, so rename can preselect it like Explorer.</summary>
    public int RenameSelectionLength => IsContainer || !Name.Contains('.') || Name.StartsWith('.') ? Name.Length : Path.GetFileNameWithoutExtension(Name).Length;
}

/// <summary>Sorting: containers first (like Explorer), then by the chosen column; natural order for names.</summary>
public sealed class FileItemComparer : System.Collections.IComparer, IComparer<FileItemViewModel>
{
    public FileItemComparer(string column, bool descending)
    {
        Column = column;
        Descending = descending;
    }

    public string Column { get; }
    public bool Descending { get; }

    public int Compare(object? x, object? y) => Compare(x as FileItemViewModel, y as FileItemViewModel);

    public int Compare(FileItemViewModel? a, FileItemViewModel? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        // Explorer order: known folders, then drives, then folders/shares, then files.
        var rank = Rank(a).CompareTo(Rank(b));
        if (rank != 0) return rank;
        var c = Column switch
        {
            "Size" => a.Size.CompareTo(b.Size),
            "Modified" => Nullable.Compare(a.Modified, b.Modified),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (c == 0) c = NaturalCompare(a.Name, b.Name);
        return Descending ? -c : c;
    }

    private static int Rank(FileItemViewModel i) => i.Kind switch
    {
        FileItemKind.KnownFolder => 0,
        FileItemKind.Drive => 1,
        FileItemKind.File => 3,
        _ => 2,
    };

    /// <summary>"file2" before "file10", like Explorer's StrCmpLogicalW.</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; while (i < a.Length && char.IsDigit(a[i])) i++;
                var sj = j; while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var cmp = na.SequenceCompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.Compare(a, i, b, j, 1, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
