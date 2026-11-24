using System.Text;

namespace RDMan.Core.Ssh;

/// <summary>
/// Turns a terminal byte stream (already decoded to text) into readable log lines: removes CSI/OSC/DCS and other
/// escape sequences, applies carriage returns (progress bars keep only their last state) and backspaces.
/// Streaming: sequences split across <see cref="Feed"/> calls are handled.
/// </summary>
public sealed class AnsiStripper
{
    private enum State { Text, Escape, Csi, Osc, OscEscape, String, StringEscape, Charset }

    private readonly StringBuilder _line = new();
    private readonly Func<string>? _prefix;
    private State _state = State.Text;
    private bool _pendingCr;

    /// <param name="linePrefix">Optional text put before every line (e.g. a timestamp).</param>
    public AnsiStripper(Func<string>? linePrefix = null) => _prefix = linePrefix;

    /// <summary>Consumes text and returns the lines completed by it (each ending in '\n').</summary>
    public string Feed(ReadOnlySpan<char> text)
    {
        var output = new StringBuilder();
        foreach (var c in text)
        {
            switch (_state)
            {
                case State.Text:
                    Text(c, output);
                    break;
                case State.Escape:
                    _state = c switch
                    {
                        '[' => State.Csi,
                        ']' => State.Osc,
                        'P' or 'X' or '^' or '_' => State.String,
                        '(' or ')' or '*' or '+' or '-' or '.' or '/' or '#' or '%' or ' ' => State.Charset,
                        _ => State.Text, // two-byte sequence (ESC 7, ESC M, ESC =…)
                    };
                    break;
                case State.Csi:
                    if (c is >= '@' and <= '~') _state = State.Text;
                    break;
                case State.Osc:
                    if (c == '\a') _state = State.Text;
                    else if (c == '\x1b') _state = State.OscEscape;
                    break;
                case State.OscEscape:
                    _state = c == '\\' ? State.Text : State.Osc;
                    break;
                case State.String:
                    if (c == '\x1b') _state = State.StringEscape;
                    break;
                case State.StringEscape:
                    _state = c == '\\' ? State.Text : State.String;
                    break;
                case State.Charset:
                    _state = State.Text;
                    break;
            }
        }
        return output.ToString();
    }

    private void Text(char c, StringBuilder output)
    {
        if (_pendingCr)
        {
            _pendingCr = false;
            if (c != '\n') _line.Clear(); // a lone CR rewinds the line: keep only what's written after it
        }

        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;
            case '\r':
                _pendingCr = true;
                return;
            case '\n':
                if (_prefix is not null) output.Append(_prefix());
                output.Append(_line).Append('\n');
                _line.Clear();
                return;
            case '\b':
                if (_line.Length > 0) _line.Length--;
                return;
            case '\t':
                _line.Append(c);
                return;
        }
        if (c < ' ' || c == '\x7f' || c is >= '\x80' and <= '\x9f') return; // other controls (bell, C1)
        _line.Append(c);
    }

    /// <summary>The unfinished last line, if any (call when the session ends).</summary>
    public string Flush()
    {
        if (_line.Length == 0) return "";
        var s = (_prefix?.Invoke() ?? "") + _line + "\n";
        _line.Clear();
        return s;
    }
}
