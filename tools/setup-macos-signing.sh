#!/usr/bin/env bash
# Walks you through Developer ID signing setup and does every part that can be
# automated. Run it once; after that, releases sign themselves in CI.
#
#   ./tools/setup-macos-signing.sh            # full walkthrough
#   ./tools/setup-macos-signing.sh --secrets  # only push secrets to GitHub
#
# What it automates:
#   * generating the private key and CSR (no Keychain Access "Request a
#     Certificate From a Certificate Authority" menu-diving)
#   * bundling the issued certificate, its private key and Apple's intermediate
#     CA into a .p12
#   * base64-encoding it and setting all six repository secrets with `gh`
#   * verifying the result actually signs
#
# What it can't: Apple's website needs a human with a browser. The script stops
# and tells you exactly which page to open and what to click.
#
# Everything is written to a gitignored scratch directory, and the private key
# never leaves your machine except as an encrypted .p12 in a GitHub secret.

set -euo pipefail

cd "$(dirname "$0")/.."

WORK="dist/signing"
SECRETS_ONLY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --secrets) SECRETS_ONLY=1; shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
ask()  { printf '\033[1;33m??\033[0m %s' "$*"; }

# This script is a conversation — it asks for a name, waits while you use a
# browser, then asks for a password. Without a terminal on stdin every `read`
# gets EOF instantly and it would appear to do nothing at all, which is a
# baffling way to fail. Say so instead.
if [[ ! -t 0 ]]; then
  cat >&2 <<'NO_TTY'
This script needs an interactive terminal: it prompts for a certificate name,
waits while you upload a CSR in your browser, and then asks for an app-specific
password. Run it directly in Terminal or iTerm:

    cd "$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
    ./tools/setup-macos-signing.sh

Nothing has been changed.
NO_TTY
  exit 1
fi

mkdir -p "$WORK"
chmod 700 "$WORK"

KEY="$WORK/developerID.key"
CSR="$WORK/developerID.csr"
CER="$WORK/developerID_application.cer"
INTERMEDIATE="$WORK/DeveloperIDG2CA.cer"
P12="$WORK/developerID.p12"

if [[ $SECRETS_ONLY -eq 0 ]]; then

  step "Step 1 of 6 — prerequisites"
  cat <<'PREREQ'
You need:
  * A paid Apple Developer Program membership ($99/yr). A free Apple ID cannot
    issue Developer ID certificates.
  * The Account Holder or Admin role on that team. Only those roles may create
    a "Developer ID Application" certificate — if you are a Developer-role
    member the option simply will not appear, which is confusing rather than
    explanatory.

Note: Apple caps how many Developer ID Application certificates a team may
have at once. If you have hit the cap, revoke an unused one before continuing.
PREREQ
  ask "Ready? [y/N] "; read -r reply
  [[ "$reply" =~ ^[Yy]$ ]] || { echo "Nothing has been changed."; exit 0; }

  step "Step 2 of 6 — generating your private key and CSR"
  if [[ -f "$KEY" ]]; then
    echo "Reusing the existing key at $KEY"
  else
    # 2048-bit RSA is what Apple requires for Developer ID.
    openssl genrsa -out "$KEY" 2048 2>/dev/null
    chmod 600 "$KEY"
    echo "Wrote $KEY  (private — never commit or share this)"
  fi

  ask "Your name or company, as it should appear in the certificate: "
  read -r COMMON_NAME
  [[ -n "$COMMON_NAME" ]] || { echo "A name is required." >&2; exit 1; }
  ask "Your Apple ID email: "
  read -r APPLE_ID
  [[ -n "$APPLE_ID" ]] || { echo "An email is required." >&2; exit 1; }

  openssl req -new -key "$KEY" -out "$CSR" \
    -subj "/emailAddress=$APPLE_ID/CN=$COMMON_NAME/C=US" 2>/dev/null
  echo "Wrote $CSR"

  step "Step 3 of 6 — upload the CSR to Apple (browser required)"
  cat <<UPLOAD
  1. Open  https://developer.apple.com/account/resources/certificates/add
  2. Choose  Software > "Developer ID Application"
     (not "Developer ID Installer" — that one signs .pkg files, which this
      project does not produce.)
  3. If asked which profile type, pick the G2 Sub-CA option.
  4. Upload this file:

       $(pwd)/$CSR

  5. Download the issued certificate and save it as:

       $(pwd)/$CER

UPLOAD
  # The macOS "open" command is right here and saves a copy-paste.
  ask "Open that page in your browser now? [y/N] "; read -r reply
  [[ "$reply" =~ ^[Yy]$ ]] && open "https://developer.apple.com/account/resources/certificates/add"

  echo
  ask "Press Return once $CER exists. "; read -r _

  if [[ ! -f "$CER" ]]; then
    echo "Still no file at $CER — rerun this script when you have it." >&2
    exit 1
  fi

  step "Step 4 of 6 — building the .p12"
  # Apple's intermediate has to travel with the leaf. Without it, codesign on a
  # fresh CI runner fails with "unable to build chain to self-signed root",
  # because a GitHub runner's keychain has no reason to already trust the
  # Developer ID G2 CA the way your Mac (with Xcode or CLT) does.
  if [[ ! -f "$INTERMEDIATE" ]]; then
    echo "Fetching Apple's Developer ID G2 intermediate CA"
    curl -fsSL -o "$INTERMEDIATE" https://www.apple.com/certificateauthority/DeveloperIDG2CA.cer
  fi

  # Apple hands back DER; openssl's pkcs12 builder wants PEM.
  openssl x509 -inform DER -in "$CER" -out "$WORK/leaf.pem" 2>/dev/null
  openssl x509 -inform DER -in "$INTERMEDIATE" -out "$WORK/intermediate.pem" 2>/dev/null

  IDENTITY="$(openssl x509 -in "$WORK/leaf.pem" -noout -subject \
              | sed -n 's/.*CN *= *\([^,/]*\).*/\1/p' | sed 's/ *$//')"
  echo "Certificate identity: $IDENTITY"

  # A random password rather than a prompt: it only has to survive being pasted
  # into a GitHub secret, and a strong random one removes any temptation to
  # reuse something memorable.
  P12_PASSWORD="$(openssl rand -base64 24)"

  openssl pkcs12 -export \
    -inkey "$KEY" \
    -in "$WORK/leaf.pem" \
    -certfile "$WORK/intermediate.pem" \
    -out "$P12" \
    -passout "pass:$P12_PASSWORD" \
    -legacy 2>/dev/null ||
  # -legacy exists only in OpenSSL 3; LibreSSL (what stock macOS ships as
  # /usr/bin/openssl) rejects the flag outright. Retry without it.
  openssl pkcs12 -export \
    -inkey "$KEY" \
    -in "$WORK/leaf.pem" \
    -certfile "$WORK/intermediate.pem" \
    -out "$P12" \
    -passout "pass:$P12_PASSWORD"

  chmod 600 "$P12"
  echo "Wrote $P12"

  step "Step 5 of 6 — verifying it signs locally"
  # Import into your login keychain so local builds can sign too, and so a
  # broken certificate is caught here rather than in CI.
  security import "$P12" -k ~/Library/Keychains/login.keychain-db \
    -P "$P12_PASSWORD" -T /usr/bin/codesign 2>/dev/null ||
    echo "  (already imported, or import declined — continuing)"

  if security find-identity -v -p codesigning | grep -q "$IDENTITY"; then
    echo "Found a valid codesigning identity for $IDENTITY"
  else
    echo "WARNING: no valid codesigning identity found for $IDENTITY." >&2
    echo "The certificate may not have imported. Check Keychain Access." >&2
  fi

  # Stash what step 6 needs, so --secrets can be rerun without redoing any of
  # the above. 600 because it holds the .p12 password.
  #
  # Every value is single-quoted, which is not optional: a Developer ID identity
  # looks like "Developer ID Application: Name (TEAMID)", and sourcing that
  # unquoted is a bash syntax error on the parenthesis. The random .p12 password
  # is base64 and can contain "+" and "/" for the same reason.
  shell_quote() {
    printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"
  }

  {
    printf 'MACOS_SIGNING_IDENTITY=%s\n'     "$(shell_quote "$IDENTITY")"
    printf 'MACOS_CERTIFICATE_PASSWORD=%s\n' "$(shell_quote "$P12_PASSWORD")"
    printf 'MACOS_NOTARY_APPLE_ID=%s\n'      "$(shell_quote "$APPLE_ID")"
  } > "$WORK/values.env"
  chmod 600 "$WORK/values.env"
fi

step "Step 6 of 6 — pushing secrets to GitHub"

[[ -f "$WORK/values.env" ]] || { echo "Run without --secrets first." >&2; exit 1; }
[[ -f "$P12" ]] || { echo "Missing $P12 — run without --secrets first." >&2; exit 1; }
# shellcheck disable=SC1090
source "$WORK/values.env"

command -v gh >/dev/null || { echo "The GitHub CLI (gh) is required." >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Run 'gh auth login' first." >&2; exit 1; }

cat <<'NOTARY'
Notarization needs credentials of its own — signing and notarizing are separate
things to Apple. There are two kinds, and the first is the better one for CI:

  1. An App Store Connect API key (recommended). It belongs to the team, not to
     one person's Apple ID, so it survives that person changing their password
     or leaving, and it can be revoked on its own without touching anything
     else. This is the current path for automated builds.

  2. An Apple ID plus an app-specific password. Older, still supported. Tied to
     an individual account.

NOTARY

ask "Use an API key? [Y/n] "; read -r use_key
if [[ "$use_key" =~ ^[Nn]$ ]]; then
  NOTARY_METHOD=appleid
else
  NOTARY_METHOD=apikey
fi

if [[ "$NOTARY_METHOD" == apikey ]]; then
  cat <<'APIKEY'

Create the key:
  1. Open  https://appstoreconnect.apple.com/access/integrations/api
  2. Team Keys tab > "+" to generate a key. Give it a name and at least the
     "Developer" role — notarization is refused for anything less.
  3. Download the AuthKey_XXXXXXXXXX.p8. Apple lets you download it ONCE.
  4. Note the Key ID (next to the key) and the Issuer ID (above the list).

Only the Account Holder can generate team keys. If the "+" is greyed out,
that is why.

APIKEY
  ask "Open that page now? [y/N] "; read -r reply
  [[ "$reply" =~ ^[Yy]$ ]] && open "https://appstoreconnect.apple.com/access/integrations/api"

  echo
  ask "Path to the downloaded .p8 file: "; read -r P8_PATH
  # Tolerate a path pasted with surrounding quotes, and expand a leading ~,
  # since both are what actually happens when dragging a file into a terminal.
  P8_PATH="${P8_PATH%\"}"; P8_PATH="${P8_PATH#\"}"
  P8_PATH="${P8_PATH%\'}"; P8_PATH="${P8_PATH#\'}"
  P8_PATH="${P8_PATH/#\~/$HOME}"
  [[ -f "$P8_PATH" ]] || { echo "No file at $P8_PATH" >&2; exit 1; }
  grep -q 'BEGIN PRIVATE KEY' "$P8_PATH" || {
    echo "$P8_PATH does not look like a .p8 private key." >&2; exit 1; }

  # Apple names the download AuthKey_<KEYID>.p8, so offer that as the default
  # rather than making someone re-read it off the website.
  GUESSED_KEY_ID="$(basename "$P8_PATH" | sed -n 's/^AuthKey_\(.*\)\.p8$/\1/p')"
  if [[ -n "$GUESSED_KEY_ID" ]]; then
    ask "Key ID [$GUESSED_KEY_ID]: "; read -r KEY_ID
    KEY_ID="${KEY_ID:-$GUESSED_KEY_ID}"
  else
    ask "Key ID: "; read -r KEY_ID
  fi
  [[ -n "$KEY_ID" ]] || { echo "Required." >&2; exit 1; }

  # Required for a Team key, and notarytool rejects it outright for an
  # Individual key, so an empty answer is a legitimate one.
  echo
  echo "The Issuer ID applies to Team keys (the usual case for an organization)."
  echo "Leave it blank if this is an Individual key."
  ask "Issuer ID [blank for Individual]: "; read -r ISSUER_ID
else
  cat <<'APPLEID'

Create an app-specific password at https://appleid.apple.com under
Sign-In and Security > App-Specific Passwords. Notarization rejects your real
account password outright. Your Team ID is shown at
https://developer.apple.com/account under Membership details.

APPLEID
  ask "App-specific password: "; read -rs NOTARY_PASSWORD; echo
  [[ -n "$NOTARY_PASSWORD" ]] || { echo "Required." >&2; exit 1; }
  ask "Team ID: "; read -r TEAM_ID
  [[ -n "$TEAM_ID" ]] || { echo "Required." >&2; exit 1; }
fi

# tr -d '\n' so the secret is one long line. base64 --decode in CI copes with
# wrapped input either way, but a single line keeps the secret free of embedded
# newlines that other tooling might trim differently.
base64 -i "$P12" | tr -d '\n' | gh secret set MACOS_CERTIFICATE_P12_BASE64
printf '%s' "$MACOS_CERTIFICATE_PASSWORD" | gh secret set MACOS_CERTIFICATE_PASSWORD
printf '%s' "$MACOS_SIGNING_IDENTITY"     | gh secret set MACOS_SIGNING_IDENTITY

if [[ "$NOTARY_METHOD" == apikey ]]; then
  base64 -i "$P8_PATH" | tr -d '\n' | gh secret set MACOS_NOTARY_KEY_P8_BASE64
  printf '%s' "$KEY_ID" | gh secret set MACOS_NOTARY_KEY_ID
  if [[ -n "$ISSUER_ID" ]]; then
    printf '%s' "$ISSUER_ID" | gh secret set MACOS_NOTARY_ISSUER_ID
  else
    # An Individual key must not send --issuer, and the build script decides that
    # by whether the secret is set, so a leftover value would break it.
    gh secret delete MACOS_NOTARY_ISSUER_ID >/dev/null 2>&1 || true
  fi
  # Clear any stale Apple ID credentials, so the fallback can't quietly take
  # over later with a password that has since been revoked.
  for stale in MACOS_NOTARY_APPLE_ID MACOS_NOTARY_PASSWORD MACOS_NOTARY_TEAM_ID; do
    gh secret delete "$stale" >/dev/null 2>&1 || true
  done
else
  printf '%s' "$MACOS_NOTARY_APPLE_ID" | gh secret set MACOS_NOTARY_APPLE_ID
  printf '%s' "$NOTARY_PASSWORD"       | gh secret set MACOS_NOTARY_PASSWORD
  printf '%s' "$TEAM_ID"               | gh secret set MACOS_NOTARY_TEAM_ID
fi

step "Done"
gh secret list

cat <<DONE

Signing identity: $MACOS_SIGNING_IDENTITY

Local signed builds:
  MACOS_SIGNING_IDENTITY="$MACOS_SIGNING_IDENTITY" ./tools/build-macos-dmg.sh --skip-notarize

A full signed + notarized run, without publishing anything:
  gh workflow run release.yml

$WORK holds your private key and .p12. It is inside the gitignored dist/
directory, so it will not be committed — but back it up somewhere safe and
keep it off shared machines. Losing it just means generating a new certificate;
leaking it means someone else can sign software as you.
DONE
