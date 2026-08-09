#!/bin/bash
# Claude Buddy hook for macOS/Linux — bash twin of ClaudeBuddyHook.ps1.
# Usage (from a Claude Code hook): ClaudeBuddyHook.sh <idle|generating|waiting|ended>
# Reads the hook payload JSON on stdin for session_id and cwd.

STATE="$1"
case "$STATE" in
    idle|generating|waiting|ended) ;;
    *) exit 0 ;;
esac

PAYLOAD=$(cat)

# No jq dependency: session_id is a UUID and cwd is a path, neither of which
# contains embedded quotes in practice, so simple sed extraction is enough.
SESSION_ID=$(printf '%s' "$PAYLOAD" | sed -n 's/.*"session_id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
CWD=$(printf '%s' "$PAYLOAD" | sed -n 's/.*"cwd"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
TRANSCRIPT=$(printf '%s' "$PAYLOAD" | sed -n 's/.*"transcript_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ -n "$SESSION_ID" ] || SESSION_ID="unknown"

# What the chat is called and what color it's been given. Claude Code keeps
# all three of these in the transcript, re-emitting them as the session goes:
#   {"type":"custom-title","customTitle":"claude-buddy",...}   <- /rename
#   {"type":"ai-title","aiTitle":"Package app with a tray",...} <- auto-named
#   {"type":"agent-color","agentColor":"green",...}             <- /color
# A name you set with /rename wins over the generated one regardless of which
# was written last, and a session too young to have either falls back to the
# directory name in the app.
#
# Anchoring the match at the start of the line is what makes this safe:
# transcripts are full of quoted text that would otherwise match, but content
# inside a message is JSON-escaped, so only a real record can start this way.
TITLE=""
COLOR=""
if [ "$STATE" != "ended" ] && [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
    # Transcripts reach tens of MB and this runs on every tool call, so pull
    # all three record types out of the tail in one read — each is normally
    # within ~25KB of the end — and only scan the whole file when a long run
    # of tool output has pushed them all out of that window.
    META=$(tail -c 262144 "$TRANSCRIPT" 2>/dev/null \
        | grep -E '^\{"type":"(custom-title|ai-title|agent-color)"')
    [ -n "$META" ] || META=$(grep -E '^\{"type":"(custom-title|ai-title|agent-color)"' \
        "$TRANSCRIPT" 2>/dev/null)

    # Newest record of one type.
    cb_pick() { printf '%s\n' "$META" | grep "^{\"type\":\"$1\"" | tail -1; }
    # Its string value. Both title keys end in `Title"`, and a record only
    # ever has one, so one greedy pattern covers both. Backslashes are
    # stripped because this script hand-rolls its JSON and a stray escape
    # would break the app's parse and drop the orb; quotes can't get this far,
    # the match stops at the first one.
    cb_value() {
        printf '%s' "$1" \
            | sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" \
            | tr -d '\\'
    }

    TITLE=$(cb_value "$(cb_pick custom-title)" customTitle)
    [ -n "$TITLE" ] || TITLE=$(cb_value "$(cb_pick ai-title)" aiTitle)

    # Color names only — letters, nothing that could need escaping.
    COLOR=$(cb_value "$(cb_pick agent-color)" agentColor | tr -cd 'a-zA-Z')
fi

# Identify the terminal hosting this session so a click on the orb can jump
# to it. This script runs inside the terminal's process tree, so:
# - inside tmux, the pane id is the only trustworthy coordinate (see below);
# - otherwise ITERM_SESSION_ID ("w0t0p0:UUID") pins the exact iTerm2 pane;
# - the controlling tty of the nearest ancestor that has one (the claude
#   TUI process — this hook itself runs on a pipe, not a tty) pins the
#   exact Terminal.app tab.
#
# tmux: $TMUX is "<socket>,<server pid>,<session index>" and $TMUX_PANE is a
# server-unique pane id like "%3". Both are inherited by this hook from the
# claude process running in the pane. Deliberately *not* recording
# ITERM_SESSION_ID in this case: inside tmux it's whatever was in the
# environment when the pane was created, which is stale as often as not, and
# jumping to the wrong pane is worse than not jumping at all. The pane's own
# tty is likewise a tmux pty, not a terminal tab, so the app resolves the
# real terminal from the attached tmux client at click time instead.
TMUX_SOCKET=""
TMUX_PANE_ID=""
TMUX_BIN=""
TERM_ID=""
if [ -n "$TMUX" ]; then
    TMUX_SOCKET="${TMUX%%,*}"
    TMUX_PANE_ID="$TMUX_PANE"
    TMUX_BIN=$(command -v tmux 2>/dev/null)
elif [ -n "$ITERM_SESSION_ID" ]; then
    TERM_ID="${ITERM_SESSION_ID#*:}"
fi

# The ancestor that owns the tty is the claude TUI process itself, so the same
# walk that finds the tty also hands us the pid to record. The app uses it to
# tell "this session is still running" from "this file was left behind": a
# session killed with Ctrl+C never fires SessionEnd, so its file survives, and
# without a pid the only way to notice is to wait out the orb lifetime — which
# is forever if that is what you picked. See SessionManager.SessionGone.
TTY=""
SESSION_PID=""
PID=$$
for _ in 1 2 3 4 5; do
    PID=$(ps -o ppid= -p "$PID" 2>/dev/null | tr -d ' ')
    { [ -z "$PID" ] || [ "$PID" = "0" ] || [ "$PID" = "1" ]; } && break
    T=$(ps -o tty= -p "$PID" 2>/dev/null | tr -d ' ')
    if [ -n "$T" ] && [ "$T" != "??" ]; then TTY="$T"; SESSION_PID="$PID"; break; fi
done

# ${TMPDIR} is what .NET's Path.GetTempPath() returns on macOS, so the app
# and this script agree on the folder (both are per-user).
DIR="${TMPDIR:-/tmp/}"
DIR="${DIR%/}/claude_buddy"
FILE="$DIR/$SESSION_ID.txt"

if [ "$STATE" = "ended" ]; then
    rm -f "$FILE"
else
    mkdir -p "$DIR"
    printf '{"state":"%s","cwd":"%s","title":"%s","color":"%s","term_program":"%s","term_id":"%s","tty":"%s","tmux_socket":"%s","tmux_pane":"%s","tmux_bin":"%s","session_pid":%s,"transcript_path":"%s"}' \
        "$STATE" "$CWD" "$TITLE" "$COLOR" "$TERM_PROGRAM" "$TERM_ID" "$TTY" \
        "$TMUX_SOCKET" "$TMUX_PANE_ID" "$TMUX_BIN" "${SESSION_PID:-0}" "$TRANSCRIPT" > "$FILE"
fi

exit 0
