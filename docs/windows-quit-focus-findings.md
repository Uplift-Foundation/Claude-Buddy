# Windows quit-and-focus: findings

Per `docs/windows-quit-and-focus.md`, on this machine, in the interactive
session started by the `/IT` scheduled task.

Before starting: `publish\ClaudeBuddy.exe` was running (pid 2792) but its
timestamp (15:44) predated the stranding fix in `146cd9b` (16:03), i.e. it was
built before that commit. Rebuilding required stopping it first via its own
"Quit Claude Buddy" menu item to release the file lock
(`dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish`, clean
build, only the expected `AVLN3001` warning), then relaunching it identically.
All checks below ran against that freshly built binary. Every trial used a
throwaway profile created via the running instance's own tray menu ("New
profile" under Claude Desktop) — no second `ClaudeBuddy.exe` was launched, and
`%APPDATA%\Claude` (Default, Owner's signed-in instance) was never touched.

Method for driving the tray menu: the notification-area icon and its native
popup menu aren't reliably reachable through UI Automation's Invoke pattern
(tried first — it only produced a tooltip, no popup). What works is a real
simulated click via `user32.dll` `mouse_event` at the icon's UIA-reported
`BoundingRectangle` center: right-click opens the top-level menu, left-click
on a profile row opens its submenu, left-click on an item there activates it.
Screenshots (cropped to the tray-menu corner) are the verification evidence
throughout, per the brief's "screenshots are the ground truth" rule.

## 1. Stranding fix — PASS, all four behaviours confirmed

Created a throwaway profile ("Profile-1"), let it fully launch (its own
"Get started" first-run screen, distinct `--user-data-dir=...\Claude-Profile-1`
process tree of 8 processes).

**Trial: first Quit, wait past the old 60s expiry.**
- Clicked Quit at 16:19:28.727. `Process.MainWindowTitle` for the main pid
  went blank (window hidden) within ~8s.
- At 16:20:06.909 (~38s after the click, past the 20s `QuitWindowMs`), the row
  read **"Profile-1   won't quit"** — `ForceQuitOffered` reached as designed.
- Waited a further ~80s (well past the old 60s `ForceQuitOfferMs`, and past
  the latest-possible transition time even accounting for poll slop). At
  16:22:02.501 — roughly 95s after the original Quit click — the row **still
  read "won't quit"**, not fallen back to plain "Quit". Confirms the
  Windows-only guard in `ResolveTransient`'s `ForceQuitOffered` case (never
  expiring while the process is alive) holds up against a real build.

**Trial: second Quit while hidden.** The literal repro from the brief (click
Quit, then click Quit again on the same still-hidden window before force-quit
resolves it) has no UI path once the fix is in place: `ForceQuitOffered` no
longer expires back to a clickable "Quit" row on Windows, so there is no
button offering "Quit" a second time while an instance is genuinely stuck.
The equivalent real failure mode — the transient state (in-memory, static)
being lost while the underlying window is still hidden — happens whenever
`ClaudeBuddy.exe` itself restarts, which is exactly what the rebuild above
required. So: with Profile-1's window already hidden from the first Quit,
`ClaudeBuddy.exe` was quit and relaunched (transient state reset to `None`,
row back to plain "Profile-1", process still alive and still hidden
underneath). Clicked Quit again at 16:23:33.905:
- At 16:23:49.095 the row read **"Profile-1   Quitting…"** — not "couldn't
  quit". `WindowsAppQuit.RequestClose`'s `EnumWindows` found the hidden
  top-level window and posted `WM_CLOSE` successfully, where the old
  `Process.CloseMainWindow()` (which only finds visible windows) would have
  returned false and produced the stranding error.
- At 16:24:26.813 (~53s later) the row reached **"won't quit"** again — the
  state machine re-entered `Quitting` and re-escalated to `ForceQuitOffered`
  exactly as on the first attempt, not stuck.

**Trial: Force quit ends the tree.** Clicked Force quit at 16:25:01.042; a
poll immediately after (~1.2s later) found 0 processes with
`Claude-Profile-1` in their command line, down from 8. Row settled to
not-running (confirmed via the swatch going hollow in the same check used for
the recheck item below).

Cleanup: `Claude-Profile-1`'s process tree was gone before its directory was
removed (`%APPDATA%\Claude-Profile-1`, verified absent after). `%APPDATA%\Claude`
was not launched, quit, or force-quit at any point.

**Bottom line: the stranding fix works as designed.** All four behaviours the
brief asked to verify held up on a real build: the offer survives past the
old 60s expiry, a second Quit on an already-hidden window succeeds instead of
erroring, the escalation to Force quit is reachable again after it, and Force
quit still reliably ends the tree.
