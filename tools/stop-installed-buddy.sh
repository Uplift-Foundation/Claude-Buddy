#!/usr/bin/env bash
# Stops every Claude Buddy this user is running from one executable path, so
# an install can replace it (CB-206).
#
#   stop-installed-buddy.sh <executable-path>
#
# Prints each pid it stopped, one per line. Exits 0 once none is left, 1 if
# one survived SIGKILL.
#
# Why an install has to do this at all: until CB-206 the single-instance mutex
# was scoped to one POSIX session, so the copy launchd started after
# `build-macos-app.sh --install` ran alongside the one already running, and
# every orb was drawn twice. With the mutex now one per user, that launchd
# start finds the old copy holding it and exits 0 — and the keep-alive's
# SuccessfulExit=false means launchd takes that exit at its word and never
# retries. Left alone, an install would swap two Buddies for one stale one,
# still running the binary the install just deleted. So the old one goes
# first.
#
# Matched on the exact executable path and this user's uid, read from `ps`'s
# comm column (the full path on macOS) rather than with `pgrep -f`, which would
# also match any shell whose command line merely mentions the path — the
# install script's own wrapper included.
#
# SIGTERM first and SIGKILL only after the grace period: the app has nothing it
# must flush on the way out, but a clean exit is still the better one to give
# it, and the grace is short because an install is waiting.
#
# Test seam: CLAUDE_BUDDY_STOP_GRACE_SECONDS shortens the wait, so the suite
# can drive the SIGKILL arm with a process that ignores SIGTERM.
set -uo pipefail

if [[ $# -ne 1 || -z "$1" ]]; then
  echo "usage: $0 <executable-path>" >&2
  exit 2
fi

EXE="$1"
GRACE="${CLAUDE_BUDDY_STOP_GRACE_SECONDS:-10}"
UID_NOW="$(id -u)"

pids_running_exe() {
  ps -axo pid=,uid=,comm= | awk -v u="$UID_NOW" -v exe="$EXE" '
    match($0, /^ *[0-9]+ +[0-9]+ /) {
      split(substr($0, 1, RLENGTH), head, " ")
      if (head[2] == u && substr($0, RLENGTH + 1) == exe) print head[1]
    }'
}

any_alive() {
  local pid
  for pid in "$@"; do
    kill -0 "$pid" 2>/dev/null && return 0
  done
  return 1
}

PIDS=()
while IFS= read -r pid; do
  [[ -n "$pid" ]] && PIDS+=("$pid")
done < <(pids_running_exe)

[[ ${#PIDS[@]} -eq 0 ]] && exit 0

kill -TERM "${PIDS[@]}" 2>/dev/null

# Counted in 0.2 s steps rather than against $SECONDS, which ticks in whole
# seconds: a one-second grace measured that way can end almost at once.
steps=$((GRACE * 5))
while any_alive "${PIDS[@]}" && [[ $steps -gt 0 ]]; do
  sleep 0.2
  steps=$((steps - 1))
done

if any_alive "${PIDS[@]}"; then
  kill -KILL "${PIDS[@]}" 2>/dev/null
  sleep 0.5
fi

printf '%s\n' "${PIDS[@]}"
any_alive "${PIDS[@]}" && exit 1
exit 0
