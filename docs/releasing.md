# Releasing — maintainer notes

Certificate and secret handling for whoever cuts releases. **Nobody needs this
to contribute.** A fork, a PR, or a local build works unsigned; only the
repository's own release runs sign anything, and those read secrets that only
maintainers can set. Don't create Developer ID certificates "to help" — Apple
caps how many a team may hold at once, and every extra one is another key that
can leak.

## Cutting a release

`ClaudeBuddy.csproj`'s `<Version>` is the single source of truth. The packaging
scripts, the installer filenames, the `.app`'s `Info.plist` and the Add/Remove
Programs entry all read it, and `release.yml` refuses to publish when the tag
disagrees with it.

```bash
# 1. bump <Version> in ClaudeBuddy.csproj
# 2. write .github/release-notes/v<version>.md
# 3. commit, then:
git tag v0.2.0-beta && git push origin v0.2.0-beta
```

That builds both DMGs and the Windows setup, signs and notarizes the macOS
ones, writes `SHA256SUMS.txt`, and publishes with those notes. A tag containing
a hyphen is marked prerelease automatically.

`gh workflow run release.yml` runs the identical build and publishes nothing —
use it to test packaging changes.

## One-time signing setup

On a Mac, signed in to a paid Apple Developer account:

```bash
./tools/setup-macos-signing.sh
```

It generates the key and CSR, hands you the Apple page to upload it to, turns
the issued certificate into a `.p12`, imports it locally so your own builds sign
too, and sets all six repository secrets with `gh`. `--secrets` re-pushes just
the secrets, e.g. after rotating the app-specific password.

Two steps need a browser and the script pauses at each:

- **Create the certificate** at
  [developer.apple.com](https://developer.apple.com/account/resources/certificates/add),
  type **Developer ID Application**. Only the Account Holder or Admin role can
  see that option — a Developer-role member finds it simply absent rather than
  being told why.
- **Create an [app-specific password](https://appleid.apple.com)** for
  notarization. The real Apple ID password is rejected outright.

The one non-obvious thing the script does is bundle Apple's Developer ID G2
intermediate CA into the `.p12`. Leave it out and local signing still works
(your Mac already trusts that CA via the Command Line Tools) while CI fails with
`unable to build chain to self-signed root`, because a fresh runner keychain has
never seen it.

### Secrets

Set by the script; listed here for rotating by hand. With none of them present
the workflow still succeeds but produces ad-hoc signed DMGs that Gatekeeper
rejects on other Macs — the difference between a testable artifact and a
shippable one.

Signing always needs these three:

| Secret | What it is |
| --- | --- |
| `MACOS_CERTIFICATE_P12_BASE64` | Developer ID Application cert + key + intermediate, as `.p12`, base64-encoded |
| `MACOS_CERTIFICATE_PASSWORD` | the export password for that `.p12` |
| `MACOS_SIGNING_IDENTITY` | full identity name, e.g. `Developer ID Application: Name (AB12CD34EF)` |

Notarization is separate, and takes either credential set. `build-macos-dmg.sh`
checks for the API key first and falls back to the Apple ID, so whichever set is
populated is the one used.

**App Store Connect API key — preferred.** It belongs to the team rather than to
one person's Apple ID, so it survives that person rotating their password or
leaving, and it can be revoked on its own. Generate it under
[Users and Access → Integrations](https://appstoreconnect.apple.com/access/integrations/api),
Team Keys, with at least the **Developer** role — notarization refuses anything
less, and only the Account Holder can create team keys. The `.p8` downloads
once.

| Secret | What it is |
| --- | --- |
| `MACOS_NOTARY_KEY_P8_BASE64` | the `AuthKey_*.p8` private key, base64-encoded |
| `MACOS_NOTARY_KEY_ID` | the key's 10-character Key ID (also in the filename) |
| `MACOS_NOTARY_ISSUER_ID` | issuer UUID — **Team keys only**; leave unset for an Individual key, which `notarytool` rejects `--issuer` for |

**Apple ID and app-specific password — fallback.** Tied to one person's account.

| Secret | What it is |
| --- | --- |
| `MACOS_NOTARY_APPLE_ID` | Apple ID email used for notarization |
| `MACOS_NOTARY_PASSWORD` | [app-specific password](https://appleid.apple.com) — *not* the Apple ID password |
| `MACOS_NOTARY_TEAM_ID` | 10-character team ID |

Windows installers ship unsigned; SmartScreen shows a "More info → Run anyway"
warning. If a code signing certificate is ever bought, setting
`WINDOWS_CERT_THUMBPRINT` (with the certificate in the runner's store) makes
`build-windows-installer.ps1` sign both the executable and the installer with
`signtool` — the code is already there and inert.

## Why notarization matters

Without a notarization ticket, a downloaded DMG doesn't produce a security
prompt the user can click through — macOS reports the app as **damaged**, which
reads as a corrupt download and generates bug reports rather than questions.

Notarizing requires the hardened runtime, and the hardened runtime disables
several things a self-contained .NET app depends on. `tools/ClaudeBuddy.entitlements`
re-enables exactly what's needed: JIT and unsigned executable memory for
CoreCLR, library validation off for the ~16 bundled native libraries, and Apple
Events for click-to-focus and Claude Desktop quit. Removing any of them still
notarizes successfully and then fails at runtime — either a crash at startup or
click-to-focus silently returning `errAEEventNotPermitted` — so a signed build
has to actually be run before shipping.

Related: `build-macos-app.sh` signs inside-out, every file under
`Contents/MacOS` individually before sealing the bundle, rather than using
`codesign --deep`. `--deep` looks equivalent but applies the bundle's
entitlements to nested binaries and seals in an order the notary service
rejects. Contents/MacOS is the bundle's executable directory, so *every* file in
it counts as nested code — including the managed `.dll` assemblies and even
`ClaudeBuddy.runtimeconfig.json`, which .NET's apphost requires sit next to the
executable and so cannot be moved out of the way.
