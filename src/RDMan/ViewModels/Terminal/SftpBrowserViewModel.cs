using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.Services.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace RDMan.ViewModels.Terminal;

public sealed class SftpEntry
{
    public SftpEntry(ISftpFile f)
    {
        Name = f.Name;
        FullName = f.FullName;
        IsDirectory = f.IsDirectory;
        IsLink = f.IsSymbolicLink;
        Length = f.Length;
        Modified = f.LastWriteTime;
        Permissions = (f.IsDirectory ? "d" : f.IsSymbolicLink ? "l" : "-")
            + (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-")
            + (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-")
            + (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-");
    }

    public string Name { get; }
    public string FullName { get; }
    public bool IsDirectory { get; }
    public bool IsLink { get; }
    public long Length { get; }
    public DateTime Modified { get; }
    public string Permissions { get; }
    public bool IsHidden => Name.StartsWith('.');
    public string Glyph => IsDirectory ? "" : IsLink ? "" : "";
    public string SizeText => IsDirectory ? "" : FormatSize(Length);
    public string ModifiedText => Modified.ToString("g", CultureInfo.CurrentCulture);

    public static string FormatSize(long bytes)
    {
        double v = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[u];
    }
}

/// <summary>A running or finished upload/download, shown at the bottom of the panel.</summary>
public sealed partial class SftpTransfer : ObservableObject
{
    public SftpTransfer(string name, bool upload, long total)
    {
        Name = name;
        IsUpload = upload;
        Total = total;
    }

    public string Name { get; }
    public bool IsUpload { get; }
    public long Total { get; }
    public CancellationTokenSource Cancel { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(ProgressText))]
    private long _done;

    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private string? _error;

    public double Percent => Total > 0 ? 100.0 * Done / Total : 0;
    public string ProgressText => Total > 0 ? $"{SftpEntry.FormatSize(Done)} / {SftpEntry.FormatSize(Total)}" : SftpEntry.FormatSize(Done);
    public string Glyph => IsUpload ? "" : "";

    [RelayCommand]
    private void Stop() => Cancel.Cancel();
}

/// <summary>
/// The SFTP side panel of a terminal tab: its own SFTP connection (same target and sign-in as the terminal, prompts
/// appear in the terminal), folder listing, transfers, and "edit": download, open with the Windows app, upload on save.
/// </summary>
public sealed partial class SftpBrowserViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly SshSessionViewModel _session;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private SshLink<SftpClient>? _link;
    private Task? _connecting;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string _tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RDMan", "sftp", Guid.NewGuid().ToString("N"));
    private bool _disposed;

    public SftpBrowserViewModel(MainViewModel main, SshSessionViewModel session)
    {
        _main = main;
        _session = session;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SshSessionViewModel.CurrentDirectory) && FollowTerminal && _session.CurrentDirectory is { Length: > 0 } cwd)
                _ = NavigateAsync(cwd);
        };
    }

    public ObservableCollection<SftpEntry> Entries { get; } = new();
    public ObservableCollection<SftpTransfer> Transfers { get; } = new();

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private SftpEntry? _selected;
    [ObservableProperty] private bool _followTerminal = true;
    [ObservableProperty] private bool _showHidden;

    public bool HasTransfers => Transfers.Count > 0;
    public bool IsEmpty => !IsBusy && Error is null && Entries.Count == 0;

    partial void OnShowHiddenChanged(bool value) => _ = RefreshAsync();

    partial void OnFollowTerminalChanged(bool value)
    {
        if (value && _session.CurrentDirectory is { Length: > 0 } cwd) _ = NavigateAsync(cwd);
    }

    // ───────────── Connection ─────────────

    private Task EnsureConnectedAsync()
    {
        if (_link is { Client.IsConnected: true }) return Task.CompletedTask;
        return _connecting ??= ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            _link?.Dispose();
            _link = null;
            var request = _session.CreateConnectRequest();
            _link = await Task.Run(() => SshConnector.ConnectAsync(request, ci => new SftpClient(ci), CancellationToken.None));
        }
        finally
        {
            _connecting = null;
        }
    }

    private async Task<T> RunAsync<T>(Func<SftpClient, T> work)
    {
        await EnsureConnectedAsync();
        var client = _link!.Client;
        return await Task.Run(() => work(client));
    }

    // ───────────── Browsing ─────────────

    public async Task OpenAsync()
    {
        if (Entries.Count > 0 || IsBusy) return;
        var start = FollowTerminal && _session.CurrentDirectory is { Length: > 0 } cwd ? cwd : null;
        await NavigateAsync(start);
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private Task RefreshAsync() => NavigateAsync(Path.Length > 0 ? Path : null);

    [RelayCommand]
    private Task Up()
    {
        if (Path is "" or "/") return Task.CompletedTask;
        var parent = Path.TrimEnd('/');
        var i = parent.LastIndexOf('/');
        return NavigateAsync(i <= 0 ? "/" : parent[..i]);
    }

    [RelayCommand]
    private Task GoToPath() => NavigateAsync(PathText.Trim());

    [RelayCommand]
    private Task Open(SftpEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return Task.CompletedTask;
        return entry.IsDirectory || entry.IsLink && !entry.Name.Contains('.') ? NavigateAsync(entry.FullName) : EditAsync(entry);
    }

    public async Task NavigateAsync(string? path)
    {
        if (_disposed) return;
        IsBusy = true;
        Error = null;
        try
        {
            var (resolved, files) = await RunAsync(client =>
            {
                var target = string.IsNullOrWhiteSpace(path) ? client.WorkingDirectory : path!;
                var list = client.ListDirectory(target).Where(f => f.Name is not ("." or "..")).Select(f => new SftpEntry(f)).ToList();
                return (target, list);
            });
            Path = resolved;
            PathText = resolved;
            Entries.Clear();
            foreach (var e in files.Where(f => ShowHidden || !f.IsHidden)
                         .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                Entries.Add(e);
        }
        catch (Exception ex) when (ex is SshException or IOException or SshConnectException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = ex is SftpPathNotFoundException ? Loc.F("Sftp.NotFound", path ?? "") :
                    ex is SftpPermissionDeniedException ? Loc.F("Sftp.Denied", path ?? "") :
                    ex is SshConnectException { Failure: SshFailure.ShellRejected or SshFailure.Unknown } ? Loc.T("Sftp.NoSubsystem") : ex.Message;
            PathText = Path;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    // ───────────── Transfers ─────────────

    /// <summary>Files dropped from Explorer (or picked) go into the current folder.</summary>
    public async Task UploadAsync(IEnumerable<string> localPaths)
    {
        foreach (var local in localPaths)
        {
            if (Directory.Exists(local))
            {
                _main.Toasts.Show(Loc.F("Sftp.FolderNotSupported", System.IO.Path.GetFileName(local)));
                continue;
            }
            if (!File.Exists(local)) continue;
            var name = System.IO.Path.GetFileName(local);
            var remote = Combine(Path, name);
            if (Entries.Any(e => e.Name == name) &&
                !Views.Dialogs.Confirm(_main.Owner, Loc.T("Sftp.Upload"), Loc.F("Sftp.ConfirmOverwrite", name), Loc.T("Sftp.Overwrite"), danger: true))
                continue;
            var transfer = new SftpTransfer(name, upload: true, new FileInfo(local).Length);
            Transfers.Add(transfer);
            OnPropertyChanged(nameof(HasTransfers));
            await RunTransferAsync(transfer, client =>
            {
                using var stream = File.OpenRead(local);
                client.UploadFile(stream, remote, true, done => Progress(transfer, (long)done));
            });
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UploadPicked()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Loc.T("Sftp.Upload") };
        if (dlg.ShowDialog(_main.Owner) == true) await UploadAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task Download(SftpEntry? entry)
    {
        entry ??= Selected;
        if (entry is null || entry.IsDirectory) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = entry.Name, Title = Loc.T("Sftp.Download") };
        if (dlg.ShowDialog(_main.Owner) != true) return;
        var transfer = new SftpTransfer(entry.Name, upload: false, entry.Length);
        Transfers.Add(transfer);
        OnPropertyChanged(nameof(HasTransfers));
        var ok = await RunTransferAsync(transfer, client =>
        {
            using var stream = File.Create(dlg.FileName);
            client.DownloadFile(entry.FullName, stream, done => Progress(transfer, (long)done));
        });
        if (ok) _main.Toasts.Show(Loc.F("Sftp.Downloaded", entry.Name), ToastKind.Success, Loc.T("Sftp.ShowInFolder"),
            () => ShellLauncher.OpenFolder(System.IO.Path.GetDirectoryName(dlg.FileName)!));
    }

    /// <summary>Downloads to a private temp folder, opens it with its Windows app and uploads every save back.</summary>
    [RelayCommand]
    private async Task EditAsync(SftpEntry? entry)
    {
        entry ??= Selected;
        if (entry is null || entry.IsDirectory) return;
        if (entry.Length > 64L * 1024 * 1024)
        {
            _main.Toasts.Show(Loc.T("Sftp.TooBigToEdit"));
            return;
        }
        var folder = System.IO.Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var local = System.IO.Path.Combine(folder, entry.Name);
        var transfer = new SftpTransfer(entry.Name, upload: false, entry.Length);
        Transfers.Add(transfer);
        OnPropertyChanged(nameof(HasTransfers));
        var ok = await RunTransferAsync(transfer, client =>
        {
            using var stream = File.Create(local);
            client.DownloadFile(entry.FullName, stream, done => Progress(transfer, (long)done));
        });
        if (!ok) return;
        Watch(local, entry.FullName);
        try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true })?.Dispose();
        }
        _main.Toasts.Show(Loc.F("Sftp.Editing", entry.Name));
    }

    private void Watch(string local, string remote)
    {
        var watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(local)!, System.IO.Path.GetFileName(local))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (_disposed || !File.Exists(local)) return;
            var name = System.IO.Path.GetFileName(local);
            var transfer = new SftpTransfer(name, upload: true, new FileInfo(local).Length);
            Transfers.Add(transfer);
            OnPropertyChanged(nameof(HasTransfers));
            var ok = await RunTransferAsync(transfer, client =>
            {
                // Editors keep the file open while saving: retry briefly until it can be read.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        client.UploadFile(stream, remote, true, done => Progress(transfer, (long)done));
                        return;
                    }
                    catch (IOException) when (attempt < 10) { Thread.Sleep(200); }
                }
            });
            if (ok) _main.Toasts.Show(Loc.F("Sftp.Uploaded", name), ToastKind.Success);
        };
        // Editors save as delete+rename; react to every kind of change.
        void Changed(object s, FileSystemEventArgs e) => _ui.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (s, e) => Changed(s, e);
        _watchers.Add(watcher);
    }

    private void Progress(SftpTransfer t, long done)
    {
        if (t.Cancel.IsCancellationRequested) throw new OperationCanceledException();
        _ui.BeginInvoke(() => t.Done = done, DispatcherPriority.Background);
    }

    private async Task<bool> RunTransferAsync(SftpTransfer t, Action<SftpClient> work)
    {
        try
        {
            await RunAsync<bool>(client =>
            {
                work(client);
                return true;
            });
            t.Done = Math.Max(t.Done, t.Total);
            return true;
        }
        catch (OperationCanceledException)
        {
            t.Error = Loc.T("Sftp.Cancelled");
            return false;
        }
        catch (Exception ex) when (ex is SshException or IOException or SshConnectException or UnauthorizedAccessException or InvalidOperationException)
        {
            t.Error = ex is SftpPermissionDeniedException ? Loc.T("Sftp.DeniedShort") : ex.Message;
            return false;
        }
        finally
        {
            t.IsFinished = true;
            // Finished transfers fade out of the list after a moment (errors stay until closed).
            if (t.Error is null)
                _ = Task.Delay(4000).ContinueWith(_ => _ui.BeginInvoke(() => { Transfers.Remove(t); OnPropertyChanged(nameof(HasTransfers)); }));
        }
    }

    [RelayCommand]
    private void DismissTransfer(SftpTransfer? t)
    {
        if (t is null) return;
        t.Cancel.Cancel();
        Transfers.Remove(t);
        OnPropertyChanged(nameof(HasTransfers));
    }

    // ───────────── File operations ─────────────

    [RelayCommand]
    private async Task NewFolder()
    {
        var prompt = new Views.TextPromptWindow(Loc.T("Sftp.NewFolder"), "", Loc.T("Snippets.Name"), Loc.T("Sftp.Create"));
        if (Views.Dialogs.Show(prompt, _main.Owner) != true || string.IsNullOrWhiteSpace(prompt.Value) || prompt.Value.Contains('/')) return;
        await Operate(client => client.CreateDirectory(Combine(Path, prompt.Value.Trim())));
    }

    [RelayCommand]
    private async Task Rename(SftpEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        var prompt = new Views.TextPromptWindow(Loc.T("Sftp.Rename"), "", Loc.T("Snippets.Name"), Loc.T("Sftp.Rename"), entry.Name);
        if (Views.Dialogs.Show(prompt, _main.Owner) != true) return;
        var name = prompt.Value.Trim();
        if (name.Length == 0 || name == entry.Name || name.Contains('/')) return;
        await Operate(client => client.RenameFile(entry.FullName, Combine(Path, name)));
    }

    [RelayCommand]
    private async Task Delete(SftpEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        if (!Views.Dialogs.Confirm(_main.Owner, Loc.T("Common.Delete"), Loc.F(entry.IsDirectory ? "Sftp.ConfirmDeleteFolder" : "Sftp.ConfirmDelete", entry.Name), Loc.T("Common.Delete"), danger: true))
            return;
        await Operate(client =>
        {
            if (entry.IsDirectory) client.DeleteDirectory(entry.FullName); // SFTP removes empty folders only
            else client.DeleteFile(entry.FullName);
        });
    }

    [RelayCommand]
    private void CopyPath(SftpEntry? entry)
    {
        var text = (entry ?? Selected)?.FullName ?? Path;
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Types "cd &lt;folder&gt;" into the terminal.</summary>
    [RelayCommand]
    private void CdHere()
    {
        if (Path.Length == 0) return;
        _session.SendInput("cd '" + Path.Replace("'", "'\\''") + "'\r");
        _session.Focus();
    }

    private async Task Operate(Action<SftpClient> work)
    {
        try
        {
            await RunAsync<bool>(c => { work(c); return true; });
        }
        catch (Exception ex) when (ex is SshException or IOException or SshConnectException or UnauthorizedAccessException or InvalidOperationException)
        {
            _main.Toasts.Show(ex is SftpPermissionDeniedException ? Loc.T("Sftp.DeniedShort") : ex.Message, ToastKind.Error);
        }
        await RefreshAsync();
    }

    private static string Combine(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var t in Transfers) t.Cancel.Cancel();
        var link = _link;
        _link = null;
        _ = Task.Run(() => link?.Dispose());
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a file is still open in an editor
    }
}
