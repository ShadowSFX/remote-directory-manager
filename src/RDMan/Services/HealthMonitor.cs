using System.Collections.Concurrent;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Smb;

namespace RDMan.Services;

public enum HealthState { Unknown, Online, Offline }

/// <summary>Live reachability of one host. Shared by every server entry pointing to that host.</summary>
public sealed partial class HostHealth : ObservableObject
{
    public HostHealth(string host) => Host = host;

    public string Host { get; }

    [ObservableProperty] private HealthState _state = HealthState.Unknown;
    [ObservableProperty] private int? _latencyMs;
    [ObservableProperty] private DateTime? _lastChecked;
    [ObservableProperty] private bool _isChecking;
}

/// <summary>Periodically probes TCP 445 on every known host (limited concurrency) and on demand.</summary>
public sealed class HealthMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, HostHealth> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(8, 8);
    private readonly CancellationTokenSource _cts = new();
    private Func<IEnumerable<string>> _hostSource = () => [];
    private TimeSpan _interval = TimeSpan.FromSeconds(30);
    private PeriodicTimer? _timer;
    private Task? _loop;

    public HealthMonitor(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public HostHealth Get(string host)
    {
        var key = Normalize(host);
        return _hosts.GetOrAdd(key, k => new HostHealth(k));
    }

    private static string Normalize(string host) => (host ?? "").Trim().ToLowerInvariant();

    public void Start(Func<IEnumerable<string>> hostSource, int intervalSeconds)
    {
        _hostSource = hostSource;
        SetInterval(intervalSeconds);
    }

    public void SetInterval(int seconds)
    {
        _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));
        _timer?.Dispose();
        _timer = new PeriodicTimer(_interval);
        var timer = _timer;
        _loop = Task.Run(async () =>
        {
            await CheckAllAsync().ConfigureAwait(false);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                    await CheckAllAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public Task CheckAllAsync()
    {
        string[] hosts;
        try
        {
            hosts = _dispatcher.Invoke(() => _hostSource().Where(UncPath.IsValidHost).Select(Normalize).Distinct().ToArray());
        }
        catch (TaskCanceledException) { return Task.CompletedTask; }
        return Task.WhenAll(hosts.Select(CheckAsync));
    }

    public async Task CheckAsync(string host)
    {
        if (_cts.IsCancellationRequested) return;
        var health = Get(host);
        try
        {
            await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            Post(() => health.IsChecking = true);
            var latency = await ConnectionService.ProbeAsync(health.Host, ConnectionService.ProbeTimeout, _cts.Token).ConfigureAwait(false);
            Post(() =>
            {
                health.State = latency is null ? HealthState.Offline : HealthState.Online;
                health.LatencyMs = latency;
                health.LastChecked = DateTime.Now;
                health.IsChecking = false;
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _concurrency.Release();
        }
    }

    private void Post(Action a)
    {
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(a, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
    }
}
