using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Persistence;
using RDMan.Core.Updates;

namespace RDMan.Services;

public enum UpdateStatus { Idle, Checking, UpToDate, Downloading, Ready, NotWritable, Failed, DevBuild }

/// <summary>
/// GitHub Releases self-updater for the single-file exe:
/// check → download next to the exe → verify SHA-256 → stage as RDMan.exe.new → swap on restart/exit.
/// Renaming a running exe is allowed on Windows, which is what makes an in-place swap possible.
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    public const string Owner = "ShadowSFX";
    public const string Repo = "remote-directory-manager";
    public static readonly string ReleasesUrl = $"https://github.com/{Owner}/{Repo}/releases";
    private static readonly string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private readonly HttpClient _http;
    private readonly Func<int> _runningOperations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private string? _etag;
    private PeriodicTimer? _timer;

    [ObservableProperty] private UpdateStatus _status = UpdateStatus.Idle;
    [ObservableProperty] private ReleaseInfo? _readyRelease;
    [ObservableProperty] private ReleaseInfo? _availableRelease;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private double _progress;

    public UpdateService(Func<int> runningOperations)
    {
        _runningOperations = runningOperations;
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RDMan-Updater", CurrentVersion.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static SemVersion CurrentVersion { get; } = ReadVersion();

    public static string CurrentVersionText => CurrentVersion.ToString();

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RDMan.exe");

    private static string ExeDir => Path.GetDirectoryName(ExePath)!;
    private static string StagedPath => Path.Combine(ExeDir, "RDMan.exe.new");
    private static string DownloadPath => Path.Combine(ExeDir, "RDMan.exe.download");
    private static string OldPath => Path.Combine(ExeDir, "RDMan.exe.old");
    private static string StagedInfoPath => Path.Combine(ExeDir, "RDMan.exe.new.version");

    private static SemVersion ReadVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemVersion.TryParse(info, out var v) ? v! : new SemVersion(0, 0, 0, "dev");
    }

    /// <summary>True when running from a published single-file exe (not "dotnet run" / a debugger build folder).</summary>
    private static bool IsPublishedExe
        => string.Equals(Path.GetFileName(ExePath), "RDMan.exe", StringComparison.OrdinalIgnoreCase)
           && !File.Exists(Path.Combine(ExeDir, "RDMan.dll"));

    /// <summary>Startup housekeeping: remove the previous exe and leftovers from interrupted downloads.</summary>
    public static void CleanupLeftovers()
    {
        foreach (var f in new[] { OldPath, DownloadPath })
        {
            for (var i = 0; i < 10 && File.Exists(f); i++)
            {
                try { File.Delete(f); }
                catch (IOException) { Thread.Sleep(200); } // old process may still be exiting
                catch (UnauthorizedAccessException) { break; }
            }
        }
        // A staged update that is not newer than us (e.g. user replaced the exe manually) is stale.
        try
        {
            if (File.Exists(StagedPath))
            {
                var staged = File.Exists(StagedInfoPath) && SemVersion.TryParse(File.ReadAllText(StagedInfoPath), out var sv) ? sv : null;
                if (staged is null || !(staged > CurrentVersion))
                {
                    File.Delete(StagedPath);
                    File.Delete(StagedInfoPath);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void StartBackgroundChecks(Func<bool> enabled)
    {
        _timer = new PeriodicTimer(TimeSpan.FromHours(6));
        var timer = _timer;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), _cts.Token).ConfigureAwait(false); // let startup settle
                if (enabled()) await CheckAsync(userInitiated: false).ConfigureAwait(false);
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                    if (enabled()) await CheckAsync(userInitiated: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!IsPublishedExe || CurrentVersion.Prerelease == "dev")
            {
                SetStatus(UpdateStatus.DevBuild);
                return;
            }
            if (Status == UpdateStatus.Ready) return;

            SetStatus(UpdateStatus.Checking);
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            // The ETag only short-circuits checks that previously ended "up to date"; a release we couldn't install yet is always re-fetched.
            if (!userInitiated && _etag is not null && AvailableRelease is null) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));
            using var resp = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotModified) { SetStatus(UpdateStatus.UpToDate); return; }
            if (resp.StatusCode == HttpStatusCode.NotFound) { SetStatus(UpdateStatus.UpToDate); return; } // no releases yet
            resp.EnsureSuccessStatusCode();
            var etag = resp.Headers.ETag?.Tag;
            var json = await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
            var release = UpdatePlanner.ParseRelease(json);
            var plan = UpdatePlanner.Plan(CurrentVersion, release);
            if (plan is null)
            {
                _etag = etag;
                SetStatus(UpdateStatus.UpToDate);
                return;
            }

            Post(() => AvailableRelease = plan.Release);
            if (!AppPaths.ProbeWritable(ExeDir)) { SetStatus(UpdateStatus.NotWritable); return; }

            await DownloadAsync(plan).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            Post(() => LastError = ex.Message);
            SetStatus(UpdateStatus.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(UpdatePlan plan)
    {
        SetStatus(UpdateStatus.Downloading);
        var expected = UpdatePlanner.ParseChecksum(await _http.GetStringAsync(plan.Checksum.DownloadUrl, _cts.Token).ConfigureAwait(false))
                       ?? throw new InvalidDataException("Invalid checksum file");

        using (var resp = await _http.GetAsync(plan.Executable.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? plan.Executable.Size;
            await using var input = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            await using var output = new FileStream(DownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), _cts.Token).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    var p = (double)done / total;
                    Post(() => Progress = p);
                }
            }
        }

        var actual = await UpdatePlanner.ComputeSha256Async(DownloadPath, _cts.Token).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(DownloadPath);
            throw new InvalidDataException("SHA-256 mismatch");
        }

        File.Move(DownloadPath, StagedPath, overwrite: true);
        await File.WriteAllTextAsync(StagedInfoPath, plan.Release.Version.ToString(), _cts.Token).ConfigureAwait(false);
        Post(() => ReadyRelease = plan.Release);
        SetStatus(UpdateStatus.Ready);
    }

    public bool CanApplyNow => Status == UpdateStatus.Ready && _runningOperations() == 0;

    /// <summary>
    /// Swaps RDMan.exe with the staged file. Call on exit (relaunch=false) or from "Restart now" (relaunch=true).
    /// Returns false if nothing was applied (no staged update, copies running, or the swap failed — then the old exe is restored).
    /// </summary>
    public bool TryApply(bool relaunch)
    {
        if (!File.Exists(StagedPath) || _runningOperations() > 0) return false;
        try
        {
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);
            try
            {
                File.Move(StagedPath, ExePath);
            }
            catch (Exception swapError) when (swapError is IOException or UnauthorizedAccessException)
            {
                // Roll back so the user is never left without an exe, and report the original cause.
                try { File.Move(OldPath, ExePath); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { App.Log(rollbackError); }
                throw;
            }
            try { File.Delete(StagedInfoPath); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(() => LastError = ex.Message);
            return false;
        }

        // The new exe is in place from here on: a failed relaunch is not a failed update.
        if (relaunch)
        {
            try { Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = ExeDir, Arguments = "--updated" })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { App.Log(ex); }
        }
        return true;
    }

    private void SetStatus(UpdateStatus s) => Post(() => Status = s);

    private static void Post(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.HasShutdownStarted) return;
        if (d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _http.Dispose();
    }
}
