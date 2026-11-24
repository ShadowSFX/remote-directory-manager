using System.IO;
using System.Windows.Threading;
using RDMan.Core.Credentials;
using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Security;

namespace RDMan.Services;

/// <summary>
/// The single source of truth for the connection tree and settings, with debounced crash-safe saving.
/// All mutations happen on the UI thread; call <see cref="MarkDirty"/> after changing the document.
/// </summary>
public sealed class AppState
{
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _settingsTimer;
    private TreeIndex? _index;

    public AppState(AppPaths paths, ConnectionDocument document, AppSettings settings, ISecretProtector secrets)
    {
        Paths = paths;
        Document = document;
        Settings = settings;
        Secrets = secrets;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _settingsTimer.Tick += (_, _) => { _settingsTimer.Stop(); SaveSettingsNow(); };
    }

    public AppPaths Paths { get; }
    public ConnectionDocument Document { get; }
    public AppSettings Settings { get; }
    public ISecretProtector Secrets { get; }

    /// <summary>Raised after the tree structure or any node changed (UI thread).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Raised when saving fails (message for a toast).</summary>
    public event EventHandler<string>? SaveFailed;

    public TreeIndex Index => _index ??= new TreeIndex(Document.Root);

    public void MarkDirty()
    {
        _index = null;
        _saveTimer.Stop();
        _saveTimer.Start();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSettingsDirty()
    {
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    public EffectiveCredential ResolveCredential(TreeNode node) => CredentialResolver.Resolve(node, Index, Document.Profiles);

    public EffectiveCredential ResolveDesktopCredential(TreeNode node) => CredentialResolver.ResolveDesktop(node, Index, Document.Profiles);

    public Core.Rdp.EffectiveRdpSettings ResolveRdpSettings(TreeNode node) => Core.Rdp.RdpSettingsResolver.Resolve(node, Index);

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            ConnectionStore.Save(Paths.ConnectionsFile, Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
        {
            SaveFailed?.Invoke(this, ex.Message);
        }
    }

    public void SaveSettingsNow()
    {
        _settingsTimer.Stop();
        try
        {
            JsonFileStore.Save(Paths.SettingsFile, Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
        {
            SaveFailed?.Invoke(this, ex.Message);
        }
    }

    public void Flush()
    {
        if (_saveTimer.IsEnabled) SaveNow();
        SaveSettingsNow();
    }

    public ServerNode? FindServerForPath(string path)
    {
        if (!Core.Smb.UncPath.TrySplit(path, out var target) || target is null) return null;
        ServerNode? hostOnly = null;
        foreach (var s in TreeIndex.AllServers(Document.Root))
        {
            if (!Core.Smb.UncPath.SameHost(s.Host, target.Host)) continue;
            if (s.Share is not null && target.Share is not null && string.Equals(s.Share, target.Share, StringComparison.OrdinalIgnoreCase))
                return s;
            if (s.Share is null) hostOnly ??= s;
        }
        // Fallback: any entry on the same host so the identity band still shows a friendly name.
        return hostOnly ?? TreeIndex.AllServers(Document.Root).FirstOrDefault(s => Core.Smb.UncPath.SameHost(s.Host, target.Host));
    }
}
