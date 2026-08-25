#!/usr/bin/env bash
# Builds "Claude Buddy.app" — a real macOS app bundle you can double-click,
# drop in /Applications, and add to Login Items.
#
#   ./tools/build-macos-app.sh              # build into dist/
#   ./tools/build-macos-app.sh --install    # ...and copy to /Applications
#   ./tools/build-macos-app.sh --rid osx-x64
#
# Signing: set MACOS_SIGNING_IDENTITY to a "Developer ID Application: ..."
# identity in the keychain to produce a distributable, notarizable build.
# Without it the bundle is ad-hoc signed, which is fine locally but cannot be
# notarized or opened on someone else's Mac without a Gatekeeper override.
# tools/build-macos-dmg.sh is the packaging entry point and sets this up for you.
#
# Why a bundle rather than the bare published binary:
#   * Finder/Dock/Login Items treat it as an application.
#   * LSUIElement makes it a menu-bar-only app at the OS level (no Dock icon,
#     no app switcher entry) instead of relying on an Avalonia setting.
#   * It carries NSAppleEventsUsageDescription, which macOS requires before
#     it will even show the Automation prompt that click-to-focus needs.
#   * A bundle has a stable code identity, so the Automation permission you
#     grant sticks to "Claude Buddy" instead of to whatever terminal happened
#     to launch a loose binary.

set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="Claude Buddy"
# Kept as-is even though the canonical repo is Uplift-Foundation/Claude-Buddy, and
# deliberately so: macOS keys the Automation (Apple Events) consent a user grants
# to the bundle identifier. Renaming it makes every existing install look like a
# brand-new app, so click-to-focus silently stops working until each user
# re-approves it in System Settings — and that grant is invisible when it breaks,
# which cost a long debugging session once already (see the Automation note in the
# README). Not worth it for a cosmetic rename. The signing certificate belongs to
# the UPLIFT FOUNDATION team, so it signs this identifier regardless of which
# repository builds it. If it is ever renamed, do it in a release whose notes say
# plainly that click-to-focus needs re-approving.
BUNDLE_ID="io.github.wtvamp.claudebuddy"
DIST="dist"
INSTALL=0

# The csproj owns the version; parse it out rather than keeping a second copy
# here that would silently drift from the one compiled into the binary.
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' ClaudeBuddy.csproj | head -1)"
[[ -n "$VERSION" ]] || { echo "Could not read <Version> from ClaudeBuddy.csproj" >&2; exit 1; }

# CFBundleVersion must be a plain dotted number — a "-beta" suffix in it makes
# the bundle unlaunchable — so strip any prerelease label for that key while
# CFBundleShortVersionString keeps the full label users should see.
VERSION_NUMERIC="${VERSION%%-*}"

SIGN_IDENTITY="${MACOS_SIGNING_IDENTITY:-}"
ENTITLEMENTS="tools/ClaudeBuddy.entitlements"

# Default to this Mac's architecture; override for cross-building.
case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  *)     RID="osx-x64" ;;
esac

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) INSTALL=1; shift ;;
    --rid) RID="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

APP="$DIST/$APP_NAME.app"
CONTENTS="$APP/Contents"

echo "==> Publishing ($RID)"
# Multi-file on purpose: PublishSingleFile (the csproj default, for handing
# someone one loose executable) would self-extract native libs to a temp dir
# at every launch, which is exactly what a .app bundle exists to avoid.
dotnet publish ClaudeBuddy.csproj -c Release -r "$RID" \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -o "$DIST/publish-$RID" \
  --nologo -v quiet

echo "==> Assembling $APP"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"
cp -R "$DIST/publish-$RID/." "$CONTENTS/MacOS/"
chmod +x "$CONTENTS/MacOS/ClaudeBuddy"

# Ship the hook and both its installers inside the bundle. Orbs only appear
# once the hook is wired into the CLI's own config, so carrying all three means
# a user who downloaded a DMG (and has no clone) can still finish setup with one
# command against a stable path under /Applications. Both installers prefer a
# hook script sitting alongside them, which is this layout.
#
# One hook script, two per-CLI installers, and install-hooks.sh over the top of
# them. The script is the same for Claude Code and Codex and only takes a
# different first argument, but where it has to be registered is completely
# different — settings.json under ~/.claude for one, hooks.json under
# $CODEX_HOME for the other, with a trust step that only the second has.
#
# install-hooks.sh is what every install path calls, and the per-CLI ones are
# what it calls. Nobody should have to know which of two scripts their machine
# needs; that knowledge belongs in a script, not in a README step someone reads
# once.
cp ClaudeBuddyHook.sh \
   tools/install-hooks.sh \
   tools/install-macos-hooks.sh \
   tools/install-codex-hooks.sh "$CONTENTS/Resources/"
chmod +x "$CONTENTS/Resources/ClaudeBuddyHook.sh" \
         "$CONTENTS/Resources/install-hooks.sh" \
         "$CONTENTS/Resources/install-macos-hooks.sh" \
         "$CONTENTS/Resources/install-codex-hooks.sh"

echo "==> Building icon"
ICONSET="$DIST/ClaudeBuddy.iconset"
rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size Assets/appicon-1024.png \
    --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double Assets/appicon-1024.png \
    --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$CONTENTS/Resources/ClaudeBuddy.icns"
rm -rf "$ICONSET"

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>              <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>       <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>        <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>        <string>ClaudeBuddy</string>
    <key>CFBundleIconFile</key>          <string>ClaudeBuddy</string>
    <key>CFBundlePackageType</key>       <string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key>           <string>$VERSION_NUMERIC</string>
    <key>LSMinimumSystemVersion</key>    <string>11.0</string>
    <key>NSHighResolutionCapable</key>   <true/>
    <!-- Menu-bar-only: no Dock icon, no Cmd-Tab entry. -->
    <key>LSUIElement</key>               <true/>
    <!-- Shown in the Automation prompt the first time an orb is clicked, and
         again for Claude Desktop the first time a profile is quit from the
         menu (quitting sends a quit Apple Event, which is TCC-gated). -->
    <key>NSAppleEventsUsageDescription</key>
    <string>Claude Buddy uses automation to bring the terminal window of a Claude Code session to the front when you click its orb, and to quit a Claude Desktop profile when you choose Quit from its menu.</string>
    <!-- Shown the moment PvRecorder opens the input device, which only
         happens if the user has turned on voice input in Settings and then
         clicks the mic that appears on hover — see VoiceRecorder. Without
         this key macOS kills the process instead of prompting, so it has to
         be here before that feature can work at all, the same way the
         Automation key above has to exist before click-to-focus can prompt.
         Typing the transcribed text into the terminal is a second, separate
         permission (Accessibility, for the "System Events keystroke"
         AppleScript in TerminalFocuser.SendText) — TCC prompts for that one
         itself the first time it's needed, with no Info.plist key of its
         own, but it's tied to this bundle's code identity exactly like the
         Automation grant is, so the same "a rebuild can invalidate it"
         caveat above applies to it too. -->
    <!-- Claude Desktop's own schemes, declared here so Claude Buddy is
         *eligible* to handle them. Being eligible is not the same as being
         chosen: Claude Desktop declares them too, so the default is claimed
         explicitly at runtime (MacOSUrlScheme), and only once there is more
         than one profile to route between.

         Why claim them at all: LaunchServices resolves a scheme to a bundle
         id, and every tinted clone Claude Buddy makes is a byte-identical copy
         of Claude.app — same id, same claimed schemes. So the id cannot say
         which profile a link belongs to, and a LaunchServices launch carries no
         CLAUDE_USER_DATA_DIR, which means every sign-in callback lands in the
         Default profile no matter which profile started it. See
         ClaudeDesktopUrlRouting for the full story. The msauth scheme is the
         Microsoft sign-in callback, and is why this shows up as "I can't log
         in" rather than only as a stray window.

         No backticks anywhere in this block: the heredoc below is unquoted so
         that $APP_NAME and friends expand, which means backticks in a comment
         are run as a command substitution. One here cost a build. -->
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>Claude</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>claude</string>
                <string>msauth.com.anthropic.claudefordesktop</string>
            </array>
            <!-- Viewer, not Editor: these schemes are Anthropic's, and this app
                 forwards them rather than owning them. -->
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
        </dict>
    </array>
    <key>NSMicrophoneUsageDescription</key>
    <string>Claude Buddy uses the microphone to transcribe what you say, entirely on this machine, when you click the mic that appears on hovering an orb — only after you turn voice input on in Settings.</string>
</dict>
</plist>
PLIST

# Sign from the inside out, nested code first, bundle last. `codesign --deep`
# looks like it does this in one step but Apple explicitly does not support it
# for distribution: it applies the *bundle's* entitlements to every nested
# binary and seals in an order the notary service rejects. A self-contained
# publish drops ~16 dylibs plus the `createdump` helper next to the executable,
# and an unsigned nested Mach-O is a hard notarization failure, so each one has
# to be signed on its own.
if [[ -n "$SIGN_IDENTITY" ]]; then
  echo "==> Signing (Developer ID: $SIGN_IDENTITY)"
  SIGN_ARGS=(--force --timestamp --options runtime --sign "$SIGN_IDENTITY")
else
  echo "==> Signing (ad-hoc — not distributable, see --help)"
  # No --timestamp or --options runtime: a timestamp needs Apple's server and
  # the hardened runtime without a real identity just breaks the app locally.
  SIGN_ARGS=(--force --sign -)
fi

# Everything in Contents/MacOS, not just the obvious binaries. Contents/MacOS is
# the bundle's *executable* directory, so codesign treats every file in it as
# nested code and refuses to seal the bundle while any one of them is unsigned
# ("code object is not signed at all"). For a self-contained .NET publish that
# means all ~200 files: the dylibs, the extensionless `createdump` helper, the
# managed .dll assemblies (PE32, which sign as Format=generic), and even
# ClaudeBuddy.runtimeconfig.json — .NET's apphost requires those sit next to the
# executable, so they cannot be moved to Resources/ to sidestep this.
#
# The main executable is skipped: signing the bundle covers it, and it has to
# come last so the entitlements land on it.
#
# Resources/ is untouched by this loop on purpose. It is not an executable
# directory, so the hook scripts there are sealed as ordinary resources.
MAIN_EXE="$APP/Contents/MacOS/ClaudeBuddy"
while IFS= read -r nested; do
  [[ "$nested" == "$MAIN_EXE" ]] && continue
  codesign "${SIGN_ARGS[@]}" "$nested" >/dev/null 2>&1 ||
    { echo "    failed to sign $nested" >&2; exit 1; }
done < <(find "$APP/Contents/MacOS" -type f -print)

# The bundle last, and only here do the entitlements apply — they describe what
# the app as a whole is allowed to do (JIT, unsigned exec memory, Apple Events).
if [[ -n "$SIGN_IDENTITY" ]]; then
  codesign "${SIGN_ARGS[@]}" --entitlements "$ENTITLEMENTS" "$APP"
  echo "==> Verifying signature"
  codesign --verify --strict --deep --verbose=1 "$APP"
else
  codesign "${SIGN_ARGS[@]}" "$APP" 2>/dev/null
fi

rm -rf "$DIST/publish-$RID"

echo "==> Built $APP"
if [[ $INSTALL -eq 1 ]]; then
  echo "==> Installing to /Applications"
  rm -rf "/Applications/$APP_NAME.app"
  cp -R "$APP" "/Applications/"
  echo "==> Installed /Applications/$APP_NAME.app"
  echo "    Launch it with: open -a \"$APP_NAME\""
else
  echo "    Try it with:    open \"$APP\""
  echo "    Install it with: $0 --install"
fi
