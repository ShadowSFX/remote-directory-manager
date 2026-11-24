using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RDMan.Interop;

namespace RDMan.Services;

/// <summary>
/// Shows the real Windows shell context menu (Open with, Send to, 7-Zip, antivirus, Properties…) for files
/// that live in the same folder. Owner-drawn submenus are serviced through IContextMenu2/3 via an HwndSource hook.
/// </summary>
public sealed class ShellContextMenu
{
    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_EXTENDEDVERBS = 0x100;
    private const uint CMF_EXPLORE = 0x4;
    private const uint CMIC_MASK_UNICODE = 0x4000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;

    public void Show(Window owner, IReadOnlyList<string> paths, Point screenPoint, bool extended)
    {
        if (paths.Count == 0) return;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var source = HwndSource.FromHwnd(hwnd);

        var pidls = new List<IntPtr>();
        IShellFolder? parent = null;
        IntPtr menuPtr = IntPtr.Zero;
        IContextMenu? menu = null;
        var hMenu = IntPtr.Zero;
        try
        {
            var children = new List<IntPtr>();
            var parentDir = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
            foreach (var p in paths)
            {
                // The shell API requires every item to share the parent folder.
                if (!string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), parentDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (NativeMethods.SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                pidls.Add(pidl);
                var iidFolder = typeof(IShellFolder).GUID;
                if (NativeMethods.SHBindToParent(pidl, ref iidFolder, out var folder, out var last) != 0) continue;
                if (parent is null) parent = folder;
                else Marshal.ReleaseComObject(folder);
                children.Add(last);
            }
            if (parent is null || children.Count == 0) return;

            var iidMenu = typeof(IContextMenu).GUID;
            parent.GetUIObjectOf(hwnd, (uint)children.Count, children.ToArray(), ref iidMenu, IntPtr.Zero, out menuPtr);
            menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            _menu2 = menu as IContextMenu2;
            _menu3 = menu as IContextMenu3;

            hMenu = NativeMethods.CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_EXPLORE | (extended ? CMF_EXTENDEDVERBS : 0);
            if (menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags) < 0) return;

            source?.AddHook(Hook);
            int cmd;
            try
            {
                cmd = NativeMethods.TrackPopupMenuEx(hMenu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                    (int)screenPoint.X, (int)screenPoint.Y, hwnd, IntPtr.Zero);
            }
            finally
            {
                source?.RemoveHook(Hook);
            }

            if (cmd >= FirstCommand)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                            | ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 ? CMIC_MASK_CONTROL_DOWN : 0)
                            | ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? CMIC_MASK_SHIFT_DOWN : 0),
                    hwnd = hwnd,
                    lpVerb = (IntPtr)(cmd - (int)FirstCommand),
                    lpVerbW = (IntPtr)(cmd - (int)FirstCommand),
                    lpDirectoryW = parentDir,
                    nShow = NativeMethods.SW_SHOWNORMAL,
                    ptInvoke = new NativeMethods.POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y },
                };
                menu.InvokeCommand(ref info);
            }
        }
        finally
        {
            _menu2 = null;
            _menu3 = null;
            if (hMenu != IntPtr.Zero) NativeMethods.DestroyMenu(hMenu);
            if (menu is not null) Marshal.ReleaseComObject(menu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parent is not null) Marshal.ReleaseComObject(parent);
            foreach (var p in pidls) NativeMethods.ILFree(p);
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MENUCHAR:
                if (_menu3 is not null && _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                {
                    handled = true;
                    return result;
                }
                break;
            case WM_INITMENUPOPUP:
            case WM_DRAWITEM:
            case WM_MEASUREITEM:
                if (_menu3 is not null && _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out _) == 0)
                {
                    handled = true;
                    return msg == WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1;
                }
                if (_menu2 is not null && _menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return msg == WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1;
                }
                break;
        }
        return IntPtr.Zero;
    }
}
