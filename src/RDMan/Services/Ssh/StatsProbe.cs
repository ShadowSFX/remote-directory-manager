using System.IO;
using System.Text;
using RDMan.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RDMan.Services.Ssh;

/// <summary>
/// Live server statistics through one extra exec channel on the terminal's connection (no second sign-in).
/// Linux only: the probe exits immediately elsewhere and <see cref="Unavailable"/> fires.
/// </summary>
public sealed class StatsProbe : IDisposable
{
    private readonly SshClient _client;
    private readonly CancellationTokenSource _cts = new();
    private SshCommand? _command;
    private StatsSample? _previous;

    public StatsProbe(SshClient client) => _client = client;

    /// <summary>Background thread: OS name (once).</summary>
    public event Action<string>? OperatingSystem;
    /// <summary>Background thread: a new sample and the CPU % since the previous one (null on the first).</summary>
    public event Action<StatsSample, double?>? Sample;
    public event Action? Unavailable;

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            _command = _client.CreateCommand(ServerStats.ProbeCommand);
            var async = _command.BeginExecute();
            using var reader = new StreamReader(_command.OutputStream, Encoding.UTF8);
            var frame = new List<string>();
            var expectOs = false;
            var sawFrame = false;
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    if (async.IsCompleted) break;
                    await Task.Delay(250, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                if (line == ServerStats.OsMarker) { expectOs = true; continue; }
                if (expectOs)
                {
                    expectOs = false;
                    OperatingSystem?.Invoke(line.Trim());
                    continue;
                }
                if (line == ServerStats.FrameMarker)
                {
                    Emit(frame);
                    frame.Clear();
                    sawFrame = true;
                    continue;
                }
                frame.Add(line);
                // Emit as soon as the frame is complete (6 lines) instead of waiting for the next marker.
                if (frame.Count == 6)
                {
                    Emit(frame);
                    frame.Clear();
                }
            }
            if (!sawFrame && !_cts.IsCancellationRequested) Unavailable?.Invoke();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SshException or IOException or InvalidOperationException)
        {
            if (!_cts.IsCancellationRequested) Unavailable?.Invoke();
        }
    }

    private void Emit(List<string> frame)
    {
        if (frame.Count == 0) return;
        if (ServerStats.ParseFrame(frame) is not { } sample) return;
        var cpu = ServerStats.CpuPercent(_previous, sample);
        _previous = sample;
        Sample?.Invoke(sample, cpu);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _command?.CancelAsync(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
        try { _command?.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
    }
}
