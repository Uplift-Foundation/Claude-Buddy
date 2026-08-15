#!/usr/bin/env bash
# Builds the optional side-car speech engine for macOS and packs it for release.
#
#   ./tools/build-speech-engine.sh                  # dist/ClaudeBuddySpeech-<version>-<rid>.zip
#   ./tools/build-speech-engine.sh --install        # ...and drop it where the app looks,
#                                                   #    for testing before a release exists
#   ./tools/build-speech-engine.sh --rid osx-x64    # build for Intel Macs
#
# The macOS twin of tools/build-speech-engine.ps1, which stays Windows-only. A
# second script rather than one cross-platform one, for the same reason the rest
# of the packaging splits that way: macOS is served by build-macos-app.sh and
# build-macos-dmg.sh, Windows by the .ps1 pair. The PowerShell version also bakes
# in `bin\Release\...` separators and %APPDATA%, and pwsh is not a given on a
# developer's Mac, so making it portable would mean rewriting it and requiring a
# shell nobody here has.
#
# THIS MUST RUN ON A MAC. Not a preference — the .NET SDK ad-hoc signs the
# apphost with `codesign` when it publishes, which it can only do on macOS, and
# Apple Silicon refuses to exec an arm64 binary with no signature at all. An
# engine cross-published from the Windows job would download, extract, and then
# be killed on launch. That is why the release workflow builds this in the macOS
# job rather than beside the Windows installer.
#
# Why the app downloads this instead of shipping it: see NeuralSpeech, and
# tools/ClaudeBuddySpeech/ClaudeBuddySpeech.csproj for why the engine is a
# separate process rather than a dependency.

set -euo pipefail

cd "$(dirname "$0")/.."

# Captured once, because the zip below is written from inside the publish
# directory and a relative path would have to count six levels back up.
PWD_ABS="$PWD"

INSTALL=0

# Default to this Mac's architecture; override for cross-building. Same shape as
# build-macos-app.sh so the two agree about what a RID is.
case "$(uname -m)" in
  arm64) RID="osx-arm64" ;;
  *)     RID="osx-x64" ;;
esac

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) INSTALL=1; shift ;;
    --rid)     RID="$2"; shift 2 ;;
    -h|--help) sed -n '2,26p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

# Read from the *app's* csproj, not the engine's. ClaudeBuddy.csproj's <Version>
# is the single source of truth for the shipped version — the installer scripts
# and the release workflow all parse that same element — and the engine ships in
# the app's own release under the same tag. NeuralSpeech derives the version it
# asks for from the app assembly, so taking it from anywhere else here is how the
# filename and the URL drift apart.
VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' ClaudeBuddy.csproj | head -1)"
[[ -n "$VERSION" ]] || { echo "Could not read <Version> from ClaudeBuddy.csproj" >&2; exit 1; }

PROJECT="tools/ClaudeBuddySpeech/ClaudeBuddySpeech.csproj"
BUILD="tools/ClaudeBuddySpeech/bin/Release/net10.0/$RID"
PUBLISH="$BUILD/publish"
DIST="dist"
SIGN_IDENTITY="${MACOS_SIGNING_IDENTITY:-}"
ENGINE_ENTITLEMENTS="tools/ClaudeBuddySpeech.entitlements"

echo "==> Speech engine $VERSION ($RID)"

echo "==> Publishing"
dotnet publish "$PROJECT" -c Release -r "$RID" -p:DebugType=none --nologo -v quiet

[[ -d "$PUBLISH" ]] || { echo "Publish output not found at $PUBLISH" >&2; exit 1; }

# The voices are the whole reason this script exists rather than a bare
# `dotnet publish`. KokoroSharp ships its 54 voice files by way of an MSBuild
# Copy that runs AfterTargets="Build" and produces no items, so they land in
# bin/ and `dotnet publish` never carries them anywhere. Measured on macOS as
# well as Windows: a published engine has no voices/ directory at all, and would
# exit "no voices directory" forever while every `dotnet run` looked perfect.
# Copied explicitly here so what ships is what was tested.
VOICES_SOURCE="$BUILD/voices"
[[ -d "$VOICES_SOURCE" ]] || {
  echo "No voices at $VOICES_SOURCE — the KokoroSharp copy target did not run" >&2; exit 1; }

VOICES_TARGET="$PUBLISH/voices"
# Removed first, and the .npy files copied by name rather than the directory by
# name. `cp -R src dst` when dst already exists nests it as dst/src, and a
# repeated build then loads every voice twice — observed while testing this, as
# `--list-voices` quietly reporting 44 English voices instead of 20.
rm -rf "$VOICES_TARGET"
mkdir -p "$VOICES_TARGET"
cp "$VOICES_SOURCE"/*.npy "$VOICES_TARGET/"

VOICE_COUNT="$(find "$VOICES_TARGET" -name '*.npy' | wc -l | tr -d ' ')"
echo "==> Bundled $VOICE_COUNT voices"
[[ "$VOICE_COUNT" -gt 0 ]] || { echo "No .npy voices were copied" >&2; exit 1; }

# Top-level *.npy only, which deliberately leaves the voices-zh/ subdirectory
# behind: this engine lists American English voices and the Chinese set is
# another ~25MB of files nothing here can select.

# Apache-2.0, unlike this repo's MIT: carried alongside so the attribution
# travels with the bytes it applies to.
[[ -f "$VOICES_SOURCE/LICENSE" ]] && cp "$VOICES_SOURCE/LICENSE" "$VOICES_TARGET/LICENSE"

# Signed only when an identity is supplied. Without one the binaries keep the
# signatures they already have — the SDK ad-hoc signs the apphost and the
# runtime and ONNX dylibs arrive signed from their packages — which is enough to
# run, because the app downloads and extracts this itself and nothing in that
# path attaches a quarantine attribute for Gatekeeper to act on. Re-signing them
# ad-hoc here would churn bytes for no gain.
if [[ -n "$SIGN_IDENTITY" ]]; then
  echo "==> Signing (Developer ID: $SIGN_IDENTITY)"
  # Inside-out, nested code first, for the same reason build-macos-app.sh does
  # it that way: --deep is not supported for distribution.
  while IFS= read -r nested; do
    [[ "$nested" == "$PUBLISH/ClaudeBuddySpeech" ]] && continue
    codesign --force --timestamp --options runtime --sign "$SIGN_IDENTITY" "$nested" >/dev/null 2>&1 ||
      { echo "    failed to sign $nested" >&2; exit 1; }
  done < <(find "$PUBLISH" -type f \( -name '*.dylib' -o -name 'createdump' -o -name '*.a' \) -print)

  # The executable last, and with entitlements, which is the whole reason this
  # branch is more than one line. --options runtime turns off the JIT and the
  # library loading a self-contained .NET process depends on, so signing the
  # hardened runtime onto it without these produces an engine that unpacks
  # perfectly and then dies the moment it is asked to speak. Its own file rather
  # than the app's: the engine needs the CoreCLR keys but neither the Apple
  # Events nor the microphone one.
  codesign --force --timestamp --options runtime \
    --entitlements "$ENGINE_ENTITLEMENTS" \
    --sign "$SIGN_IDENTITY" "$PUBLISH/ClaudeBuddySpeech"
  codesign --verify --strict --verbose=1 "$PUBLISH/ClaudeBuddySpeech"
fi

mkdir -p "$DIST"
ZIP="$DIST/ClaudeBuddySpeech-$VERSION-$RID.zip"

echo "==> Packing"
rm -f "$ZIP"
# Zipped from inside the publish directory so the archive has no wrapping
# folder: NeuralSpeech extracts straight into its versioned directory and looks
# for ./ClaudeBuddySpeech there, not ./publish/ClaudeBuddySpeech.
#
# `zip` rather than `ditto`, which would add the __MACOSX resource-fork entries
# that .NET's ZipFile.ExtractToDirectory then materialises as junk files beside
# the binary. There are no symlinks in a self-contained publish (checked), so
# nothing here needs ditto's handling of them.
( cd "$PUBLISH" && zip -q -r -X "$PWD_ABS/$ZIP" . )

MB="$(echo "scale=1; $(stat -f%z "$ZIP") / 1048576" | bc)"
echo "==> Built $ZIP (${MB} MB)"

if [[ $INSTALL -eq 1 ]]; then
  # Straight into the layout NeuralSpeech expects, so the feature can be tested
  # end to end before any release exists to download from. Settings do not
  # follow HOME on macOS — SpecialFolder.ApplicationData resolves through the
  # OS — so this is the real location the running app will read.
  TARGET="$HOME/Library/Application Support/ClaudeBuddy/speech-engine/$VERSION"
  echo "==> Installing to $TARGET"
  rm -rf "$TARGET"
  mkdir -p "$TARGET"
  cp -R "$PUBLISH"/. "$TARGET/"
  chmod +x "$TARGET/ClaudeBuddySpeech"
  echo "==> Installed. The model downloads separately on first enable."
fi
