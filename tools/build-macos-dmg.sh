#!/usr/bin/env bash
# Packages "Claude Buddy.app" into a distributable, notarized .dmg.
#
#   ./tools/build-macos-dmg.sh                  # -> dist/ClaudeBuddy-<ver>-osx-arm64.dmg
#   ./tools/build-macos-dmg.sh --rid osx-x64    # Intel
#   ./tools/build-macos-dmg.sh --skip-notarize  # sign but don't submit to Apple
#
# Signing and notarization are driven entirely by environment variables, so the
# same script runs locally and in CI. With none of them set you still get a
# working DMG — just an ad-hoc signed one that Gatekeeper will block on another
# Mac, which is useful for testing the packaging itself but not for shipping.
#
#   MACOS_SIGNING_IDENTITY   "Developer ID Application: Name (TEAMID)"
#
# Notarization takes either credential set. An App Store Connect API key is
# preferred and checked first: it belongs to the team rather than to one
# person's Apple ID, survives that person changing their password or leaving,
# and can be revoked on its own.
#
#   MACOS_NOTARY_KEY_P8_BASE64  the .p8 private key, base64-encoded
#   MACOS_NOTARY_KEY_ID         the key's 10-character Key ID
#   MACOS_NOTARY_ISSUER_ID      the issuer UUID (one per team)
#
# Or the older Apple ID route, kept as a fallback:
#
#   MACOS_NOTARY_APPLE_ID    Apple ID email for notarytool
#   MACOS_NOTARY_PASSWORD    app-specific password (NOT the Apple ID password)
#   MACOS_NOTARY_TEAM_ID     10-character team ID
#
# Why notarize at all: without it, a user who downloads the DMG gets "Claude
# Buddy is damaged and can't be opened" (quarantine + no notarization ticket),
# which reads as a broken download rather than a security prompt. Notarizing
# and stapling means a plain double-click works.

set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="Claude Buddy"
DIST="dist"
SKIP_NOTARIZE=0

case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  *)     RID="osx-x64" ;;
esac

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="$2"; shift 2 ;;
    --skip-notarize) SKIP_NOTARIZE=1; shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' ClaudeBuddy.csproj | head -1)"
[[ -n "$VERSION" ]] || { echo "Could not read <Version> from ClaudeBuddy.csproj" >&2; exit 1; }

SIGN_IDENTITY="${MACOS_SIGNING_IDENTITY:-}"
APP="$DIST/$APP_NAME.app"
DMG="$DIST/ClaudeBuddy-$VERSION-$RID.dmg"
STAGE="$DIST/dmg-stage"

# Build the bundle first; it reads MACOS_SIGNING_IDENTITY from the environment
# and does the hardened-runtime signing that notarization requires.
./tools/build-macos-app.sh --rid "$RID"

echo "==> Staging DMG contents"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"

# The drag-to-install target. A symlink rather than instructions, because it is
# the convention every Mac user already knows.
ln -s /Applications "$STAGE/Applications"

# Orbs only appear once the hook is wired into Claude Code's settings.json, and
# an app that shows nothing looks broken rather than unconfigured. A .app cannot
# run an installer wizard the way the Windows setup does, so hook setup ships as
# a double-clickable script instead. It resolves the installed app first so it
# keeps working after the DMG is ejected, which is the normal case: people drag
# the app across, then run this.
cat > "$STAGE/Install Hooks.command" <<'COMMAND'
#!/bin/bash
# Wires the Claude Buddy hook into every agent CLI on this machine — Claude Code,
# Codex, or both — so their sessions show up as orbs.
#
# Re-run this any time to repair it, or after installing a second CLI; it
# converges rather than duplicating. Pass --uninstall to remove the entries.
set -euo pipefail

for app in "/Applications/Claude Buddy.app" \
           "$HOME/Applications/Claude Buddy.app" \
           "$(cd "$(dirname "$0")" && pwd)/Claude Buddy.app"; do
  script="$app/Contents/Resources/install-hooks.sh"
  if [[ -x "$script" ]]; then
    echo "Using $app"
    echo
    exec "$script" "$@"
  fi
done

echo "Couldn't find Claude Buddy.app in /Applications, ~/Applications, or next"
echo "to this script. Drag Claude Buddy to Applications first, then run this again."
exit 1
COMMAND
chmod +x "$STAGE/Install Hooks.command"

cat > "$STAGE/Read Me First.txt" <<READ_ME
Claude Buddy $VERSION
=====================

1. Drag "Claude Buddy" onto the Applications folder in this window.

2. Double-click "Install Hooks.command".

   This step is not optional. Claude Buddy shows an orb per coding-agent
   session, and it learns about sessions from a hook that the agent runs.
   Until that hook is wired up, the app runs correctly but displays nothing
   at all -- which looks broken but isn't.

   It wires whichever of Claude Code and Codex it finds, and says so for
   either one it doesn't. Run it again after installing the other; it repairs
   rather than duplicating.

   Your existing config is backed up first -- to
   ~/.claude/settings.json.claudebuddy-backup and
   ~/.codex/hooks.json.claudebuddy-backup respectively.

   Already-running sessions won't produce orbs until you restart them,
   because hooks are read once at session start.

   Codex only: Codex will not run a hook it has not been told to trust. The
   first time you start Codex after this, accept the hook review it shows
   you, or run /hooks inside it and trust the Claude Buddy entries. Until
   you do, no Codex hook fires and no Codex orb appears -- and nothing
   anywhere tells you why.

3. Launch Claude Buddy from Applications.

   Nothing appears in the Dock and no window opens -- that is correct. Look
   for the icon in the menu bar. With no sessions running you should see zero
   orbs and a slate-colored menu bar icon.

4. The first time you click an orb, macOS asks for Automation permission.
   Click-to-focus needs it to bring the session's terminal window forward.

To start it automatically, add it in System Settings > General >
Login Items & Extensions.

Uninstalling: drag the app to the Trash, and run
  "Install Hooks.command" --uninstall
from a Terminal to take the hook entries back out of every CLI it wired.

Source, issues and docs: https://github.com/Uplift-Foundation/Claude-Buddy
MIT licensed.
READ_ME

if [[ -n "$SIGN_IDENTITY" ]]; then
  # Sign the helper script too. A quarantined shell script from a downloaded
  # image trips Gatekeeper on double-click; a Developer ID signature clears it.
  codesign --force --timestamp --sign "$SIGN_IDENTITY" \
    "$STAGE/Install Hooks.command"
fi

echo "==> Building $DMG"
rm -f "$DMG"
# UDZO (zlib) rather than the newer ULFO/ULMO: it is readable by every macOS
# version this app supports, back to the 11.0 floor in Info.plist.
hdiutil create \
  -volname "$APP_NAME $VERSION" \
  -srcfolder "$STAGE" \
  -fs HFS+ \
  -format UDZO \
  -ov \
  -quiet \
  "$DMG"

rm -rf "$STAGE"

if [[ -z "$SIGN_IDENTITY" ]]; then
  echo "==> Built $DMG (UNSIGNED — for local testing only)"
  echo "    Set MACOS_SIGNING_IDENTITY to produce a distributable image."
  exit 0
fi

echo "==> Signing $DMG"
codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"

if [[ $SKIP_NOTARIZE -eq 1 ]]; then
  echo "==> Built $DMG (signed, notarization skipped)"
  exit 0
fi

# Pick the credential set. API key first — see the header for why.
if [[ -n "${MACOS_NOTARY_KEY_P8_BASE64:-}" ]]; then
  : "${MACOS_NOTARY_KEY_ID:?MACOS_NOTARY_KEY_P8_BASE64 is set, so MACOS_NOTARY_KEY_ID is required too}"

  echo "==> Notarizing with an App Store Connect API key"
  # notarytool wants a file path, so the key has to touch disk. Keep it in a
  # 0700 temp directory and delete it on any exit path, including failure —
  # a leaked .p8 is a credential someone else can notarize with.
  KEY_DIR="$(mktemp -d "${TMPDIR:-/tmp}/claudebuddy-notary.XXXXXX")"
  chmod 700 "$KEY_DIR"
  trap 'rm -rf "$KEY_DIR"' EXIT
  KEY_FILE="$KEY_DIR/AuthKey.p8"
  printf '%s' "$MACOS_NOTARY_KEY_P8_BASE64" | base64 --decode > "$KEY_FILE"
  chmod 600 "$KEY_FILE"

  NOTARY_AUTH=(--key "$KEY_FILE" --key-id "$MACOS_NOTARY_KEY_ID")
  # --issuer is required for a Team key and must NOT be passed for an Individual
  # key, so it is appended only when set rather than always.
  if [[ -n "${MACOS_NOTARY_ISSUER_ID:-}" ]]; then
    NOTARY_AUTH+=(--issuer "$MACOS_NOTARY_ISSUER_ID")
  fi
else
  : "${MACOS_NOTARY_APPLE_ID:?set MACOS_NOTARY_KEY_P8_BASE64 (preferred) or MACOS_NOTARY_APPLE_ID (or pass --skip-notarize)}"
  : "${MACOS_NOTARY_PASSWORD:?set MACOS_NOTARY_PASSWORD (or pass --skip-notarize)}"
  : "${MACOS_NOTARY_TEAM_ID:?set MACOS_NOTARY_TEAM_ID (or pass --skip-notarize)}"

  echo "==> Notarizing with an Apple ID and app-specific password"
  NOTARY_AUTH=(--apple-id "$MACOS_NOTARY_APPLE_ID"
               --password "$MACOS_NOTARY_PASSWORD"
               --team-id "$MACOS_NOTARY_TEAM_ID")
fi

echo "    (this usually takes a few minutes)"
# --wait blocks until Apple returns a verdict. Without it the staple below would
# race the notary service and fail with "does not have a ticket".
xcrun notarytool submit "$DMG" "${NOTARY_AUTH[@]}" --wait

# Stapling writes the ticket into the image, so Gatekeeper can approve it
# offline. Skip this and a user with no network sees the same "damaged" error
# notarizing was supposed to prevent.
echo "==> Stapling ticket"
xcrun stapler staple "$DMG"

echo "==> Verifying Gatekeeper acceptance"
spctl --assess --type open --context context:primary-signature -v "$DMG"

echo "==> Built $DMG (signed, notarized, stapled)"
