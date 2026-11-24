using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RDMan.Interop;

namespace RDMan.Services;

/// <summary>
/// System file-type icons and type names ("Documento de texto"), cached per extension.
/// Uses SHGFI_USEFILEATTRIBUTES so it never touches the network or the file itself
/// (per-file icons such as .exe/.lnk show their generic type icon instead of reading remote files).
/// Must be called on the UI thread; results are frozen and shareable.
/// </summary>
public sealed class IconProvider
{
    private readonly Dictionary<string, (ImageSource? Icon, string TypeName)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _local = new(StringComparer.OrdinalIgnoreCase);

    public (ImageSource? Icon, string TypeName) ForFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var key = string.IsNullOrEmpty(ext) ? "<none>" : ext;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var value = Query(string.IsNullOrEmpty(ext) ? "file" : "file" + ext, NativeMethods.FILE_ATTRIBUTE_NORMAL);
        _cache[key] = value;
        return value;
    }

    public (ImageSource? Icon, string TypeName) ForFolder()
    {
        if (_cache.TryGetValue("<folder>", out var hit)) return hit;
        var value = Query("folder", NativeMethods.FILE_ATTRIBUTE_DIRECTORY);
        _cache["<folder>"] = value;
        return value;
    }

    /// <summary>Real icon of a local path (drives, Desktop, Downloads). Never call for network paths.</summary>
    public ImageSource? ForLocalPath(string path)
    {
        if (_local.TryGetValue(path, out var hit)) return hit;
        var info = new NativeMethods.SHFILEINFO();
        var r = NativeMethods.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON);
        var img = r != IntPtr.Zero ? ToImage(info.hIcon) : null;
        _local[path] = img;
        return img;
    }

    private static (ImageSource?, string) Query(string probe, uint attributes)
    {
        var info = new NativeMethods.SHFILEINFO();
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON | NativeMethods.SHGFI_TYPENAME | NativeMethods.SHGFI_USEFILEATTRIBUTES;
        var r = NativeMethods.SHGetFileInfo(probe, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
        if (r == IntPtr.Zero) return (null, "");
        return (ToImage(info.hIcon), info.szTypeName ?? "");
    }

    private static ImageSource? ToImage(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}
