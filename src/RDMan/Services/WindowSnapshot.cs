using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RDMan.Services;

/// <summary>
/// Small, frozen pictures of a native window (used for remote-desktop thumbnails). PW_RENDERFULLCONTENT renders what
/// DWM composes, which includes the RDP ActiveX surface; a hidden window renders nothing, so callers only capture
/// while the session is on screen.
/// </summary>
internal static class WindowSnapshot
{
    private const uint PW_CLIENTONLY = 1;
    private const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>The window's client area scaled to at most <paramref name="maxWidth"/> pixels wide, or null.</summary>
    public static BitmapSource? Capture(IntPtr hwnd, int maxWidth)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || !GetClientRect(hwnd, out var rc)) return null;
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w < 16 || h < 16) return null;
        try
        {
            using var full = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT)) return null;
                }
                finally { g.ReleaseHdc(hdc); }
            }
            var scale = Math.Min(1.0, maxWidth / (double)w);
            var tw = Math.Max(1, (int)(w * scale));
            var th = Math.Max(1, (int)(h * scale));
            using var small = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, tw, th);
            }
            var hbmp = small.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hbmp); }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }
}
