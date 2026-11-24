using RDMan.ViewModels.Terminal;

namespace RDMan.ViewModels;

/// <summary>
/// Broadcast input (like MobaXterm's MultiExec): what you type in one terminal of the set goes to all of them.
/// Every participant shows a warning banner with the count and a way out, so it's never on by accident.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<SshSessionViewModel> _broadcast = new();

    public IReadOnlyList<SshSessionViewModel> BroadcastSessions => _broadcast;

    public void SetBroadcast(SshSessionViewModel session, bool on)
    {
        if (on && !_broadcast.Contains(session))
        {
            _broadcast.Add(session);
            session.InputTyped += OnBroadcastInput;
        }
        else if (!on && _broadcast.Remove(session))
        {
            session.InputTyped -= OnBroadcastInput;
        }
        RefreshBroadcast();
    }

    /// <summary>Adds (or removes) every connected terminal at once.</summary>
    public void BroadcastToAll(bool on)
    {
        foreach (var t in TerminalSessions.Where(t => on ? t.IsConnected : true).ToList()) SetBroadcast(t, on);
    }

    private void RefreshBroadcast()
    {
        _broadcast.RemoveAll(s => !TerminalSessions.Contains(s));
        foreach (var s in TerminalSessions) s.SetBroadcastState(_broadcast.Contains(s), _broadcast.Count);
    }

    private void OnBroadcastInput(SshSessionViewModel from, string text)
    {
        if (_broadcast.Count < 2 || !_broadcast.Contains(from)) return;
        foreach (var other in _broadcast)
            if (!ReferenceEquals(other, from)) other.SendInput(text);
    }

    /// <summary>A terminal closed or its tab went away: drop it from the set.</summary>
    public void LeaveBroadcast(SshSessionViewModel session)
    {
        if (_broadcast.Contains(session)) SetBroadcast(session, false);
    }
}
