using System.Runtime.InteropServices;

namespace RDMan.Interop;

internal static partial class NativeMethods
{
    // ───────────── user32.dll (taskbar attention) ─────────────
    public const uint FLASHW_TRAY = 0x2;
    public const uint FLASHW_TIMERNOFG = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
