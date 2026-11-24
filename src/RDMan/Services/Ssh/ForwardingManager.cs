using System.Collections.ObjectModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Infrastructure;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RDMan.Services.Ssh;

public enum ForwardState { Stopped, Listening, Failed }

/// <summary>One port forward of a terminal session, shown as a chip in the terminal toolbar.</summary>
public sealed partial class ForwardViewModel : ObservableObject
{
    private readonly ForwardingManager _owner;

    public ForwardViewModel(ForwardingManager owner, SshForward forward)
    {
        _owner = owner;
        Forward = forward;
    }

    public SshForward Forward { get; }
    internal ForwardedPort? Port { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening), nameof(IsFailed), nameof(ToolTip))]
    private ForwardState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string? _problem;

    public bool IsListening => State == ForwardState.Listening;
    public bool IsFailed => State == ForwardState.Failed;

    public string Label => string.IsNullOrWhiteSpace(Forward.Label) ? Short : Forward.Label!;

    /// <summary>"5432 → db:5432", "SOCKS 1080", "9000 ← :3000".</summary>
    public string Short => Forward.Kind switch
    {
        SshForwardKind.Dynamic => $"SOCKS {Forward.BindPort}",
        SshForwardKind.Remote => $"{Forward.BindPort} ← {Forward.TargetHost}:{Forward.TargetPort}",
        _ => $"{Forward.BindPort} → {Forward.TargetHost}:{Forward.TargetPort}",
    };

    public string LocalAddress => $"{BindHost}:{Forward.BindPort}";

    private string BindHost => string.IsNullOrWhiteSpace(Forward.BindAddress) || Forward.BindAddress is "*" or "0.0.0.0" ? "localhost" : Forward.BindAddress!;

    public string ToolTip => State switch
    {
        ForwardState.Listening => Loc.F("Term.Forward.Listening", Forward.ToString()),
        ForwardState.Failed => Loc.F("Term.Forward.Failed", Forward.ToString(), Problem ?? ""),
        _ => Loc.F("Term.Forward.Stopped", Forward.ToString()),
    };

    /// <summary>Web-ish ports open in the browser from the chip menu.</summary>
    public bool LooksLikeWeb => Forward.Kind == SshForwardKind.Local
        && (Forward.TargetPort is 80 or 443 or 3000 or 5000 or 5173 or 8000 or 8080 or 8443 or 8888 or 9000 or 9090 || Forward.BindPort is 80 or 443 or 8080);

    [RelayCommand]
    private void Toggle()
    {
        if (IsListening) _owner.Stop(this);
        else _owner.Start(this);
    }

    [RelayCommand]
    private void CopyAddress()
    {
        try { System.Windows.Clipboard.SetText(LocalAddress); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        var scheme = Forward.TargetPort is 443 or 8443 ? "https" : "http";
        ShellLauncher.OpenUrl($"{scheme}://{LocalAddress}/");
    }
}

/// <summary>Starts and stops a session's port forwards on its SSH client; failures stay visible per chip.</summary>
public sealed class ForwardingManager : IDisposable
{
    private SshClient? _client;

    public ObservableCollection<ForwardViewModel> Items { get; } = new();

    public void Load(IEnumerable<SshForward> forwards)
    {
        StopAll();
        Items.Clear();
        foreach (var f in forwards) Items.Add(new ForwardViewModel(this, f));
    }

    /// <summary>Binds to a (new) connection and starts the forwards marked to start automatically.</summary>
    public void Attach(SshClient client)
    {
        _client = client;
        foreach (var item in Items)
        {
            item.Port = null;
            item.State = ForwardState.Stopped;
            if (item.Forward.AutoStart) Start(item);
        }
    }

    public void Detach()
    {
        StopAll();
        _client = null;
    }

    public void Start(ForwardViewModel item)
    {
        if (_client is not { IsConnected: true } client || item.IsListening) return;
        var f = item.Forward;
        var bind = string.IsNullOrWhiteSpace(f.BindAddress) ? "127.0.0.1" : f.BindAddress == "*" ? "0.0.0.0" : f.BindAddress!;
        try
        {
            ForwardedPort port = f.Kind switch
            {
                SshForwardKind.Dynamic => new ForwardedPortDynamic(bind, (uint)f.BindPort),
                SshForwardKind.Remote => new ForwardedPortRemote(string.IsNullOrWhiteSpace(f.BindAddress) ? "localhost" : bind, (uint)f.BindPort, f.TargetHost!, (uint)f.TargetPort),
                _ => new ForwardedPortLocal(bind, (uint)f.BindPort, f.TargetHost!, (uint)f.TargetPort),
            };
            port.Exception += (_, e) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Per-connection errors (target refused one connection) don't stop the forward; only report them.
                item.Problem = e.Exception.Message;
            });
            client.AddForwardedPort(port);
            port.Start();
            item.Port = port;
            item.Problem = null;
            item.State = ForwardState.Listening;
        }
        catch (Exception ex) when (ex is SocketException or SshException or InvalidOperationException or ArgumentException)
        {
            item.Problem = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ? Loc.T("Term.Forward.InUse") : ex.Message;
            item.State = ForwardState.Failed;
            if (item.Port is null) TryRemove(client, null);
        }
    }

    public void Stop(ForwardViewModel item)
    {
        if (item.Port is { } port)
        {
            try { port.Stop(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException) { }
            TryRemove(_client, port);
        }
        item.Port = null;
        item.State = ForwardState.Stopped;
    }

    private static void TryRemove(SshClient? client, ForwardedPort? port)
    {
        if (client is null || port is null) return;
        try { client.RemoveForwardedPort(port); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
        try { port.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
    }

    private void StopAll()
    {
        foreach (var item in Items) Stop(item);
    }

    public void Dispose() => Detach();
}
