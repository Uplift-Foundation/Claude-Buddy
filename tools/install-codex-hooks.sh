#!/usr/bin/env bash
# Installs the Claude Buddy hook into Codex's macOS hook config.
#
# The sibling of tools/install-macos-hooks.sh, which does the same job for
# Claude Code, and it exists for the same reason: the hook is what makes orbs
# appear, and an app with no hook wired up doesn't error, it just sits there
# showing nothing.
#
#   install-codex-hooks.sh              # install / repair
#   install-codex-hooks.sh --uninstall  # remove just our entries
#
# A second Codex account run as `CODEX_HOME=~/.codex-work codex` is a separate
# hooks.json and invisible to the default wiring, exactly as an extra
# CLAUDE_CONFIG_DIR account is for Claude Code. Every directory name saved in
# the app's own settings ("Codex profiles" in the Settings window) is wired too,
# in addition to the default and never instead of it.
#
# Safe to re-run: it strips any existing Claude Buddy entries before adding
# fresh ones, so it converges rather than accumulating duplicates. That matters
# more here than it does for Claude Code, because Codex's own `/import` copies
# a Claude Code setup across and can leave hooks of its own behind.
#
# Runs from either the repo (tools/install-codex-hooks.sh, hook script one level
# up) or from inside the installed app bundle (Contents/Resources, hook script
# right alongside), so the shipped .app can offer hook setup without the user
# needing a clone.
#
# Three things differ from the Claude Code installer, all measured rather than
# assumed — see docs/codex-findings.md:
#
#  1. **The target is its own file.** $CODEX_HOME/hooks.json is discovered
#     automatically; nothing needs adding to config.toml. Confirmed by asking a
#     running app-server for `hooks/list` and seeing entries come back tagged
#     source "user".
#  2. **Codex has no Notification event.** PermissionRequest is the analogue,
#     and it is a better one — it carries the tool name. An event name Codex
#     does not know is dropped *silently*, with no warning and no error, so a
#     wrong name here would look exactly like a hook that never fires.
#  3. **PermissionRequest is installed async.** A synchronous hook on that event
#     can deny the user's approval by exiting non-zero or printing anything that
#     isn't its expected JSON. `async: true` makes it structurally unable to
#     return a decision at all, which is the only guarantee worth having when
#     the failure mode is refusing something the user asked for.

set -euo pipefail

UNINSTALL=0
NO_PROFILES=0
EXTRA_PROFILES=()
CODEX_DIR="${CODEX_HOME:-$HOME/.codex}"
HOOK_DIR=""
HOOKS_JSON=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --uninstall) UNINSTALL=1; shift ;;
    --codex-home) CODEX_DIR="$2"; shift 2 ;;
    # One extra Codex home, by name under $HOME. The saved list is used when
    # this is absent.
    --profile-dir) EXTRA_PROFILES+=("$2"); shift 2 ;;
    # Don't recurse into the saved profiles; set when this script re-invokes
    # itself for one of them.
    --no-profiles) NO_PROFILES=1; shift ;;
    --hooks-json) HOOKS_JSON="$2"; shift 2 ;;
    --hook-dir) HOOK_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$HOOK_DIR"   ]] || HOOK_DIR="$CODEX_DIR/claude-buddy"
[[ -n "$HOOKS_JSON" ]] || HOOKS_JSON="$CODEX_DIR/hooks.json"

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

# What goes in the config, which is not always what goes on disk. For the
# default location that is the literal string $HOME rather than this shell's
# expansion of it — hook commands run through a shell, so leaving it unexpanded
# makes hooks.json portable across machines and usernames, and makes what the
# installer writes byte-identical to codex-hooks-snippet-macos.json for anyone
# comparing the two. A custom --codex-home has no such shorthand and gets the
# real path.
if [[ "$HOOK_DIR" == "$HOME/.codex/claude-buddy" ]]; then
  CONFIGURED='$HOME/.codex/claude-buddy/ClaudeBuddyHook.sh'
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

if [[ ! -f "$HOOKS_JSON" ]]; then
  mkdir -p "$(dirname "$HOOKS_JSON")"
  printf '{}' > "$HOOKS_JSON"
  echo "Created $HOOKS_JSON"
fi

BACKUP="$HOOKS_JSON.claudebuddy-backup"
cp "$HOOKS_JSON" "$BACKUP"
echo "Backed up hooks to $BACKUP"

# JavaScript for Automation, for the reason the Claude Code installer gives:
# stock macOS has no guaranteed jq and /usr/bin/python3 is a stub that prompts
# to install the Command Line Tools, but osascript has had a real JSON parser
# since 10.10 — so this works on a clean machine, which is the case an
# installer has to work on.
JXA=$(cat <<'JAVASCRIPT'
ObjC.import('Foundation');

function readUtf8(path) {
  const s = $.NSString.stringWithContentsOfFileEncodingError(path, $.NSUTF8StringEncoding, null);
  return s.isNil() ? '' : ObjC.unwrap(s);
}

function run(argv) {
  const hooksPath = argv[0];
  const uninstall = argv[1] === 'uninstall';
  const scriptPath = argv[2];
  const script = '"' + scriptPath + '"';

  const raw = readUtf8(hooksPath).trim();
  const config = raw === '' ? {} : JSON.parse(raw);

  let hooks = (config.hooks && typeof config.hooks === 'object') ? config.hooks : {};

  // Which Codex event drives which orb state.
  //
  // PostToolUse is here and has no counterpart in the Claude Code table. It
  // exists to undo an amber that turned out not to need answering: Codex fires
  // PermissionRequest before it decides whether to ask, so a call that is then
  // auto-approved would otherwise leave the orb saying "needs you" until the
  // turn ended.
  const wanted = [
    { event: 'SessionStart',      matcher: null, state: 'idle',       async: false },
    { event: 'UserPromptSubmit',  matcher: null, state: 'generating', async: false },
    { event: 'PreToolUse',        matcher: '.*', state: 'generating', async: false },
    { event: 'PermissionRequest', matcher: null, state: 'waiting',    async: true  },
    { event: 'PostToolUse',       matcher: '.*', state: 'generating', async: false },
    { event: 'Stop',              matcher: null, state: 'idle',       async: false },
    { event: 'SessionEnd',        matcher: null, state: 'ended',      async: false }
  ];

  // Strip our own entries wherever they appear, so re-running repairs rather
  // than duplicating and --uninstall leaves hooks belonging to
  // other tools untouched.
  // Matched on the filename of the script rather than its full path, so a
  // config written by an older version, or carried over by the /import command
  // in Codex from
  // a Claude Code setup, which points at ~/.claude — is still recognised as
  // ours and replaced instead of left to fire twice.
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
      const handler = { type: 'command', command: 'bash ' + script + ' codex ' + entry.state };
      if (entry.async) { handler.async = true; }
      const group = { hooks: [handler] };
      if (entry.matcher) { group.matcher = entry.matcher; }
      hooks[entry.event] = [].concat(hooks[entry.event] || []).concat([group]);
    }
  }

  config.hooks = hooks;
  if (Object.keys(config.hooks).length === 0) { delete config.hooks; }

  return JSON.stringify(config, null, 2);
}
JAVASCRIPT
)

MODE=$([[ $UNINSTALL -eq 1 ]] && echo uninstall || echo install)

# Write via a temp file and mv, so an interrupted run can't leave a truncated
# hooks.json behind.
TMP="$(mktemp "${TMPDIR:-/tmp}/claudebuddy-codex-hooks.XXXXXX")"
trap 'rm -f "$TMP"' EXIT

osascript -l JavaScript -e "$JXA" "$HOOKS_JSON" "$MODE" "$CONFIGURED" > "$TMP"

# Refuse to install anything that isn't valid JSON — better to keep the old file.
if ! osascript -l JavaScript -e 'ObjC.import("Foundation"); function run(a){ JSON.parse(ObjC.unwrap($.NSString.stringWithContentsOfFileEncodingError(a[0], $.NSUTF8StringEncoding, null))); return "ok" }' "$TMP" >/dev/null 2>&1; then
  echo "Refusing to write: generated hooks.json did not parse. Left $HOOKS_JSON untouched." >&2
  echo "Your backup is at $BACKUP" >&2
  exit 1
fi

mv "$TMP" "$HOOKS_JSON"
trap - EXIT

if [[ $UNINSTALL -eq 1 ]]; then
  echo "Removed Claude Buddy hooks from $HOOKS_JSON."
  echo "The installed hook script was left in place; delete $HOOK_DIR if you want it gone."
  exit 0
fi

echo "Wired 7 hook entries into $HOOKS_JSON"
echo
# The part people get stuck on. Codex does not run a hook it has not been told
# to trust, and a hooks.json written by anything other than Codex itself starts
# out untrusted — confirmed by reading trustStatus back from a running
# app-server. Until this is done there is no error anywhere: no hook fires, no
# orb appears, and the app looks broken.
echo "One more step, and nothing works without it:"
echo
echo "  Codex will not run a hook it has not been told to trust, and a hooks.json"
echo "  written by anything other than Codex itself starts out untrusted. Start"
echo "  Codex and accept the hook review it shows you, or run /hooks inside it"
echo "  and trust the Claude Buddy entries."
echo
echo "  Editing hooks.json later — including re-running this installer — changes"
echo "  its hash and asks you again."
echo
echo "Then restart any running Codex sessions: hooks are read at session start,"
echo "so existing sessions will not produce orbs until they are restarted."

# --- extra Codex accounts -----------------------------------------------------

# The directory names saved in the app's own settings, one per line. JXA for the
# reason the merge above uses it, and a missing file is not an error.
#
# This path does *not* follow HOME — SpecialFolder.ApplicationData resolves
# through the OS, so the app reads this exact file whatever HOME says.
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
      const dirs = parsed.codexHomes;
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
    # A bare name under $HOME, the shape the Settings window stores. A path is
    # refused rather than resolved: CODEX_HOME can point anywhere, and guessing
    # would write hooks somewhere nobody asked for.
    if [[ "$profile" == */* ]]; then
      echo "Skipping profile '$profile': expected a directory name under \$HOME, not a path." >&2
      continue
    fi

    echo
    echo "--- codex profile: $profile"

    # An array, not ${UNINSTALL:+--uninstall}: UNINSTALL is 0 or 1 and "0" is a
    # non-empty string, so :+ would expand every time and an install would
    # quietly unwire each extra profile. That exact mistake was made and caught
    # in the Claude Code installer next door.
    mode=()
    [[ $UNINSTALL -eq 1 ]] && mode=(--uninstall)

    "$0" "${mode[@]+"${mode[@]}"}" --no-profiles \
         --codex-home "$HOME/$profile"
  done
fi
