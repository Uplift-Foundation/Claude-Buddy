#!/usr/bin/env bash
# Wires Claude Buddy into every agent CLI on this machine.
#
# The one thing an install should run. Orbs appear because a CLI calls the hook,
# so an app with no hooks wired doesn't error — it sits there showing nothing —
# and asking someone to know which of two installers to run for which CLI is a
# way of arranging for that to happen.
#
#   install-hooks.sh              # install / repair everything found
#   install-hooks.sh --uninstall  # remove just our entries, everywhere
#
# Both sub-installers converge rather than duplicating, so re-running is how you
# repair a broken setup, and how you pick up a CLI you installed later.
#
# A CLI that isn't here is skipped and said so, not treated as a failure. Most
# people have one of the two, and "Codex: not installed" is information; an
# error would be a lie.

set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"

# Only the flag both sub-installers understand is accepted, and it is the only
# one forwarded. Passing everything through looked tidier and was wrong: the
# Codex installer takes --codex-home, the Claude Code one takes --settings, and
# forwarding either to the other makes it exit 2 in the middle of a run that has
# already changed something. An entry point whose failure mode is a half-done
# install is worse than one that doesn't take the option.
#
# Anything more specific than --uninstall is a job for the sub-installer
# directly, which is what they are still there for.
FORWARD=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) FORWARD+=(--uninstall); shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *)
      echo "unknown option: $1" >&2
      echo "This wires every agent CLI it finds. It takes --uninstall and nothing else;" >&2
      echo "for per-CLI options run tools/install-macos-hooks.sh or" >&2
      echo "tools/install-codex-hooks.sh directly." >&2
      exit 2
      ;;
  esac
done

# Alongside (installed app bundle) wins over the repo's tools/ directory, the
# same resolution both sub-installers use for the hook script itself.
find_installer() {
    for candidate in "$HERE/$1" "$HERE/tools/$1"; do
        [[ -x "$candidate" ]] && { printf '%s' "$candidate"; return 0; }
    done
    return 1
}

have_claude_code() {
    [[ -d "$HOME/.claude" ]] || command -v claude >/dev/null 2>&1
}

have_codex() {
    [[ -d "${CODEX_HOME:-$HOME/.codex}" ]] || command -v codex >/dev/null 2>&1
}

have_grok() {
    [[ -d "${GROK_HOME:-$HOME/.grok}" ]] || command -v grok >/dev/null 2>&1
}

wired=0
skipped=()
failed=()

run_one() {
    local label="$1" script="$2"
    local path
    if ! path="$(find_installer "$script")"; then
        failed+=("$label (couldn't find $script)")
        return
    fi

    echo "=== $label"
    if "$path" "${FORWARD[@]+"${FORWARD[@]}"}"; then
        wired=$((wired + 1))
    else
        failed+=("$label")
    fi
    echo
}

if have_claude_code; then
    run_one "Claude Code" install-macos-hooks.sh
else
    skipped+=("Claude Code")
fi

if have_codex; then
    run_one "Codex" install-codex-hooks.sh
else
    skipped+=("Codex")
fi

if have_grok; then
    run_one "Grok Build" install-grok-hooks.sh
else
    skipped+=("Grok Build")
fi

for one in "${skipped[@]+"${skipped[@]}"}"; do
    echo "=== $one: not installed on this machine, nothing to wire."
    echo "    Install it and run this again — that is all it takes."
    echo
done

if [[ ${#failed[@]} -gt 0 ]]; then
    echo "Finished with problems:"
    for one in "${failed[@]}"; do echo "  - $one"; done
    exit 1
fi

if [[ $wired -eq 0 ]]; then
    echo "Neither Claude Code, Codex, nor Grok Build was found, so nothing was wired."
    echo "Claude Buddy will show no orbs until one of them is installed."
    exit 0
fi

echo "Done."
