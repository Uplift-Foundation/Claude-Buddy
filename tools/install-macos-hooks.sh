#!/usr/bin/env bash
# Installs the Claude Buddy hook into Claude Code's macOS settings.
#
# The bash twin of tools/install-windows-hooks.ps1, and the reason it exists is
# the same: the hook is what makes orbs appear, and an app with no hook wired up
# doesn't error, it just sits there showing nothing. Hand-merging JSON into a
# settings.json that already holds your model, permissions and status line is
# both fiddly and easy to get wrong, so do it mechanically.
#
#   install-macos-hooks.sh              # install / repair
#   install-macos-hooks.sh --uninstall  # remove just our entries
#
# Extra Claude Code accounts — a second one run as
# `CLAUDE_CONFIG_DIR=~/.claude-work claude` — are separate settings.json files
# and invisible to the default wiring. Every directory name saved in the app's
# own settings ("Claude Code profiles" in the Settings window) is wired too, in
# addition to ~/.claude and never instead of it. That mirrors what
# install-windows-hooks.ps1 has always done; the two had drifted, and the macOS
# side was the half that silently left a second account unwired.
#
# Safe to re-run: it strips any existing Claude Buddy entries before adding
# fresh ones, so it converges rather than accumulating duplicates.
#
# Runs from either the repo (tools/install-macos-hooks.sh, hook script one level
# up) or from inside the installed app bundle (Contents/Resources, hook script
# right alongside), so the shipped .app can offer hook setup without the user
# needing a clone.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
HOOK_DIR="$HOME/.claude/claude-buddy"
SETTINGS="$HOME/.claude/settings.json"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --settings) SETTINGS="$2"; shift 2 ;;
    # One extra profile, explicitly. The saved list is used when this is absent.
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    # Don't recurse into the saved profiles. Set when this script re-invokes
    # itself for one of them, which is how each gets its own settings path
    # without the merge below having to loop.
    --no-profiles) NO_PROFILES=1; shift ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

HERE="$(cd "$(dirname "$0")" && pwd)"

# Alongside (installed app bundle) wins over one level up (repo checkout).
if [[ -f "$HERE/ClaudeBuddyHook.sh" ]]; then
  SOURCE="$HERE/ClaudeBuddyHook.sh"
elif [[ -f "$HERE/../ClaudeBuddyHook.sh" ]]; then
  SOURCE="$HERE/../ClaudeBuddyHook.sh"
else
  SOURCE=""
fi

INSTALLED="$HOOK_DIR/ClaudeBuddyHook.sh"

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

if [[ ! -f "$SETTINGS" ]]; then
  # An absent settings file is normal on a fresh install; start one rather than
  # refusing, but never invent anything beyond the hooks themselves.
  mkdir -p "$(dirname "$SETTINGS")"
  printf '{}' > "$SETTINGS"
  echo "Created $SETTINGS"
fi

BACKUP="$SETTINGS.claudebuddy-backup"
cp "$SETTINGS" "$BACKUP"
echo "Backed up settings to $BACKUP"

# The JSON surgery runs in JavaScript for Automation. Stock macOS has no
# guaranteed jq (only 15+ ships one) and /usr/bin/python3 is a stub that
# prompts to install the Command Line Tools, but osascript has been present
# since 10.10 and brings a real JSON parser with it — so this works on a clean
# machine with nothing installed, which is the case that matters for an
# installer. Foundation is imported for the file read so UTF-8 is decoded
# exactly rather than through osascript's text coercion.
JXA=$(cat <<'JAVASCRIPT'
ObjC.import('Foundation');

function readUtf8(path) {
  const s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  return s.isNil() ? '' : ObjC.unwrap(s);
}

function run(argv) {
  const settingsPath = argv[0];
  const uninstall = argv[1] === 'uninstall';
  // The literal string $HOME, not this shell's expansion of it: hook commands
  // run through a shell, so keeping it unexpanded makes settings.json portable
  // across machines and usernames. This matches claude-hooks-snippet-macos.json.
  const script = '"$HOME/.claude/claude-buddy/ClaudeBuddyHook.sh"';

  const raw = readUtf8(settingsPath).trim();
  const settings = raw === '' ? {} : JSON.parse(raw);

  let hooks = (settings.hooks && typeof settings.hooks === 'object') ? settings.hooks : {};

  // Which Claude Code events drive which orb state. Notification carries a
  // matcher because only some notifications mean "Claude needs you"; the rest
  // would turn every orb amber, which would make amber meaningless.
  const wanted = [
    { event: 'SessionStart',     matcher: null,                  state: 'idle' },
    { event: 'UserPromptSubmit', matcher: null,                  state: 'generating' },
    { event: 'PreToolUse',       matcher: '.*',                  state: 'generating' },
    { event: 'Stop',             matcher: null,                  state: 'idle' },
    { event: 'SessionEnd',       matcher: null,                  state: 'ended' },
    { event: 'Notification',     matcher: 'permission_prompt',    state: 'waiting' },
    { event: 'Notification',     matcher: 'elicitation_dialog',   state: 'waiting' },
    { event: 'Notification',     matcher: 'elicitation_complete', state: 'generating' }
  ];

  // Strip our own entries wherever they appear, so re-running repairs rather
  // than duplicating and --uninstall leaves other tools' hooks untouched.
  for (const name of Object.keys(hooks)) {
    const groups = [].concat(hooks[name] || []);
    const kept = [];

    for (const group of groups) {
      if (!group || typeof group !== 'object') continue;
      const inner = [].concat(group.hooks || []).filter(function (h) {
        return h && typeof h.command === 'string' &&
               h.command.indexOf('ClaudeBuddyHook.sh') === -1;
      });
      if (inner.length > 0) { group.hooks = inner; kept.push(group); }
    }

    if (kept.length > 0) { hooks[name] = kept; } else { delete hooks[name]; }
  }

  if (!uninstall) {
    for (const entry of wanted) {
      const group = { hooks: [{ type: 'command', command: 'bash ' + script + ' ' + entry.state }] };
      if (entry.matcher) { group.matcher = entry.matcher; }
      hooks[entry.event] = [].concat(hooks[entry.event] || []).concat([group]);
    }
  }

  settings.hooks = hooks;
  // An empty hooks object is noise in a settings file; drop the key entirely.
  if (Object.keys(settings.hooks).length === 0) { delete settings.hooks; }

  return JSON.stringify(settings, null, 2);
}
JAVASCRIPT
)

MODE=$([[ $UNINSTALL -eq 1 ]] && echo uninstall || echo install)

# Write via a temp file and mv, so an interrupted run can't leave a truncated
# settings.json behind — losing the user's model and permissions to a failed
# hook install would be a bad trade.
TMP="$(mktemp "${TMPDIR:-/tmp}/claudebuddy-settings.XXXXXX")"
trap 'rm -f "$TMP"' EXIT

# No BOM and no trailing newline games: System.Text.Json, which both Claude Code
# and this app use, rejects a leading BOM as an invalid start of value.
osascript -l JavaScript -e "$JXA" "$SETTINGS" "$MODE" > "$TMP"

# Refuse to install anything that isn't valid JSON — better to keep the old file.
if ! osascript -l JavaScript -e 'ObjC.import("Foundation"); function run(a){ JSON.parse(ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null))); return "ok" }' "$TMP" >/dev/null 2>&1; then
  echo "Refusing to write: generated settings.json did not parse. Left $SETTINGS untouched." >&2
  echo "Your backup is at $BACKUP" >&2
  exit 1
fi

mv "$TMP" "$SETTINGS"
trap - EXIT

if [[ $UNINSTALL -eq 1 ]]; then
  echo "Removed Claude Buddy hooks from $SETTINGS."
  echo "The installed hook script was left in place; delete $HOOK_DIR if you want it gone."
else
  echo "Wired 8 hook entries into $SETTINGS"
  echo
  echo "Restart any running Claude Code sessions — hooks are read at session start,"
  echo "so existing sessions will not produce orbs until they are restarted."
fi

# --- extra accounts -----------------------------------------------------------

# The directory names saved in the app's own settings, one per line.
#
# Read with JXA for the reason the merge above uses it: a clean Mac has no jq,
# and /usr/bin/python3 is a stub that prompts to install the Command Line Tools.
# A missing or unreadable file is not an error — it just means no extra
# profiles, which is the common case.
#
# Note this file does *not* follow HOME: SpecialFolder.ApplicationData resolves
# through the OS, so the app reads this exact path whatever HOME says, and so
# must this.
saved_profiles() {
  local settings="$HOME/Library/Application Support/ClaudeBuddy/settings.json"
  [[ -f "$settings" ]] || return 0

  osascript -l JavaScript -e '
    ObjC.import("Foundation");
    function run(a) {
      const s = $.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null);
      if (s.isNil()) return "";
      let parsed;
      try { parsed = JSON.parse(ObjC.unwrap(s)); } catch (e) { return ""; }
      const dirs = parsed.claudeCodeProfileDirs;
      if (!Array.isArray(dirs)) return "";
      return dirs.filter(function (d) { return typeof d === "string" && d.length > 0; }).join("\n");
    }' "$settings" 2>/dev/null
}

if [[ $NO_PROFILES -eq 0 ]]; then
  profiles=()
  if [[ ${#EXTRA_PROFILES[@]} -gt 0 ]]; then
    profiles=("${EXTRA_PROFILES[@]}")
  else
    while IFS= read -r line; do
      [[ -n "$line" ]] && profiles+=("$line")
    done < <(saved_profiles)
  fi

  for profile in "${profiles[@]+"${profiles[@]}"}"; do
    # A bare name under $HOME, the same shape the Settings window stores and
    # the Windows installer expects. Anything with a slash in it is refused
    # rather than resolved: CLAUDE_CONFIG_DIR pointing outside $HOME is a
    # different feature, and guessing at it would write hooks somewhere the
    # user did not ask for.
    if [[ "$profile" == */* ]]; then
      echo "Skipping profile '$profile': expected a directory name under \$HOME, not a path." >&2
      continue
    fi

    echo
    echo "--- profile: $profile"

    # Built as an array rather than with ${UNINSTALL:+--uninstall}, which is
    # wrong here and was: UNINSTALL is 0 or 1, and "0" is a non-empty string,
    # so :+ expanded it every time and an *install* quietly unwired every extra
    # profile it was supposed to be wiring. Caught only because the test looked
    # at the resulting file rather than at the exit code.
    mode=()
    [[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)

    # Re-invoked rather than looped internally, so each profile takes exactly
    # the same path through the merge as the default one — including the
    # backup, the JSON validation and the refusal to write anything that did
    # not parse. A loop would have been a second, less-tested code path.
    "$0" "${mode[@]+"${mode[@]}"}" --no-profiles \
         --settings "$HOME/$profile/settings.json" \
         --hook-dir "$HOOK_DIR"
  done
fi
