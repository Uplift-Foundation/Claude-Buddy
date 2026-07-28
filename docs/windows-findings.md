# Windows verification findings

Run against `windows-verification` branch, built locally with
`dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish` (.NET 10
SDK, targeting net8.0, RollForward=LatestMajor). Executed unattended on the
Windows box per `docs/windows-verification.md`.

## 1. It starts and stays up — PASS

Launched `publish\ClaudeBuddy.exe` from a background shell. Confirmed via
`tasklist` that the process (PID 29592) was still running ~15s later, under
session `RDP-Tcp#2` (Owner's interactive desktop, not a headless session).
No console window, no output in redirected stdout/stderr — expected for a
`WinExe` notification-area app. No crash, no exception.

## 2. Notification-area icon — PASS

The icon landed behind the `^` chevron (Windows 11's default for new icons),
as the brief warned. Expanded the overflow flyout and zoomed a screenshot crop
4x: it renders as a blue-grey coloured ring, matching the "coloured ring"
description in the brief. Confirmed it's ours (not another app's icon) via the
hover tooltip, which read `Claude Buddy — no sessions`.

## 3. Right-click menu — PASS, with a Windows shell quirk worth flagging

Direct click on the icon once it was promoted out of the overflow (Windows
sometimes pins a recently-used hidden icon onto the visible tray strip)
produced the full, correct menu:

```
No Claude Code sessions        (disabled — 0 sessions)
Show orbs                      (checked)
Reset all sessions to idle     (disabled — 0 sessions)
---
Settings…
Quit Claude Buddy
```

No Claude Desktop section — correct, that feature is macOS-only
(`ClaudeDesktopSection.Append` no-ops on this platform).

**Quirk, not a defect:** while the icon was still hidden behind the `^`
chevron, right-clicking it from inside that overflow flyout produced a menu
missing the `Settings…` row entirely, in two independent trials — "No Claude
Code sessions" / "Show orbs" / "Reset all sessions to idle" / --- / "Quit
Claude Buddy", with the gap where Settings should be simply absent, not
blank. I read `TrayController.cs:153-217` end to end: `Settings…` is built
identically to the three items around it (a plain `NativeMenuItem` + `Click`
lambda, same pattern as `Quit`), added unconditionally, no platform guard. The
same code, same running process, produced the complete 5-item menu once the
icon sat directly on the tray instead of inside the hidden-icons flyout — so
this isn't the app dropping the item, it's the Windows 11 overflow flyout's
own popup-menu host clipping a row. Judgement call: not fixing this, since
there's nothing in `TrayController.cs` to fix — the item is always added. If
it recurs for Owner, the workaround is dragging the icon out of the overflow
(Settings → Personalization → Taskbar → Other system tray icons).

## 4. Orbs via synthetic hook payload — PASS

`$PSVersionTable.PSVersion` for `powershell.exe` (what the shipped hook
snippets actually invoke) on this box: **5.1.26100.5710** — the exact
Windows PowerShell 5.1 the encoding fix in `ClaudeBuddyHook.ps1` targets, not
just PowerShell 7. Confirmed this is what's used by re-reading
`claude-hooks-snippet-windows.json`, which shells out to `powershell.exe`
directly.

Per the brief, avoided spawning a nested `claude`; instead ran
`ClaudeBuddyHook.ps1` directly with synthetic stdin JSON from inside a real
Windows Terminal tab (`wt.exe` new tab, not an ad-hoc `cmd.exe`, so the
terminal-resolution fields come out the way a real session would produce
them). Payload:
`{"session_id":"synthtest2","cwd":"C:\\cb","transcript_path":"...\\transcript.jsonl"}`,
transcript pre-seeded with `ai-title` / `agent-color` lines, piped through
`type payload.json | powershell.exe -File ClaudeBuddyHook.ps1 -State <state>`.

Resulting status file for `-State generating`:
`{"term_program":"WindowsTerminal","color":"green","term_pid":39068,"state":"generating","cwd":"C:\\cb","title":"Test Orb Session"}`
— `term_pid` correctly resolved to the real `WindowsTerminal.exe` PID via the
hook's parent-process walk.

Cycled the same session through all four states and screenshotted the orb
(cropped to a 150x150 top-right corner, zoomed 2x) after each:

- `generating` — violet fill, green ring (from `agent-color`), "T" (from the
  title "Test Orb Session")
- `waiting` — amber/orange fill
- `idle` — slate fill
- `ended` — status file removed, orb gone, confirmed via directory listing

All match the brief's description exactly.

**Encoding fix re-verified under real PS 5.1, not just read in the diff.**
Ran a second synthetic session with a transcript title containing an accent
and an em dash (`café — résumé`) through the same `powershell.exe` 5.1 path.
The written status file round-tripped as clean UTF-8 with no BOM:
`"title":"café — résumé"` (bytes confirmed with a hex dump — `c3 a9` for
`é`, `e2 80 94` for the em dash, first two bytes of the file are `7b 22`
i.e. `{"`, no `ef bb bf`). The orb rendered "C" with no mangling or `?`
placeholder. The `-Encoding UTF8` / `UTF8Encoding(false)` fix from the code
comments holds up in practice on the PowerShell version that's actually
wired into the hooks.

One thing worth recording as a process-model gotcha, not a defect: my first
attempt at this test used `Start-Process cmd.exe` directly (not through
`wt.exe`), which Windows 11's "default terminal application" setting silently
re-hosts inside Windows Terminal anyway — but taking that path instead of a
normal interactive `wt.exe` launch left `WT_SESSION` unset and the parent-walk
landing on a process that had already exited, so `term_pid` came back `0` and
the orb never appeared at all (filtered out by `SessionManager`'s "no
terminal info at all" check). Switching to `wt.exe` directly for a new tab —
matching how a person actually opens Windows Terminal — fixed it completely.
Not a code bug: it just means this specific synthetic-payload test needs a
real terminal launch, not a bare child-process spawn, to be representative.

## 5. Click-to-focus — FAIL, found and fixed one bug; one inherent limitation documented, not fixed

Tested by minimizing the target terminal window, clicking the corresponding
orb, and checking whether it restored and came to the foreground.

**Bug found and fixed: clicking an orb silently did nothing.** First test:
Windows Terminal (`wt.exe`, a genuine new-tab launch), `term_pid` correctly
resolved to `WindowsTerminal.exe`. Minimized that window, clicked its orb —
nothing happened; the window stayed minimized and in the background, no
error anywhere. Root cause, found by reading `OrbWindow.axaml:12`:
`ShowActivated="False"`. That's necessary so the orb never steals keyboard
focus just by existing, but it means clicking it never makes
`ClaudeBuddy.exe` itself the foreground process — and Windows silently
refuses `SetForegroundWindow()` calls from any process that isn't already
foreground (anti focus-stealing protection, present since Windows 2000).
`TerminalFocuser.FocusWindows` was calling `SetForegroundWindow` bare, so
every click was quietly rejected by the OS.

Confirmed via an isolated repro script (bypassing the app, calling the same
Win32 sequence directly) that `SetForegroundWindow` returns `false` under
these conditions, and that wrapping it in `AttachThreadInput` — attach this
thread's input queue to the current foreground window's thread for the
duration of the call — is the standard fix. Implemented it as
`TerminalFocuser.ForceForegroundWindow` (`TerminalFocuser.cs`), used in place
of the bare call. Rebuilt, re-tested against a plain `conhost`-hosted
terminal (unambiguous single window, see below for why that matters):
minimized it, clicked its orb, and this time it restored from minimized and
came to the foreground — confirmed by screenshot and by `IsIconic` reporting
`false` afterward. Windows-only change; `TerminalFocuser`'s macOS path is
untouched.

**Limitation found, not fixed: multiple Windows Terminal windows are
indistinguishable to this app.** While chasing the above, `Process.
GetProcessById(pid).MainWindowHandle` for a `WindowsTerminal.exe` PID
returned a *different* WT window than the one hosting the session being
clicked — Owner already had one WT window open ("claude") and my test had
opened a second, separate WT window ("WTRealTest"), both owned by the same
single `WindowsTerminal.exe` process. `MainWindowHandle` can only ever name
one window per process, and it isn't reliably the one you want. This is the
same limitation `TerminalFocuser.cs:18` already documents for WT *tabs*
("Selecting the exact Windows Terminal tab isn't possible — WT doesn't
expose its tabs to other processes") — it turns out to extend to separate WT
*windows* too, not just tabs within one. Not fixing this: there's no
documented public API to ask WT which of its windows hosts a given process's
console, so there's nothing to change in `TerminalFocuser.cs` beyond what's
already there. Recording it because it means click-to-focus for a WT-hosted
session is only reliable when the user has exactly one Windows Terminal
window open — with more than one, a click may raise the wrong one.

Hosts tried: Windows Terminal (works, single-window case; wrong-window risk
with multiple WT windows as above), plain `conhost` (works cleanly, single
unambiguous window, confirmed after the fix). VS Code's integrated terminal
was also attempted, but the VS Code window exited on its own partway through
setup for reasons unrelated to ClaudeBuddy (no ClaudeBuddy code was running
against it yet) — marking that host **INCONCLUSIVE** rather than guessing at
its behavior.

## 6. Settings window — PASS

Opened via the tray menu's `Settings…` item. No crash (confirms the
already-committed `SetRegular()` fix for the `DllNotFoundException` on
`libobjc` holds). Observed:

- **"Claude Desktop profiles"**: "No profiles found. Create one from the menu
  bar." — correct; this box is macOS-only and Windows has none, per the
  ground rules.
- **Both global toggles present**: "Show orbs" and "Tint the active Claude
  Desktop window", both checkboxes, both checked by default.
- **Closes on Done**.

**Keyboard focus**: confirmed the window is the real foreground window
(`GetForegroundWindow` returned its handle right after opening, no extra
activation trick needed — unlike the macOS path, which the code comments say
needed an activation-policy change). The brief's instruction to "actually
type into a field" doesn't have a literal target here: the only `TextBox` in
`SettingsWindow.cs` (`SettingsWindow.cs:166`) is per-profile, rendered only
inside the profile list, which is empty on Windows — there's no text field
to type into with zero profiles, and that's correct, not a gap. Substituted
the equivalent keyboard-interactivity check: `Tab` moved a visible focus
rectangle through "Show orbs" → "Tint…" → "Done" and wrapped back around
(3 stops, as expected), `Space` toggled "Show orbs" off and back on (screenshot-
confirmed both states), and `Enter` on the focused "Done" button closed the
window. All keyboard-driven, no mouse involved after the initial menu click.
