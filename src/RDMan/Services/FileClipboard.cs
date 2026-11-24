using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace RDMan.Services;

public enum ClipboardIntent { Copy, Move }

public sealed record ClipboardFiles(IReadOnlyList<string> Paths, ClipboardIntent Intent);

/// <summary>
/// File clipboard in the exact format File Explorer uses (CF_HDROP + "Preferred DropEffect"),
/// so copy/cut/paste works both ways between RDMan and Explorer.
/// </summary>
public sealed class FileClipboard
{
    private const string PreferredDropEffect = "Preferred DropEffect";
    private const int DropEffectCopy = 5; // DROPEFFECT_COPY | DROPEFFECT_LINK, what Explorer writes on Ctrl+C
    private const int DropEffectMove = 2;

    public event EventHandler? Changed;

    /// <summary>Paths currently "cut" by RDMan (shown dimmed). Cleared when the clipboard is overwritten.</summary>
    public IReadOnlySet<string> CutPaths { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool Set(IReadOnlyList<string> paths, ClipboardIntent intent)
    {
        if (paths.Count == 0) return false;
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(intent == ClipboardIntent.Move ? DropEffectMove : DropEffectCopy)));
        if (!Retry(() => Clipboard.SetDataObject(data, copy: true))) return false;
        CutPaths = intent == ClipboardIntent.Move
            ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public ClipboardFiles? Get()
    {
        IDataObject? data = null;
        if (!Retry(() => data = Clipboard.GetDataObject()) || data is null) return null;
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;
            return new ClipboardFiles(files, ReadIntent(data));
        }
        catch (COMException) { return null; }
        catch (ExternalException) { return null; }
    }

    public bool HasFiles()
    {
        var has = false;
        Retry(() => has = Clipboard.ContainsFileDropList());
        return has;
    }

    /// <summary>After a cut+paste completes, Explorer empties the clipboard; we do the same.</summary>
    public void ClearAfterMove()
    {
        Retry(Clipboard.Clear);
        CutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called when another app changes the clipboard (detected on window activation).</summary>
    public void Revalidate()
    {
        if (CutPaths.Count == 0) return;
        var current = Get();
        if (current is null || current.Intent != ClipboardIntent.Move || !current.Paths.All(CutPaths.Contains))
        {
            CutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public static ClipboardIntent ReadIntent(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(PreferredDropEffect) && data.GetData(PreferredDropEffect) is MemoryStream ms && ms.Length >= 4)
            {
                var buffer = new byte[4];
                ms.Position = 0;
                ms.ReadExactly(buffer);
                return (BitConverter.ToInt32(buffer, 0) & DropEffectMove) != 0 && (BitConverter.ToInt32(buffer, 0) & 1) == 0
                    ? ClipboardIntent.Move
                    : ClipboardIntent.Copy;
            }
        }
        catch (COMException) { }
        catch (IOException) { }
        return ClipboardIntent.Copy;
    }

    public static bool SetText(string text) => Retry(() => Clipboard.SetDataObject(text, copy: true));

    /// <summary>The clipboard is a shared resource; another process may hold it open for a moment (CLIPBRD_E_CANT_OPEN).</summary>
    private static bool Retry(Action action)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(15 * (i + 1));
            }
            catch (ExternalException)
            {
                Thread.Sleep(15 * (i + 1));
            }
        }
        return false;
    }
}
