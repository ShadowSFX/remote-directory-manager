using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RDMan.Services;

/// <summary>
/// One RDMan per Windows user. A second launch hands its argument (e.g. "\\host\share") to the running
/// instance over a current-user-only named pipe, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _mutex;

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _name = $"RDMan.RemoteDirectoryManager.{sid}";
    }

    /// <summary>Raised on a background thread with the forwarded path ("" = just activate).</summary>
    public event Action<string>? Activated;

    /// <summary>True when this process is the primary instance.</summary>
    public bool TryAcquire(bool waitForPrevious)
    {
        _mutex = new Mutex(false, _name + ".mutex");
        bool owned;
        try
        {
            // After a self-update the previous process is still exiting: wait for it instead of handing over.
            owned = _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // previous instance crashed
        }
        if (!owned)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        var thread = new Thread(Listen) { IsBackground = true, Name = "RDMan instance pipe" };
        thread.Start();
        return true;
    }

    public bool Forward(string path)
    {
        try
        {
            AllowSetForegroundWindow(-1); // let the primary instance come to the front
            using var client = new NamedPipeClientStream(".", _name + ".pipe", PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2500);
            var bytes = Encoding.UTF8.GetBytes(path + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Listen()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_name + ".pipe", PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = reader.ReadLine() ?? "";
                if (line.Length > 32_767) line = ""; // ignore garbage
                Activated?.Invoke(line.Trim());
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
