using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RDMan.Services.Ssh;

/// <summary>Size of the remote pseudo-terminal.</summary>
public readonly record struct PtySize(uint Columns, uint Rows, uint PixelWidth, uint PixelHeight);

/// <summary>
/// The interactive channel of a terminal session. <see cref="Data"/> is raised on SSH.NET's receive thread; a slow
/// handler slows the server down (that's the backpressure), it never loses data.
/// </summary>
public abstract class TerminalChannel : IDisposable
{
    public event Action<ArraySegment<byte>>? Data;
    /// <summary>The remote side closed the channel (exit, logout, connection gone).</summary>
    public event Action? Closed;

    /// <summary>True when the channel runs RDMan's command (prompt injection) instead of the plain login shell.</summary>
    public bool RunsCommand { get; protected set; }

    public abstract void Write(byte[] data, int offset, int count);

    public abstract void Resize(PtySize size);

    public abstract void Dispose();

    protected void RaiseData(ArraySegment<byte> segment) => Data?.Invoke(segment);

    protected void RaiseClosed() => Closed?.Invoke();

    public static readonly Dictionary<TerminalModes, uint> Modes = new()
    {
        // UTF-8 input editing (backspace over multi-byte characters) and a sane erase key.
        [TerminalModes.IUTF8] = 1,
        [TerminalModes.VERASE] = 0x7f,
    };

    /// <summary>
    /// Opens a PTY channel. With <paramref name="command"/>, runs it (exec + pty) so RDMan can start bash with its
    /// prompt; when that isn't possible, falls back to the plain login shell and <see cref="RunsCommand"/> is false.
    /// </summary>
    public static TerminalChannel Open(SshClient client, string terminalType, PtySize size, string? command, IReadOnlyDictionary<string, string> environment)
    {
        if (command is not null || environment.Count > 0)
        {
            try
            {
                return new ExecChannel(client, terminalType, size, command, environment);
            }
            catch (Exception ex) when (ex is MissingMemberException or TargetInvocationException or InvalidCastException or NotSupportedException or ArgumentException or SshException)
            {
                // SSH.NET internals changed: degrade to the public shell API (no injected prompt).
                App.Log(ex);
            }
        }
        return new ShellChannel(client, terminalType, size);
    }

    /// <summary>
    /// SSH.NET's public API only opens a login shell. This drives its internal session channel (pinned SSH.NET version)
    /// to send env + pty + exec/shell requests ourselves, exactly like OpenSSH does for <c>ssh -t host command</c>.
    /// </summary>
    private sealed class ExecChannel : TerminalChannel
    {
        private static readonly Assembly SshNet = typeof(SshClient).Assembly;
        private readonly object _channel;
        private MethodInfo? _sendData;
        private MethodInfo? _windowChange;
        private readonly List<(EventInfo Event, Delegate Handler)> _handlers = new();
        private int _closed;

        public ExecChannel(SshClient client, string terminalType, PtySize size, string? command, IReadOnlyDictionary<string, string> environment)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var sessionProp = typeof(BaseClient).GetProperty("Session", flags) ?? throw new MissingMemberException("BaseClient.Session");
            var session = sessionProp.GetValue(client) ?? throw new NotSupportedException("no session");
            var createChannel = sessionProp.PropertyType.GetMethod("CreateChannelSession") ?? throw new MissingMemberException("ISession.CreateChannelSession");
            var channelType = createChannel.ReturnType; // IChannelSession
            var baseChannelType = SshNet.GetType("Renci.SshNet.Channels.IChannel") ?? throw new MissingMemberException("IChannel");
            _channel = createChannel.Invoke(session, null) ?? throw new NotSupportedException("no channel");
            try
            {
                Start(channelType, baseChannelType, terminalType, size, command, environment);
            }
            catch
            {
                Dispose(); // a refused pty/exec must not leave the channel open on the session
                throw;
            }
        }

        private void Start(Type channelType, Type baseChannelType, string terminalType, PtySize size, string? command, IReadOnlyDictionary<string, string> environment)
        {
            MethodInfo M(Type t, string name, params Type[] args) => t.GetMethod(name, args) ?? throw new MissingMemberException(t.Name + "." + name);
            _sendData = M(baseChannelType, "SendData", typeof(byte[]), typeof(int), typeof(int));
            _windowChange = M(channelType, "SendWindowChangeRequest", typeof(uint), typeof(uint), typeof(uint), typeof(uint));

            Subscribe(baseChannelType, "DataReceived", e => RaiseData(DataOf(e)));
            Subscribe(baseChannelType, "ExtendedDataReceived", e => RaiseData(DataOf(e)));
            Subscribe(baseChannelType, "Closed", _ => OnClosed());

            M(channelType, "Open").Invoke(_channel, null);
            var env = M(channelType, "SendEnvironmentVariableRequest", typeof(string), typeof(string));
            foreach (var (k, v) in environment)
            {
                try { env.Invoke(_channel, [k, v]); }
                catch (TargetInvocationException) { } // servers refuse variables they don't AcceptEnv: fine
            }
            var ptyOk = (bool)M(channelType, "SendPseudoTerminalRequest", typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<TerminalModes, uint>))
                .Invoke(_channel, [terminalType, size.Columns, size.Rows, size.PixelWidth, size.PixelHeight, Modes])!;
            if (!ptyOk) throw new NotSupportedException("pty refused");

            var started = command is null
                ? (bool)M(channelType, "SendShellRequest").Invoke(_channel, null)!
                : (bool)M(channelType, "SendExecRequest", typeof(string)).Invoke(_channel, [command])!;
            if (!started) throw new SshException("The server refused to start the session.");
            RunsCommand = command is not null;
        }

        private static ArraySegment<byte> DataOf(object args)
        {
            var prop = args.GetType().GetProperty("Data");
            return prop?.GetValue(args) is ArraySegment<byte> seg ? seg : default;
        }

        private void Subscribe(Type channelType, string eventName, Action<object> handler)
        {
            var ev = channelType.GetEvent(eventName) ?? throw new MissingMemberException(eventName);
            var del = MakeHandler(ev.EventHandlerType!, handler);
            ev.AddEventHandler(_channel, del);
            _handlers.Add((ev, del));
        }

        /// <summary>Builds an EventHandler&lt;internal args&gt; that forwards the args as object.</summary>
        private static Delegate MakeHandler(Type handlerType, Action<object> handler)
        {
            var invoke = handlerType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var call = Expression.Invoke(Expression.Constant(handler), Expression.Convert(ps[1], typeof(object)));
            return Expression.Lambda(handlerType, call, ps).Compile();
        }

        private void OnClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) RaiseClosed();
        }

        public override void Write(byte[] data, int offset, int count)
        {
            try { _sendData?.Invoke(_channel, [data, offset, count]); }
            catch (TargetInvocationException ex) when (ex.InnerException is SshException or ObjectDisposedException or InvalidOperationException) { OnClosed(); }
        }

        public override void Resize(PtySize size)
        {
            try { _windowChange?.Invoke(_channel, [size.Columns, size.Rows, size.PixelWidth, size.PixelHeight]); }
            catch (TargetInvocationException) { }
        }

        public override void Dispose()
        {
            foreach (var (ev, del) in _handlers)
            {
                try { ev.RemoveEventHandler(_channel, del); } catch (TargetInvocationException) { }
            }
            _handlers.Clear();
            // The internal channel type implements IDisposable (IChannelSession : IChannel : IDisposable).
            try { (_channel as IDisposable)?.Dispose(); }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    /// <summary>Public-API fallback: SSH.NET's ShellStream read on a background loop.</summary>
    private sealed class ShellChannel : TerminalChannel
    {
        private readonly ShellStream _stream;
        private readonly CancellationTokenSource _cts = new();
        private int _closed;

        public ShellChannel(SshClient client, string terminalType, PtySize size)
        {
            _stream = client.CreateShellStream(terminalType, size.Columns, size.Rows, size.PixelWidth, size.PixelHeight, 64 * 1024, Modes);
            _stream.Closed += (_, _) => OnClosed();
            _ = Task.Run(ReadLoop);
        }

        private async Task ReadLoop()
        {
            var buffer = new byte[32 * 1024];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await _stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    RaiseData(new ArraySegment<byte>(buffer, 0, n));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SshException or IOException) { }
            OnClosed();
        }

        private void OnClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) RaiseClosed();
        }

        public override void Write(byte[] data, int offset, int count)
        {
            try
            {
                _stream.Write(data, offset, count);
                _stream.Flush();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException) { OnClosed(); }
        }

        public override void Resize(PtySize size)
        {
            try { _stream.ChangeWindowSize(size.Columns, size.Rows, size.PixelWidth, size.PixelHeight); }
            catch (Exception ex) when (ex is ObjectDisposedException or SshException) { }
        }

        public override void Dispose()
        {
            _cts.Cancel();
            try { _stream.Dispose(); } catch (Exception ex) when (ex is ObjectDisposedException or SshException) { }
        }
    }
}
