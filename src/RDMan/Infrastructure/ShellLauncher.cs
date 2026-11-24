using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace RDMan.Infrastructure;

/// <summary>Opening things with Windows: only web links and existing folders, never arbitrary commands.</summary>
public static class ShellLauncher
{
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            App.Log(ex);
            return false;
        }
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { App.Log(ex); }
    }
}
