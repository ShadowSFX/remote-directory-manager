using System.Collections.Concurrent;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Smb;

namespace RDMan.Services;

public enum HealthState { Unknown, Online, Offline }

/// <summary>Live reachability of one host:port. Shared by every server entry pointing to that endpoint.</summary>
public sealed partial class HostHealth : ObservableObject
{
    public HostHealth(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }

    [ObservableProperty] private HealthState _state = HealthState.Unknown;
    [ObservableProperty] private int? _latencyMs;
    [ObservableProperty] private DateTime? _lastChecked;
    [ObservableProperty] private bool _isChecking;
}

/// <summary>Periodically probes every known endpoint (SMB 445 and/or the RDP port) with limited concurrency, and on demand.</summary>
public sealed class HealthMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, HostHealth> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(8, 8);
    private readonly CancellationTokenSource _cts = new();
    private Func<IEnumerable<(string Host, int Port)>> _endpointSource = () => [];
    private TimeSpan _interval = TimeSpan.FromSeconds(30);
    private PeriodicTimer? _timer;
    private Task? _loop;

    public HealthMonitor(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public HostHealth Get(string host, int port = ConnectionService.SmbPort)
    {
        var h = Normalize(host);
        return _hosts.GetOrAdd($"{h}|{port}", _ => new HostHealth(h, port));
    }

    private static string Normalize(string host) => (host ?? "").Trim().ToLowerInvariant();

    public void Start(Func<IEnumerable<(string Host, int Port)>> endpointSource, int intervalSeconds)
    {
        _endpointSource = endpointSource;
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
        (string Host, int Port)[] endpoints;
        try
        {
            endpoints = _dispatcher.Invoke(() => _endpointSource()
                .Where(e => UncPath.IsValidHost(e.Host))
                .Select(e => (Normalize(e.Host), e.Port))
                .Distinct()
                .ToArray());
        }
        catch (TaskCanceledException) { return Task.CompletedTask; }
        return Task.WhenAll(endpoints.Select(e => CheckAsync(e.Item1, e.Item2)));
    }

    public async Task CheckAsync(string host, int port = ConnectionService.SmbPort)
    {
        if (_cts.IsCancellationRequested) return;
        var health = Get(host, port);
        try
        {
            await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            Post(() => health.IsChecking = true);
            var latency = await ConnectionService.ProbeAsync(health.Host, health.Port, ConnectionService.ProbeTimeout, _cts.Token).ConfigureAwait(false);
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
