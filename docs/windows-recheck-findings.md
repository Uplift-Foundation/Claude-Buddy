# Windows re-check: findings

Re-checking two fixes made blind on the Mac side, per `docs/windows-recheck.md`,
on this machine, in the interactive session started by the `/IT` scheduled task.

Before starting: `publish\ClaudeBuddy.exe` was already running unattended
(pid 17620, started 3:19 PM, orphaned — its parent process had already exited).
Rebuilding required stopping it to release the file lock. It was stopped,
rebuilt (`dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish`,
clean build, only the expected `AVLN3001` warning), and restarted identically
immediately after, so Owner's orb tracking wasn't left down. All checks below
used that instance's own tray menu — no second `ClaudeBuddy.exe` was launched.

## 1. Quit escalation to WM_ENDSESSION — FAIL, does not quit

**The fix does not work.** Quit still falls through exactly the way the
original bug report described; the WM_ENDSESSION escalation added in
`WindowsAppQuit.cs` did not cause the app to exit in any trial.

Method: created a throwaway profile from the tray menu ("New profile" under
Claude Desktop), let it fully launch (confirmed via its own "Get started"
first-run screen and a distinct `--user-data-dir=...\Claude-Profile-N` main
process), then clicked Quit on that profile's submenu and polled
`Win32_Process` for that profile's command line every 0.5s.

Trial 1 (Profile-1): clicked Quit. The window immediately disappeared from the
taskbar (the known WM_CLOSE hide-to-tray behavior) and `CloseMainWindow()`
evidently returned true (no immediate error). Polled continuously for 28.5s —
process count stayed at a constant 8 the entire time, never dropping. Reopening
the tray menu confirmed the row had reached `Profile-1   won't quit` (the
`ForceQuitOffered` state), which per the code only happens when
`QuitWindowMs` (20s) elapses with the process still alive — i.e. neither the
initial WM_CLOSE nor the 1.5s-later WM_QUERYENDSESSION/WM_ENDSESSION pair
terminated it.

Trial 2 (Profile-2, to rule out Profile-1 being a fluke): same result. Quit
clicked, polled to 21s with the process still fully alive (8 processes), menu
confirmed `won't quit` shortly after. Escalation had no effect a second time,
on a separate freshly-launched instance.

So: the process tree does not exit, not within the 20s "Quitting…" window and
not afterward on its own. The row does reach "won't quit" / offer Force quit
as designed for a refused quit — that part of the state machine is correct —
but the thing it's reacting to (an actually-failed quit) is exactly the
pre-fix behavior. **Escalation was exercised (confirmed via the 1.5s-later
timing and the process staying alive well past it) and made no observable
difference.**

Given Trial 1 hit the 1.5s+ mark before the process disappeared window closed
(it didn't disappear at all), this is squarely "escalation ran and didn't
help," not "WM_CLOSE alone was enough" — the opposite of what was hoped for.

**Confirmed Force quit still works.** On Profile-2, once the menu offered
"Force quit" (`won't quit` row state, reached ~40s after the Quit click, well
inside the 60s offer window), clicking it terminated the entire process tree
(all 8 processes) in under 1 second — first poll already read 0. The row
correctly settled to a hollow ring / "Launch", matching not-running.

**Additional finding, not asked for but discovered along the way:** once the
first Quit attempt has hidden the window (WM_CLOSE succeeds, window vanishes
from the taskbar), a *second* Quit click on the same profile fails immediately
with "couldn't quit" instead of re-attempting anything — `Process.
MainWindowHandle` is presumably 0 once there's no visible window left, so
`CloseMainWindow()` itself returns false before the WM_ENDSESSION escalation
task is even scheduled. This happened to Profile-1: after its first Quit
cycle reached `ForceQuitOffered` and then (for reasons not fully chased —
possibly the display lag between my checks, possibly the offer's own timeout)
reverted to plain "Quit" before I clicked Force quit, a second Quit click
produced `couldn't quit` immediately, and no further Quit or Force quit
attempt through the menu could reach it again — it was permanently stuck
running until killed outside the app (`Stop-Process`, done here as cleanup).
This means the *only* window in which Force quit is reachable is the one
right after the very first Quit click on a given launch; miss it and the UI
has no route back to killing that instance short of Task Manager. Flagging
as a product-behaviour question for the Mac side, not fixing here — root-causing
whether Electron actually processes WM_ENDSESSION on this build, or finding a
gentler-but-reliable signal, is a bigger question than this recheck's scope.

**Bottom line for item 1:** FAIL. The diagnosis (Electron ignores
WM_QUERYENDSESSION but should honor WM_ENDSESSION, per electron/electron#44598)
did not hold up against this real, signed, installed Claude Desktop build —
sending WM_ENDSESSION to every top-level window of the process did not end it.
Whether that's because this Electron version doesn't act on it either, because
the message needs to reach a different window/thread, or something else, is
unknown; not chased further per the brief ("don't go hunting for a third
mechanism"). Force quit remains a reliable fallback when it's still reachable.

Cleanup for this item: both throwaway profile directories
(`Claude-Profile-1`, `Claude-Profile-2`) and every process for both were
removed before moving to item 2 (Profile-2 via its own Force quit; Profile-1,
stuck, via a direct process kill since the UI path was unreachable). Verified
`%APPDATA%\Claude` (Default) was never launched, quit, or force-quit —
untouched throughout, still running as Owner's live signed-in instance.

## 2. Menu swatch — PASS, whole dot again

Confirmed with cropped, heavily zoomed (10x nearest-neighbor) screenshots of
the swatch icon in the Claude Desktop section of the tray menu, for both
states observed live during item 1's testing and again independently
afterward:

- **Filled (running):** Default's swatch — a clean, complete filled circle.
  No cropping to a quadrant; the diagnosis (16 dip / 32 physical-pixel
  mismatch reading 1:1) and the fix (16px @ 96 dpi, 1:1 geometry) both hold up
  on this real Windows menu render.
- **Hollow (not running):** Profile-2's swatch, captured right after Force
  quit settled it to not-running — a clean, complete ring outline, same
  geometry, correctly unfilled.

Filled-vs-hollow still reads unambiguously as running-vs-not, and the colour
itself now correctly differs per profile (Profile-1 was blue, Profile-2 was
green, Default a third blue-grey — colour is per-folder identity, as
designed) rather than the shape distinguishing profiles. No further defect
found here; the diagnosis in the brief was correct and the fix resolved it.

## Smoke test — PASS, no regression found

Tray icon and menu: already exercised heavily above (both items required
opening the menu repeatedly); the icon rendered throughout and the menu
always came up with the expected structure ("No Claude Code sessions",
Claude Desktop section, Show orbs, Reset all sessions to idle, Settings…,
Quit Claude Buddy).

Orbs and click-to-focus: rather than spawn a nested `claude` (explicitly
disallowed — it hangs and competes with this run's own usage), invoked
`ClaudeBuddyHook.ps1` directly with a synthetic JSON payload on stdin
(`-State generating`, a fake `session_id`, `cwd=C:\cb`). It wrote a status
file to `%TEMP%\claude_buddy\<session_id>.txt` as expected. The hook's own
parent-process walk found no window-owning ancestor in this harness's process
tree (`term_pid` came back 0 — this session isn't hosted the way a normal
interactive terminal session is), so to isolate the app's *rendering and
click* behaviour from that unrelated gap, the status file was then
overwritten directly with a known-good `term_pid` (this session's own
`WindowsTerminal.exe`, pid 64904, the "claude"-titled window visible on
screen throughout this run).

- A violet orb appeared top-right showing "S" (first letter of the synthetic
  title "smoke test"), matching the documented violet-while-working state.
- Clicking it brought pid 64904 to the foreground — confirmed via
  `GetForegroundWindow` / `GetWindowThreadProcessId`, not just visually.
- Deleting the status file (simulating session end) made the orb disappear
  within the next poll.

No regression in any of the areas the brief called out (orbs, tray icon,
click-to-focus, menu, settings window untouched by this branch).
