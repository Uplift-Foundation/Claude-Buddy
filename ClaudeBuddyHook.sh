#!/bin/bash
# Claude Buddy hook for macOS/Linux — bash twin of ClaudeBuddyHook.ps1.
# Usage (from a Claude Code or Codex hook):
#   ClaudeBuddyHook.sh <idle|generating|waiting|ended>
#   ClaudeBuddyHook.sh codex <idle|generating|waiting|ended>
# Reads the hook payload JSON on stdin for session_id and cwd.
#
# One script for both CLIs rather than two, because almost none of it is about
# either: finding the terminal, finding the session's process, and writing the
# status file are the same job whoever asked. The agent word only gates the
# handful of places where the two genuinely differ, and each of those says why.
#
# It must exit 0 and print nothing, whatever happens. That has always been good
# manners; under Codex it is load-bearing. Codex reads a hook's stdout as JSON
# with `additionalProperties: false`, so anything printed is
# "hook returned invalid permission-request JSON output" — and **exit status 2
# is a deny**. A hook that fails noisily would start refusing the user's own
# approvals. See docs/codex-findings.md.

AGENT="claude"
case "$1" in
    claude|codex) AGENT="$1"; shift ;;
esac

STATE="$1"
case "$STATE" in
    idle|generating|waiting|ended) ;;
    *) exit 0 ;;
esac

PAYLOAD=$(cat)

# Pull one top-level string out of the payload.
#
# The obvious `sed 's/.*"key":"\([^"]*\)".*/\1/'` is greedy and takes the
# *last* match on the line, which was fine while the only payloads came from
# Claude Code. Codex's PreToolUse and PermissionRequest payloads embed
# `tool_input` as arbitrary JSON, and its own command records carry
# `"cwd":"file:///…"` — so a nested key would win and CWD would become
# something that is not a directory. Everything downstream keys off cwd: the
# orb's name, its saved position, which sessions are siblings.
#
# grep -o takes the first, which is the top-level one, because the payload
# writes its own fields before nesting anything.
field() {
    printf '%s' "$PAYLOAD" \
        | grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
        | head -1 \
        | sed 's/.*:[[:space:]]*"\(.*\)"$/\1/'
}

SESSION_ID=$(field session_id)
CWD=$(field cwd)
TRANSCRIPT=$(field transcript_path)
[ -n "$SESSION_ID" ] || SESSION_ID="unknown"

# ${TMPDIR} is what .NET's Path.GetTempPath() returns on macOS, so the app
# and this script agree on the folder (both are per-user).
DIR="${TMPDIR:-/tmp/}"
DIR="${DIR%/}/claude_buddy"
FILE="$DIR/$SESSION_ID.txt"

if [ "$STATE" = "ended" ]; then
    rm -f "$FILE"
    exit 0
fi

# Codex's transcript_path is nullable, and a null is not a quoted string so it
# arrives here empty. The rollout is findable anyway — its filename ends in the
# session id — and only this script can see CODEX_HOME, which is where the app
# would otherwise have to guess. Resolving it here keeps the same division of
# labour the rest of the file has: the hook knows the environment, the app
# reads files.
if [ "$AGENT" = "codex" ] && [ -z "$TRANSCRIPT" ]; then
    for candidate in "${CODEX_HOME:-$HOME/.codex}"/sessions/*/*/*/rollout-*-"$SESSION_ID".jsonl; do
        [ -f "$candidate" ] && TRANSCRIPT="$candidate"
    done
fi

TITLE=""
COLOR=""

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
#
# Claude Code only. A Codex rollout contains none of these three records, so
# the tail below would never match and the whole-file fallback would re-read a
# multi-megabyte transcript on every single tool call.
if [ "$AGENT" = "claude" ] && [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
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

# Codex names nothing. It has a /rename, but the name it sets is not written to
# the rollout — it lives in a sqlite database this has no business reading —
# and there is no auto-title and no /color at all. So the orb would fall back
# to the directory name, which is right for one session in a folder and useless
# for three.
#
# The first thing you asked for is the closest thing to a name the file
# actually contains, and it has the property that matters most here: it never
# changes, so the orb's saved position doesn't move when the conversation does.
# It is a title this app invented rather than one Codex reported, which is why
# it is written down in AGENTS.md and the README rather than left to be
# discovered.
#
# Computed once. Every later write re-reads the title out of the status file,
# so the grep below happens on a session's first hook and never again — which
# matters because this one runs on every tool call.
if [ "$AGENT" = "codex" ]; then
    if [ -f "$FILE" ]; then
        TITLE=$(sed -n 's/.*"title"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$FILE")
    fi

    if [ -z "$TITLE" ] && [ -n "$TRANSCRIPT" ] && [ -f "$TRANSCRIPT" ]; then
        # -m1 stops at the first match, and a UserMessage is within the first
        # handful of rows, so this reads the head of the file rather than the
        # file. The row is one line of compact JSON; the prompt is the first
        # "text" value inside the first UserMessage item.
        #
        # The two sed passes before the trim are not tidying. A prompt is
        # several lines as often as not, and its newlines arrive as the two
        # characters \ and n — so stripping backslashes first would weld the
        # lines together with an "n" in the seam ("first linensecond line").
        # Escaped quotes are stripped after that, for the reason the Claude
        # branch above gives: this script hand-rolls its JSON and a stray
        # backslash would break the app's parse and drop the orb.
        TITLE=$(grep -m1 '"type":"UserMessage"' "$TRANSCRIPT" 2>/dev/null \
            | sed -n 's/.*"type":"UserMessage".*"text":"\([^"]*\)".*/\1/p' \
            | sed 's/\\[nrt]/ /g' \
            | tr -d '\\' \
            | tr -s '[:space:]' ' ' \
            | cut -c1-60 \
            | sed 's/[[:space:]]*$//')

        # Cut at 60 characters lands mid-word about as often as not, and this
        # is a name rather than a summary — "remove all usa" reads as a bug in
        # the app. Back off to the last space, but only when that still leaves
        # something worth reading; a first word longer than 30 characters is a
        # path or a URL, and half of one is better than none of it.
        SHORTER="${TITLE% *}"
        if [ "$SHORTER" != "$TITLE" ] && [ "${#SHORTER}" -ge 30 ]; then
            TITLE="$SHORTER"
        fi
    fi
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
#
# For Codex the walk looks for the codex process by name first, and only then
# for a tty. The two usually land on the same process and the distinction only
# shows up in one case — but it is a case that costs someone an orb they were
# using. Claude Code running `codex exec` as a Bash tool puts a codex process
# in a pipe with no tty of its own, so a tty-only walk would run straight past
# it and record *Claude's* pid. The app groups status files by pid to work out
# which one supersedes which, so the nested codex file would be the newest in
# Claude's bucket and would delete the live Claude orb.
TTY=""
SESSION_PID=""
PID=$$
for _ in 1 2 3 4 5; do
    PID=$(ps -o ppid= -p "$PID" 2>/dev/null | tr -d ' ')
    { [ -z "$PID" ] || [ "$PID" = "0" ] || [ "$PID" = "1" ]; } && break

    T=$(ps -o tty= -p "$PID" 2>/dev/null | tr -d ' ')

    if [ "$AGENT" = "codex" ]; then
        COMM=$(basename "$(ps -o comm= -p "$PID" 2>/dev/null)" 2>/dev/null)
        if [ "$COMM" = "codex" ]; then
            SESSION_PID="$PID"
            [ -n "$T" ] && [ "$T" != "??" ] && TTY="$T"
            break
        fi
    fi

    if [ -n "$T" ] && [ "$T" != "??" ]; then TTY="$T"; SESSION_PID="$PID"; break; fi
done

mkdir -p "$DIR"

# "cli" is what tells the app which of the two wrote this. Written by name
# rather than left to be inferred from which fields happen to be empty — the
# app has an enum for it and the whole point is that a typo shouldn't be
# indistinguishable from a Claude Code session. A file from an older hook has
# no such key, which reads as Claude Code, which is what it was.
printf '{"state":"%s","cli":"%s","cwd":"%s","title":"%s","color":"%s","term_program":"%s","term_id":"%s","tty":"%s","tmux_socket":"%s","tmux_pane":"%s","tmux_bin":"%s","session_pid":%s,"transcript_path":"%s"}' \
    "$STATE" "$AGENT" "$CWD" "$TITLE" "$COLOR" "$TERM_PROGRAM" "$TERM_ID" "$TTY" \
    "$TMUX_SOCKET" "$TMUX_PANE_ID" "$TMUX_BIN" "${SESSION_PID:-0}" "$TRANSCRIPT" > "$FILE"

exit 0
