using System.IO;
using System.Text;
using RDMan.Core.Ssh;

namespace RDMan.Services.Ssh;

/// <summary>
/// Plain-text session log (escape sequences removed, progress bars collapsed) under RDMan.data\logs\&lt;server&gt;.
/// Written on a background task so a slow disk never slows the terminal.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly AnsiStripper _stripper = new();
    private readonly StreamWriter _writer;
    // Multi-reader flavor on purpose: it supports Reader.Count (the single-reader one throws), used to flush when idle.
    private readonly System.Threading.Channels.Channel<string> _queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private readonly Task _pump;

    private SessionLogger(string path)
    {
        Path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        _pump = Task.Run(PumpAsync);
    }

    public string Path { get; }

    public static string FolderFor(string dataDirectory, string serverName)
    {
        var safe = string.Concat(serverName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "server";
        return System.IO.Path.Combine(dataDirectory, "logs", safe);
    }

    public static SessionLogger? TryOpen(string folder, string header)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".log");
            var logger = new SessionLogger(path);
            logger._queue.Writer.TryWrite(header + "\n");
            return logger;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>Any thread; called with the decoded terminal output.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        string lines;
        lock (_stripper) lines = _stripper.Feed(text);
        if (lines.Length > 0) _queue.Writer.TryWrite(lines);
    }

    public void Note(string line) => _queue.Writer.TryWrite(line + "\n");

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var chunk in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _writer.WriteAsync(chunk).ConfigureAwait(false);
                if (_queue.Reader.Count == 0) await _writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never die silently: an empty log with no reason is worse than a logged failure.
            App.Log(ex);
        }
    }

    public void Dispose()
    {
        string rest;
        lock (_stripper) rest = _stripper.Flush();
        if (rest.Length > 0) _queue.Writer.TryWrite(rest);
        _queue.Writer.TryComplete();
        try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        try { _writer.Dispose(); } catch (IOException) { }
    }
}
