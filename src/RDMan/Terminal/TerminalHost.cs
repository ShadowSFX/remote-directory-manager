using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RDMan.Terminal;

/// <summary>
/// One terminal screen: a WebView2 (an HWND, so it lives in the window's session overlay like RDP) showing the
/// embedded xterm.js page. Assets are served from embedded resources on a private https origin; nothing is fetched
/// from the network and navigation away from the page is blocked.
/// </summary>
public sealed class TerminalHost : IDisposable
{
    public const string Origin = "https://rdman.terminal/";

    private static Task<CoreWebView2Environment>? _environment;
    private static Dictionary<string, byte[]>? _assets;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<string> _queue = new();
    private CoreWebView2? _core;
    private bool _pageLoaded;
    private bool _disposed;

    public TerminalHost(Color background)
    {
        View = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(background.A, background.R, background.G, background.B),
            Focusable = true,
        };
        _ = InitializeAsync();
    }

    public WebView2 View { get; }

    /// <summary>Messages from the page (UI thread). "type" is the "t" field.</summary>
    public event Action<string, JsonElement>? Message;

    /// <summary>WebView2 failed to start (missing or broken runtime).</summary>
    public event Action<Exception>? Failed;

    public bool IsLoaded => _pageLoaded;

    /// <summary>Null when the Evergreen WebView2 runtime isn't installed.</summary>
    public static string? RuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return null; }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or BadImageFormatException) { return null; }
    }

    private static Task<CoreWebView2Environment> Environment()
    {
        // One browser process for every terminal. The user data folder is a cache: keep it out of the portable folder.
        return _environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "RDMan", "WebView2"),
            options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows" });
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await Environment();
            await View.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            _core = View.CoreWebView2;
            var s = _core.Settings;
            s.AreDefaultContextMenusEnabled = false;
#if DEBUG
            s.AreDevToolsEnabled = true;
#else
            s.AreDevToolsEnabled = false;
#endif
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false; // F5/Ctrl+R would reload the page and kill the session view
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;

            _core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnResourceRequested;
            _core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            _core.NewWindowRequested += (_, e) => e.Handled = true;
            _core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            _core.WebMessageReceived += OnWebMessage;
            _core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    Failed?.Invoke(new InvalidOperationException($"Terminal view: {e.ProcessFailedKind}"));
            };
            _core.Navigate(Origin + "index.html");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or FileNotFoundException or DllNotFoundException or ArgumentException)
        {
            App.Log(ex);
            Failed?.Invoke(ex);
        }
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_core is null) return;
        var path = Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimStart('/') : "";
        var assets = Assets();
        if (!assets.TryGetValue(path, out var bytes))
        {
            e.Response = _core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var headers = $"Content-Type: {MimeOf(path)}\r\nX-Content-Type-Options: nosniff";
        if (path == "index.html")
            headers += "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'none'";
        e.Response = _core.Environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", headers);
    }

    private static Dictionary<string, byte[]> Assets()
    {
        if (_assets is not null) return _assets;
        var asm = Assembly.GetExecutingAssembly();
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith("web/", StringComparison.Ordinal)) continue;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            map[normalized[4..]] = ms.ToArray();
        }
        return _assets = map;
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".woff2" => "font/woff2",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return;
            var type = t.GetString()!;
            if (type == "loaded")
            {
                _pageLoaded = true;
                foreach (var m in _queue) _core?.PostWebMessageAsString(m);
                _queue.Clear();
            }
            Message?.Invoke(type, root.Clone());
        }
    }

    /// <summary>Terminal output (already decoded text).</summary>
    public void Write(string text)
    {
        if (text.Length == 0) return;
        Post("o" + text);
    }

    /// <summary>A command for the page: an object with a "t" property.</summary>
    public void Send(object message) => Post(JsonSerializer.Serialize(message, Json));

    private void Post(string raw)
    {
        if (_disposed) return;
        if (!_pageLoaded || _core is null)
        {
            _queue.Add(raw);
            return;
        }
        try { _core.PostWebMessageAsString(raw); }
        catch (InvalidOperationException ex) { App.Log(ex); }
    }

    public void Focus()
    {
        View.Focus();
        Send(new { t = "focus" });
    }

    /// <summary>Snapshot of the visible terminal (for the Connected view thumbnails).</summary>
    public async Task<byte[]?> CaptureAsync()
    {
        if (_core is null || _disposed) return null;
        try
        {
            using var ms = new MemoryStream();
            await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Clear();
        try { View.Dispose(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { App.Log(ex); }
    }
}
