# Implementation brief: port the Claude Desktop profile switcher to Windows

For a Claude Code instance running **unattended on the Windows machine**, inside
the logged-on interactive desktop session, started by a scheduled task with `/IT`.
Same situation as `docs/windows-verification.md` — read that file's "Your
situation" and "Ground rules" sections first; they apply here unchanged.

You are implementing a feature, not verifying one. The mechanism is already
proven; what remains is the code.

## Why this is possible (all of it measured on this machine)

The macOS switcher launches Claude Desktop once per profile with
`CLAUDE_USER_DATA_DIR` pointed at a different directory. On Windows that variable
is unreachable — MSIX activation doesn't inherit the caller's environment, and the
registry route doesn't reach the activation broker. That is where an earlier
attempt stopped, wrongly concluding the feature couldn't port.

Two facts change it:

1. **The app honors `--user-data-dir=<path>`** as a plain Chromium command-line
   argument, no environment variable involved. Verified: launching with the flag
   alone produced a complete 27-entry Electron userData tree in the target
   directory.
2. **A packaged app can be handed a command line, unelevated**, via
   `IApplicationActivationManager::ActivateApplication`. Verified end to end from
   the interactive session: `ACTIVATED pid=12604`, 27 entries created, and the
   already-running packaged instance was unaffected — so the single-instance lock
   is per profile directory, as on macOS.

The trap that hid this: `ActivateApplication` is a shell API and returns
`E_ACCESSDENIED` from a non-interactive logon (an SSH session). It works from the
interactive session — which is where you are, and where the tray app lives. If you
see `E_ACCESSDENIED`, check what session you're in before concluding anything.

`tools/windows-activate-test.ps1` is the probe that established this. Read it for
the exact COM interface and GUIDs, then **delete it** as part of your work — the
real implementation is the same call from C#, and the probe shouldn't outlive it.

## What to build

Target: on Windows, the tray menu grows the same "Claude Desktop" section macOS
has — profiles listed with running state, click to launch or focus, quit, plus
"New profile" and "Reveal profiles folder".

Read `ClaudeDesktopManager.cs` (933 lines, 13 `OperatingSystem.IsMacOS()` guards),
`ClaudeDesktopSection.cs`, and `ClaudeDesktopColors.cs` before changing anything.
The pieces:

- **`ProfileRoot`** (`ClaudeDesktopManager.cs:99`) is hardcoded to
  `~/Library/Application Support`. On Windows the default profile is
  `%APPDATA%\Claude`, so the root is `%APPDATA%`. Note `Environment.SpecialFolder.
  ApplicationData` already resolves correctly on both platforms — that's how
  `ClaudeBuddySettings` does it.
- **Discovery** mostly ports as-is: directories named `Claude` / `Claude-*` under
  the root, excluding `Claude-3p` (the app's own sidecar) and `ClaudeBuddy` (ours).
  `LooksLikeProfile`'s marker-file heuristic is platform-neutral; check the marker
  names actually appear in a real Windows profile and adjust if not.
- **Launch.** New Windows path: resolve the AUMID, then `ActivateApplication` with
  `--user-data-dir=<directory>` for a created profile, and **no arguments** for
  Default. Read the comment above the macOS `-n`/`--env` block first: launching
  Default *with* the variable suppresses the app's own sidecar resolution and can
  re-trigger the deployment-mode chooser. The same reasoning applies to the flag —
  Default must launch clean.
  - Keep the `LaunchGate` + authoritative re-scan inside the gate. Concurrent
    Chromium access to one userData directory corrupts leveldb and SQLite; that
    guard is not optional and matters as much here as on macOS.
- **AUMID resolution.** Don't hardcode `Claude_pzs8sxrjxfjjc!Claude`. Derive it:
  package family name from the AppModel registry (or a package API), AppId from
  the manifest's `<Application Id="...">`. If it can't be resolved, treat the app
  as not installed and let the section stay hidden — same as macOS does when
  `Claude.app` is missing.
- **Running-state detection.** `Win32_Process.CommandLine` carries
  `--user-data-dir=` on the main process, so parse that; a `claude.exe` with no
  such argument is the Default profile. This replaces `MacOSProcessScan`; produce
  the same `(Pid, UserDataDir)` shape so `MapInstances` is reused rather than
  duplicated. Beware: **`claude.exe` is also the Claude Code CLI's name** — filter
  by executable path (the packaged one lives under `WindowsApps`), or you will
  report CLI processes as Desktop instances. This confused an earlier session.
  - Watch the cost. The scan runs on a 2-second poll and Windows idle CPU is
    currently ~0%; a WMI query per tick is not free. Cache and only re-query when
    the set of `claude.exe` PIDs changes.
- **Focus and quit** need Windows equivalents of the macOS `NSRunningApplication`
  calls. For focus, reuse the `AttachThreadInput` + `SetForegroundWindow` approach
  already added to `TerminalFocuser.ForceForegroundWindow` — the same
  foreground-lock rule applies, and that fix is on this branch. Factor it out
  rather than copy-pasting it.

## Explicitly out of scope

- **Dock/taskbar icon tinting.** No analogue: the taskbar icon comes from the
  signed package and there is no Dock. Leave `ClaudeDesktopBundles` macOS-only.
- **The window tint overlay.** `ClaudeDesktopOverlay` is built on
  `CGWindowListCopyWindowInfo`; a Windows port is a separate piece of work. Keep
  it gated off.
- **Per-profile colour surfaces** beyond the menu swatch, for the same reasons.

Don't widen the scope to these. A working launcher and accurate running state is
the deliverable.

## Verify before claiming done

Rebuild (`dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish`) and
check, with screenshots as evidence:

1. The Claude Desktop section appears in the tray menu and lists the real
   profiles under `%APPDATA%`.
2. "New profile" creates a directory and launches an instance against it.
3. That instance's own profile directory fills up, and **`%APPDATA%\Claude` is not
   touched** by it.
4. The menu shows the new profile as running, and Default separately.
5. Clicking a running profile focuses its window rather than starting a second
   one.
6. Quit stops that profile only.
7. Owner's real signed-in instance is unaffected throughout.
8. Idle CPU has not regressed from ~0%.

**Never launch a second instance against a directory another instance is already
using**, and never against `%APPDATA%\Claude` while the real one runs.

## Reporting

Same protocol as the verification run, and for the same reason — a previous run
died after 40 minutes and lost everything it hadn't pushed:

- Work on a branch `windows-profile-switcher` off `claude-desktop-profile-switcher`.
- Commit and push **after each piece lands**, not at the end.
- Keep a running `docs/windows-profile-switcher-findings.md` with what you built,
  what you verified, and anything you couldn't.
- Budget ~2 cropped screenshots per check; delete them after reading.
- Don't spawn a nested `claude` process.
- If you get blocked, push what works and say plainly what's incomplete. A partial
  port with an honest boundary is worth more than a confident whole.
