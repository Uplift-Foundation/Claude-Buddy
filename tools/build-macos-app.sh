#!/usr/bin/env bash
# Builds "Claude Buddy.app" — a real macOS app bundle you can double-click,
# drop in /Applications, and add to Login Items.
#
#   ./tools/build-macos-app.sh              # build into dist/
#   ./tools/build-macos-app.sh --install    # ...and copy to /Applications
#   ./tools/build-macos-app.sh --rid osx-x64
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
BUNDLE_ID="io.github.wtvamp.claudebuddy"
VERSION="1.0.0"
DIST="dist"
INSTALL=0

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
    <key>CFBundleVersion</key>           <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>    <string>11.0</string>
    <key>NSHighResolutionCapable</key>   <true/>
    <!-- Menu-bar-only: no Dock icon, no Cmd-Tab entry. -->
    <key>LSUIElement</key>               <true/>
    <!-- Shown in the Automation prompt the first time an orb is clicked. -->
    <key>NSAppleEventsUsageDescription</key>
    <string>Claude Buddy uses automation to bring the terminal window of a Claude Code session to the front when you click its orb.</string>
</dict>
</plist>
PLIST

echo "==> Signing (ad-hoc)"
# Ad-hoc is enough for running locally and gives the bundle a code identity
# that macOS can hang the Automation grant on. Note that every rebuild
# changes the signature, so macOS may ask for Automation permission again.
codesign --force --deep --sign - "$APP" 2>/dev/null

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
