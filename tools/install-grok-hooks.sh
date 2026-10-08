#!/usr/bin/env bash
# Installs the Claude Buddy hook into Grok Build's macOS hook config.
#
# Grok discovers global hooks from $GROK_HOME/hooks/*.json and they are
# always trusted — unlike Codex, there is no extra /hooks-trust step.
#
#   install-grok-hooks.sh              # install / repair
#   install-grok-hooks.sh --uninstall  # remove just our file
#
# Extra Grok accounts run as GROK_HOME=~/.grok-work grok are a separate
# hooks directory. Every directory name saved in Settings ("Grok profiles")
# is wired too, in addition to the default ~/.grok.
#
# Safe to re-run: it overwrites our file rather than accumulating copies.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
GROK_DIR="${GROK_HOME:-$HOME/.grok}"
HOOK_DIR=""
HOOKS_FILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --grok-home) GROK_DIR="$2"; shift 2 ;;
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    --no-profiles) NO_PROFILES=1; shift ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    --hooks-file) HOOKS_FILE="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$HOOK_DIR" ]] || HOOK_DIR="$GROK_DIR/claude-buddy"
[[ -n "$HOOKS_FILE" ]] || HOOKS_FILE="$GROK_DIR/hooks/claude-buddy.json"

HERE="$(cd "$(dirname "$0")" && pwd)"

if [[ -f "$HERE/ClaudeBuddyHook.sh" ]]; then
  SOURCE="$HERE/ClaudeBuddyHook.sh"
elif [[ -f "$HERE/../ClaudeBuddyHook.sh" ]]; then
  SOURCE="$HERE/../ClaudeBuddyHook.sh"
else
  SOURCE=""
fi

INSTALLED="$HOOK_DIR/ClaudeBuddyHook.sh"

if [[ "$HOOK_DIR" == "$HOME/.grok/claude-buddy" ]]; then
  CONFIGURED='$HOME/.grok/claude-buddy/ClaudeBuddyHook.sh'
else
  CONFIGURED="$INSTALLED"
fi

if [[ $UNINSTALL -eq 0 ]]; then
  if [[ -z "$SOURCE" ]]; then
    echo "Can't find ClaudeBuddyHook.sh next to $HERE or one level up." >&2
    exit 1
  fi
  mkdir -p "$HOOK_DIR"
  cp "$SOURCE" "$INSTALLED"
  chmod +x "$INSTALLED"
  echo "Hook installed: $INSTALLED"
fi

if [[ $UNINSTALL -eq 1 ]]; then
  rm -f "$HOOKS_FILE"
  echo "Removed Claude Buddy hooks from $HOOKS_FILE."
  echo "The installed hook script was left in place; delete $HOOK_DIR if you want it gone."
else
  mkdir -p "$(dirname "$HOOKS_FILE")"
  # timeout is explicit because Grok's default for observe hooks is 5 seconds.
  cat > "$HOOKS_FILE" <<EOF
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok idle", "timeout": 15 } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok generating", "timeout": 15 } ] }
    ],
    "PreToolUse": [
      { "matcher": ".*", "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok generating", "timeout": 15 } ] }
    ],
    "Notification": [
      { "matcher": "permission_prompt", "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok waiting", "timeout": 15 } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok idle", "timeout": 15 } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "command", "command": "bash \"$CONFIGURED\" grok ended", "timeout": 15 } ] }
    ]
  }
}
EOF
  echo "Wired Claude Buddy hooks into $HOOKS_FILE"
  echo
  echo "Restart any running Grok sessions: hooks are read at session start."
fi

# --- extra Grok accounts ------------------------------------------------------

if [[ $NO_PROFILES -eq 1 ]]; then
  exit 0
fi

# Honours CLAUDE_BUDDY_SETTINGS_DIR like the app does; a failed read is reported
# on stderr rather than discarded (CB-258).
saved_profiles() {
  local settings="${CLAUDE_BUDDY_SETTINGS_DIR:-$HOME/Library/Application Support/ClaudeBuddy}/settings.json"
  [[ -f "$settings" ]] || return 0

  osascript -l JavaScript -e '
    ObjC.import("Foundation");
    function run(a) {
      const s = $.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null);
      if (s.isNil()) return "";
      let parsed;
      try { parsed = JSON.parse(ObjC.unwrap(s)); } catch (e) { throw new Error("settings.json is not valid JSON"); }
      const dirs = parsed.grokHomes;
      if (!Array.isArray(dirs)) return "";
      return dirs.filter(function (d) { return typeof d === "string" && d.length > 0; }).join("\n");
    }
  ' "$settings" || {
    echo "warning: could not read the saved profile list from $settings (exit $?); no extra profiles were wired." >&2
    return 1
  }
}

SELF="$HERE/$(basename "$0")"
[[ -x "$SELF" ]] || SELF="$0"

LIST_UNREADABLE=0
saved=$(saved_profiles) || LIST_UNREADABLE=1

for name in "${EXTRA_PROFILES[@]+"${EXTRA_PROFILES[@]}"}" $saved; do
  [[ -n "$name" ]] || continue
  extra="$HOME/$name"
  echo
  echo "=== extra Grok home: $extra"
  # An array, not ${UNINSTALL:+--uninstall}: UNINSTALL is 0 or 1 and "0" is a
  # non-empty string, so :+ expanded every time and an *install* unwired every
  # extra Grok home it was meant to wire (CB-258). install-macos-hooks.sh hit the
  # same trap and says so beside the same fix.
  mode=()
  [[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)
  "$SELF" "${mode[@]+"${mode[@]}"}" --grok-home "$extra" --no-profiles
done

# Exit 3 means "the saved profile list could not be read": the default profile was
# still wired, but any extra ones were not, and the app turns that code into a
# message of its own (HookInstaller.SavedListUnreadableExit). Exit 0 here used to
# make an unreadable list indistinguishable from an empty one (CB-258).
if [[ $LIST_UNREADABLE -eq 1 ]]; then
  exit 3
fi
