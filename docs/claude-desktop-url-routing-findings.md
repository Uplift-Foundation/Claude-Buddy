# Claude Desktop URL routing — findings

CB-4. What was measured on a real machine, and what is still assumed.

The reported symptoms were four: profiles "sometimes open in the wrong
profile", "sometimes I can't login", "sometimes it makes me log in multiple
times", and Dock icon tinting that "seems to have stopped working". Three of
those are one bug; the fourth is a separate one in the same file.

## Confirmed on a real machine (macOS 27.0, Aug 2026)

### The schemes collide, and the collision is not a tie macOS can break

Claude Desktop's `Info.plist` declares two URL schemes:

```
$ plutil -extract CFBundleURLTypes json -o - /Applications/Claude.app/Contents/Info.plist
[{"CFBundleURLName":"Claude","CFBundleURLSchemes":["claude"]},
 {"CFBundleURLName":"MSAL","CFBundleURLSchemes":["msauth.com.anthropic.claudefordesktop"]}]
```

Every tinted clone is a byte-identical copy of that bundle — deliberately, so
the CDHash matches and the "Claude Safe Storage" keychain ACL keeps working —
so every clone declares the same two schemes under the same bundle id.
LaunchServices records a scheme handler by bundle id alone:

```
handlerpref id: claude
URL scheme:     claude
all roles:      com.anthropic.claudefordesktop
```

and three live bundles answered to it:

```
claude:// -> /Applications/Claude.app
    candidate: /Applications/Claude.app
    candidate: ~/Library/Application Support/ClaudeBuddy/bundles/Claude-Board/Claude.app
    candidate: ~/Library/Application Support/ClaudeBuddy/bundles/Claude/Claude.app
```

A fourth, `bundles/Claude-Profile-1/Claude.app`, was still registered and
claiming `claude:` months after its directory was deleted — `Remove()` deleted
the folder and never unregistered the bundle.

### The failure is deterministic, not intermittent

With only the Board profile running, sending a `claude://` link:

```
before:  75247  .../bundles/Claude-Board/Claude.app/Contents/MacOS/Claude
after:     973  /Applications/Claude.app/Contents/MacOS/Claude   <- new process
         75247  .../bundles/Claude-Board/...                     <- never received it
```

`ps eww 973` contained no `CLAUDE_USER_DATA_DIR` at all, so pid 973 resolved
the default userData directory: it opened **Default**. Only Claude Buddy's own
`open --env` ever sets that variable, so *any* LaunchServices-initiated launch
lands on Default regardless of which profile the link was meant for.

That accounts for three symptoms at once. Signing in to a non-Default profile
sends the callback to Default, so a Default window appears (wrong profile), the
profile being signed into never receives its token (can't log in), and the user
retries (log in multiple times). It looks intermittent only because signing in
to Default itself works correctly.

### Delivery by bundle path works, and reuses the running instance

The fix needs a way to address one instance rather than one bundle id. Both of
these delivered to the already-running Board instance and started nothing new:

```
NSWorkspace.open(_:withApplicationAt:configuration:)  -> delivered to pid 75247
/usr/bin/open -a <clone path> "claude://…"            -> no new process
```

The shipped code uses the second, because it is the same `/usr/bin/open` the
launcher already goes through, and needs no interop.

**Deliberately without `-n`.** `-n` is correct when *launching* a profile,
where the caller has just proved from a fresh scan that nothing is running on
that directory. For delivery the opposite is wanted: a second Chromium on a
live userData directory is the leveldb/SQLite corruption the whole profile
feature exists to avoid, and corrupt `Cookies` loses the `sessionKey` — another
route to "log in again".

### The fix, verified end to end

After installing the signed build and relaunching:

```
claude:// -> /Applications/Claude Buddy.app
msauth   -> /Applications/Claude Buddy.app
```

and the same probe that previously spawned a Default instance:

```
before:  75247  .../bundles/Claude-Board/...
after:   75247  .../bundles/Claude-Board/...      <- nothing else started
```

The link reached the profile it belonged to. This is the same probe, on the
same machine, that produced the "after: 973 /Applications/Claude.app" result
above.

### The tinting bug is a separate, provable one

`ApplyTintedIcon` wrote its `icon-colour` marker *before* calling
`MacOSCustomIcon.Set`. A refused write — App Management, whose grant is tied to
Claude Buddy's code identity and is invalidated by an ad-hoc rebuild — was
therefore recorded as a success, and `Ensure()`'s `ColourMatches` check then
treated the clone as correctly coloured permanently.

Caught in exactly that state on disk:

```
bundles/Claude-Board/icon-colour     written 2026-08-24 10:21
bundles/Claude-Board/Claude.app/     contains only Contents/ — no "Icon\r"
xattr -l  .../Claude.app             com.apple.macl, com.apple.provenance
                                     — no com.apple.FinderInfo
```

`IconApplied` recorded the refusal and was never read anywhere in the codebase,
so nothing surfaced it.

## Assumed, not confirmed

- **That the re-tint now succeeds on the next launch.** `ColourMatches` now
  also requires the `Icon\r` file, so a clone in the broken state above is
  rebuilt rather than skipped — that much is covered by unit tests. Whether the
  icon then *applies* depends on macOS granting App Management to the newly
  signed build, which cannot be forced from here. If it is still refused, the
  profile's row now says `allow App Management for colours` instead of failing
  silently, which is the part that was missing.
- **Windows.** Windows has no clone bundles, so the id collision cannot happen
  there; but a `claude://` link on Windows still resolves through the registry
  to Claude Desktop with no `--user-data-dir`, which should land on Default the
  same way. Not reproduced — no Windows machine was available for this work —
  and not fixed. The routing row in Settings is macOS-only for that reason,
  rather than because Windows was checked and found healthy.
- **Which profile a link belongs to when several are running and none has been
  frontmost.** The router prefers the last Claude Desktop instance the user was
  in, then a lone running instance, then Default, then the lowest pid. Only the
  first two were exercised on a real machine; the tie-breaks are unit-tested but
  not observed in the wild.
- **Restoring the scheme on uninstall.** `Restore()` hands `claude:` back to the
  recorded previous handler and is wired to the Settings toggle, which was
  exercised only through its tests. Nothing runs it on uninstall, because
  nothing in this repo runs anything on uninstall today.

## Notes for whoever touches this next

- `tools/build-macos-app.sh` writes `Info.plist` from an **unquoted** heredoc,
  so `$VAR` expands — and so do backticks. A backtick inside an XML comment in
  that block is run as a command substitution. This cost one build here; there
  is now a comment in the file saying so.
- `lsregister` is not API and has no supported equivalent — public
  LaunchServices can register a bundle but has never been able to remove one.
  `Unregister()` is best-effort by design.
