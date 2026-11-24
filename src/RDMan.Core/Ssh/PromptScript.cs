using System.Text;

namespace RDMan.Core.Ssh;

/// <summary>
/// RDMan's session-only prompt. Nothing is written on the server: the remote command starts bash with an rcfile
/// delivered inline (base64 through process substitution). The rc first does what a login shell does
/// (/etc/profile, then ~/.bash_profile | ~/.bash_login | ~/.profile), then adds:
/// <list type="bullet">
/// <item>a powerline prompt (user, host, path, git branch, last exit code, duration, Python venv) drawn with the
/// 16 ANSI colors so it follows the terminal's color scheme;</item>
/// <item>shell integration: OSC 133 prompt/command marks and OSC 7 current directory.</item>
/// </list>
/// When the account's login shell isn't bash (zsh, fish…), the user's own shell starts untouched.
/// </summary>
public static class PromptScript
{
    /// <summary>Remote command for an exec channel with a PTY.</summary>
    public static string RemoteCommand()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(RcFile()));
        // Outer layer runs in /bin/sh (works whatever the login shell is, fish included). Base64 is quote-safe.
        return "exec sh -c 'case \"${SHELL##*/}\" in bash) ;; *) exec \"${SHELL:-/bin/sh}\" -l;; esac; " +
               "command -v base64 >/dev/null 2>&1 || exec \"$SHELL\" -l; " +
               $"exec bash -c \"exec bash --rcfile <(printf %s {b64} | base64 -d) -i\"'";
    }

    // Powerline glyphs as UTF-8 escapes so the script works even when the server's locale isn't UTF-8.
    private const string Arrow = @"$'\xee\x82\xb0'";      // U+E0B0
    private const string Branch = @"$'\xee\x82\xa0'";     // U+E0A0
    private const string Cross = @"$'\xe2\x9c\x98'";      // U+2718 ✘
    private const string Clock = @"$'\xef\x80\x97'";      // U+F017 (Nerd Font clock)
    private const string Thin = @"$'\xee\x82\xb1'";       // U+E0B1

    /// <summary>The bash rcfile (LF line endings).</summary>
    public static string RcFile()
    {
        var rc = $$"""
# RDMan session prompt: lives only in this session, nothing is saved on the server.
[ -r /etc/profile ] && . /etc/profile
if [ -r ~/.bash_profile ]; then . ~/.bash_profile
elif [ -r ~/.bash_login ]; then . ~/.bash_login
elif [ -r ~/.profile ]; then . ~/.profile
fi

__rdman_arrow={{Arrow}}
__rdman_thin={{Thin}}
__rdman_branch={{Branch}}
__rdman_cross={{Cross}}
__rdman_clock={{Clock}}
__rdman_more=$'\xe2\x80\xa6'
__rdman_user=${USER:-$(id -un 2>/dev/null)}
__rdman_start=
__rdman_none= # must be set: bash skips the PS0 offset arithmetic of an unset variable
VIRTUAL_ENV_DISABLE_PROMPT=1

# Colors are ANSI indices 0-15 so the prompt follows the terminal's scheme. \001 \002 tell readline which bytes
# take no space; they work inside an expanded variable, which keeps folder and branch names out of prompt
# expansion (a folder called $(...) is shown, never run).
__rdman_sgr() { printf -v __rdman_tmp '\001\033[%sm\002' "$1"; __rdman_ps+=$__rdman_tmp; }

__rdman_seg() { # bg fg text
  local bg=$((40 + $1 % 8 + ($1 > 7 ? 60 : 0))) fg=$((30 + $2 % 8 + ($2 > 7 ? 60 : 0)))
  if [ -n "$__rdman_bg" ]; then
    __rdman_sgr "$((30 + __rdman_bg % 8 + (__rdman_bg > 7 ? 60 : 0)));$bg"
    __rdman_ps+=$__rdman_arrow
  fi
  __rdman_sgr "$fg;$bg"
  __rdman_ps+=" $3 "
  __rdman_bg=$1
}

__rdman_prompt() {
  local ec=$? elapsed=
  if [ -n "$__rdman_start" ]; then elapsed=$((SECONDS - __rdman_start)); fi
  __rdman_start=
  printf '\033]133;D;%s\007\033]7;file://%s%s\007\033]133;A\007' "$ec" "${HOSTNAME:-localhost}" "${PWD// /%20}"

  __rdman_ps=
  __rdman_bg=
  if [ "$EUID" = 0 ]; then __rdman_seg 1 15 "$__rdman_user"; else __rdman_seg 8 15 "$__rdman_user"; fi
  __rdman_seg 0 7 "${HOSTNAME%%.*}"
  [ -n "$VIRTUAL_ENV" ] && __rdman_seg 6 0 "${VIRTUAL_ENV##*/}"

  local p=$PWD rest=
  if [ "$p" = "$HOME" ]; then __rdman_seg 4 0 "~"
  elif [ -n "$HOME" ] && [ "${p#"$HOME"/}" != "$p" ]; then __rdman_seg 4 0 "~"; rest=${p#"$HOME"/}
  else __rdman_seg 4 0 "/"; rest=${p#/}
  fi
  if [ -n "$rest" ]; then
    local parts=() part joined=
    IFS=/ read -r -a parts <<< "$rest"
    if [ "${#parts[@]}" -gt 3 ]; then parts=("$__rdman_more" "${parts[@]: -2}"); fi
    for part in "${parts[@]}"; do
      [ -n "$joined" ] && joined+=" $__rdman_thin "
      joined+=$part
    done
    __rdman_seg 0 15 "$joined"
  fi

  local br
  br=$(git symbolic-ref --short -q HEAD 2>/dev/null || git rev-parse --short HEAD 2>/dev/null)
  if [ -n "$br" ]; then
    if git diff --quiet --ignore-submodules HEAD -- 2>/dev/null; then __rdman_seg 2 0 "$__rdman_branch $br"
    else __rdman_seg 3 0 "$__rdman_branch $br *"; fi
  fi

  [ "$ec" != 0 ] && __rdman_seg 1 15 "$__rdman_cross $ec"
  [ -n "$elapsed" ] && [ "$elapsed" -ge 2 ] && __rdman_seg 5 0 "$__rdman_clock ${elapsed}s"
  if [ "$EUID" = 0 ]; then __rdman_seg 8 15 "#"; else __rdman_seg 8 15 "\$"; fi

  __rdman_sgr "0;$((30 + __rdman_bg % 8 + (__rdman_bg > 7 ? 60 : 0)))"
  __rdman_ps+=$__rdman_arrow
  __rdman_sgr 0
  __rdman_ps+=$' \001\033]133;B\007\002'
  return $ec
}

if [ "${BASH_VERSINFO[0]}" -gt 4 ] || { [ "${BASH_VERSINFO[0]}" -eq 4 ] && [ "${BASH_VERSINFO[1]}" -ge 4 ]; }; then
  # PS0 is expanded before each command runs (in this shell, no subshell): start the timer, mark the command.
  PS0='${__rdman_none:$((__rdman_start=SECONDS, 0)):0}'$'\033]133;C\007'
fi
PROMPT_COMMAND="__rdman_prompt${PROMPT_COMMAND:+; $PROMPT_COMMAND}"
PS1='${__rdman_ps}'
""";
        return rc.Replace("\r\n", "\n");
    }
}
