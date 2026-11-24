using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using RDMan.Interop;

namespace RDMan.Services;

public enum FileOpKind { Copy, Move, Delete, DeletePermanently, Rename }

public sealed record FileOpResult(bool Success, bool Aborted, string? Error);

/// <summary>
/// File operations through the Windows copy engine (IFileOperation): the very same progress window,
/// conflict resolution ("Replace / Skip / Compare"), recycle bin and permission prompts as File Explorer.
/// Each operation runs on its own STA thread so the app stays responsive while Windows shows its UI.
/// </summary>
public sealed class ShellOperations
{
    private const uint FOF_RENAMEONCOLLISION = 0x0008;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_WANTNUKEWARNING = 0x4000;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;

    private const int HrCancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    private static readonly Guid IidShellItem = typeof(IShellItem).GUID;
    private int _running;

    /// <summary>Number of operations currently in progress (the updater waits for zero).</summary>
    public int RunningCount => Volatile.Read(ref _running);

    public event EventHandler<IReadOnlyCollection<string>>? Completed;

    public Task<FileOpResult> CopyAsync(IntPtr owner, IReadOnlyList<string> sources, string destinationFolder)
        => RunAsync(owner, FileOpKind.Copy, sources, destinationFolder, null);

    public Task<FileOpResult> MoveAsync(IntPtr owner, IReadOnlyList<string> sources, string destinationFolder)
        => RunAsync(owner, FileOpKind.Move, sources, destinationFolder, null);

    public Task<FileOpResult> DeleteAsync(IntPtr owner, IReadOnlyList<string> items, bool permanently)
        => RunAsync(owner, permanently ? FileOpKind.DeletePermanently : FileOpKind.Delete, items, null, null);

    public Task<FileOpResult> RenameAsync(IntPtr owner, string item, string newName)
        => RunAsync(owner, FileOpKind.Rename, [item], null, newName);

    private Task<FileOpResult> RunAsync(IntPtr owner, FileOpKind kind, IReadOnlyList<string> items, string? destination, string? newName)
    {
        if (items.Count == 0) return Task.FromResult(new FileOpResult(true, false, null));
        var tcs = new TaskCompletionSource<FileOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Interlocked.Increment(ref _running);
            try
            {
                tcs.SetResult(Execute(owner, kind, items, destination, newName));
            }
            catch (Exception ex)
            {
                tcs.SetResult(new FileOpResult(false, false, ex.Message));
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                var touched = new List<string>();
                if (destination is not null) touched.Add(destination);
                foreach (var i in items)
                {
                    var parent = Path.GetDirectoryName(i.TrimEnd('\\'));
                    if (parent is not null) touched.Add(parent);
                }
                Completed?.Invoke(this, touched.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
        })
        {
            IsBackground = true,
            Name = "RDMan file operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static FileOpResult Execute(IntPtr owner, FileOpKind kind, IReadOnlyList<string> items, string? destination, string? newName)
    {
        var op = (IFileOperation)new FileOperationCoClass();
        var created = new List<object> { op };
        try
        {
            uint flags = FOF_NOCONFIRMMKDIR | FOFX_ADDUNDORECORD;
            switch (kind)
            {
                case FileOpKind.Copy:
                case FileOpKind.Move:
                    flags |= FOF_ALLOWUNDO;
                    // Pasting into the folder the files came from → "name - copy", exactly like Explorer.
                    if (kind == FileOpKind.Copy && destination is not null && items.All(i => SameFolder(Path.GetDirectoryName(i.TrimEnd('\\')), destination)))
                        flags |= FOF_RENAMEONCOLLISION;
                    break;
                case FileOpKind.Delete:
                    flags |= FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING;
                    break;
                case FileOpKind.DeletePermanently:
                    flags |= FOF_WANTNUKEWARNING;
                    break;
                case FileOpKind.Rename:
                    flags |= FOF_ALLOWUNDO;
                    break;
            }
            op.SetOperationFlags(flags);
            if (owner != IntPtr.Zero) op.SetOwnerWindow(owner);

            IShellItem? dest = null;
            if (destination is not null)
            {
                dest = CreateItem(destination);
                created.Add(dest);
            }

            foreach (var path in items)
            {
                var item = CreateItem(path);
                created.Add(item);
                switch (kind)
                {
                    case FileOpKind.Copy: op.CopyItem(item, dest!, null, IntPtr.Zero); break;
                    case FileOpKind.Move: op.MoveItem(item, dest!, null, IntPtr.Zero); break;
                    case FileOpKind.Delete:
                    case FileOpKind.DeletePermanently: op.DeleteItem(item, IntPtr.Zero); break;
                    case FileOpKind.Rename: op.RenameItem(item, newName!, IntPtr.Zero); break;
                }
            }

            try
            {
                op.PerformOperations();
            }
            catch (COMException ex) when (ex.HResult is HrCancelled or CopyEngineUserCancelled)
            {
                return new FileOpResult(true, true, null);
            }
            return new FileOpResult(true, op.GetAnyOperationsAborted(), null);
        }
        catch (COMException ex)
        {
            return new FileOpResult(false, false, DescribeHResult(ex.HResult, ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return new FileOpResult(false, false, ex.Message);
        }
        finally
        {
            foreach (var o in created)
                if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = IidShellItem;
        NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        return item;
    }

    private static bool SameFolder(string? a, string? b)
        => a is not null && b is not null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    internal static string DescribeHResult(int hr, string fallback)
    {
        var code = Core.Smb.SmbErrorTranslator.Win32CodeFromHResult(hr);
        if (code != hr) return new Win32Exception(code).Message;
        return fallback;
    }
}
