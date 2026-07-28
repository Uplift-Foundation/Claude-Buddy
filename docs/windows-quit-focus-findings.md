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
`Claude-Profile-1` in their command line, down from 8.

Cleanup: `Claude-Profile-1`'s process tree was gone before its directory was
removed (`%APPDATA%\Claude-Profile-1`, verified absent after). `%APPDATA%\Claude`
was not launched, quit, or force-quit at any point.

**Bottom line: the stranding fix works as designed.** All four behaviours the
brief asked to verify held up on a real build: the offer survives past the
old 60s expiry, a second Quit on an already-hidden window succeeds instead of
erroring, the escalation to Force quit is reachable again after it, and Force
quit still reliably ends the tree.

## 2. Does terminating the tree corrupt the profile? — clean across 3 cycles

Measured directly rather than through the app, since this is a question about
Chromium's own resilience, independent of ClaudeBuddy: launched a fresh
throwaway profile (`Claude-Profile-1`, created via "New profile" as above),
let it settle ~20s each time, then `taskkill /F /T /PID <main>` (the same
tree-kill `Process.Kill(entireProcessTree: true)` performs), and relaunched
the same profile directly (`Claude.exe --user-data-dir=...`) between cycles.

No `sqlite3.exe` was available on this machine (checked `Get-Command`,
absent). `Microsoft.Data.Sqlite` (8.0.2) and its native `e_sqlite3` bundle
were already in the local NuGet cache, though, so a throwaway console app
(built and run entirely offline, deleted afterward — never part of the repo)
ran real `PRAGMA integrity_check;` queries rather than falling back to a
header-only check.

Five genuine SQLite databases were found under the profile root and one
level down (identified by companion `-journal`/`-wal` files, the giveaway for
a live SQLite store even without a `.db`/`.sqlite` extension): `Network\Cookies`
(the minimum the brief asked for), `DIPS`, `Network\Trust Tokens`,
`Shared Dictionary\db`, and `WebStorage\QuotaManager`. All five were locked
(`SQLITE_BUSY` / "unable to open database file") while Claude Desktop had them
open, as expected, so integrity was checked only after each kill, while the
process was dead.

Verbatim `PRAGMA integrity_check;` results, three kill/relaunch cycles on the
same profile directory:

| File | After kill 1 | After kill 2 | After kill 3 |
|---|---|---|---|
| `Network\Cookies` | `ok` | `ok` | `ok` |
| `DIPS` | `ok` | `ok` | `ok` |
| `Network\Trust Tokens` | `ok` | `ok` | `ok` |
| `Shared Dictionary\db` | `ok` | `ok` | `ok` |
| `WebStorage\QuotaManager` | `ok` | `ok` | `ok` |

All 15 checks returned exactly `ok`, no other rows. Each of the three
relaunches (plus one final relaunch after the third kill, to match the
brief's "confirm it starts normally" step one more time) came up to the same
plain "Get started" first-run screen — the profile never authenticated, so
that screen recurring every time is the expected steady state, not evidence
of a reset; nothing resembling a "profile corrupted" or repair dialog
appeared on any relaunch, confirmed by screenshot on cycles 1 and 3.

**Bottom line: terminating the process tree does not corrupt this profile.**
Three kill/relaunch cycles, five real SQLite stores each time, zero integrity
failures. This matches the reasoning in the brief — Chromium's leveldb and
SQLite stores are built to survive abrupt termination — and the corruption
risk that motivated the original caution (concurrent access to one userData
directory from two simultaneously-running instances) is a genuinely different
failure mode from a single instance being killed once. Item 3's fix is
warranted.

Cleanup: the final Claude-Profile-1 instance was killed, its directory
(`%APPDATA%\Claude-Profile-1`) removed, and the scratch `sqlitecheck` console
project (built under `%USERPROFILE%`, never inside the repo) deleted.
`%APPDATA%\Claude` was untouched throughout.

(Small correction to item 1 above: the Force-quit trial's "row settled to
not-running" claim originally cited a swatch check that was never actually
captured — removed. The 0-process-count observation stands as the evidence
for that trial.)

## 3. Make Quit actually quit — done, verified end-to-end

Item 2 came out clean, so per the brief this change was made: `Quit`'s
Windows path in `ClaudeDesktopManager.cs` now asks first (`QuitWindows`,
unchanged — `WindowsAppQuit.RequestClose`, posts `WM_CLOSE` to every window
of the process), then hands a delayed check to `Task.Run` — `await
Task.Delay(WindowsQuitGraceMs)` (2.5s), then if the process is still alive,
`ForceQuitWindows(pid)`, the same `Process.Kill(entireProcessTree: true)`
Force quit already uses. The delay runs on a thread-pool thread, not the UI
thread: `Quit()` reaches this code via `Dispatcher.UIThread.Post`, and a
blocking sleep there would freeze the tray menu and every orb until it woke
up. Added a small `ProcessAlive(pid)` helper (`Process.GetProcessById` +
`HasExited`, catch → false) so a build that does honour the close request
within the grace period is left alone rather than killed out from under it.
The macOS branch of `Quit()` is untouched — diffed to confirm — and
`QuitWindows`/`ForceQuitWindows` keep their existing `[SupportedOSPlatform]`
guards. Force quit stays in the menu unchanged, as the brief asked, as a
fallback for the (now much narrower) case where the grace-period kill itself
somehow doesn't land.

Rebuilding required stopping the running `ClaudeBuddy.exe` via its own "Quit
Claude Buddy" menu item first (same file-lock reason as before item 1), then
relaunching the freshly built binary.

Re-verified item 1's flow end-to-end against the new behaviour, on a fresh
throwaway profile:

- First trial: clicked Quit, then polled process count. By the first poll
  (~9s later, tool round-trip overhead) the tree was already gone; the tray
  row had settled to a plain, hollow-ringed "Profile-1" — not running, no
  error, and never reached "won't quit" at all.
- Second, tightly-timed trial (click and poll issued from the same script, no
  gap): the process count (8 processes) stayed put through 2.1s, then read 0
  at the 3.16s poll. So the tree ended roughly 2.5–3.2s after the click —
  consistent with the 2.5s grace period plus the time to post `WM_CLOSE`,
  wake the delayed task, and tear down 8 processes. "Within a few seconds,"
  as the brief asked for.
- Tray row confirmed hollow/not-running by screenshot after both trials.

**Bottom line: Quit now actually quits on Windows**, ending the instance in
about 3 seconds without the user ever needing to reach for Force quit, while
Force quit remains available unchanged if a future build's close handling
ever needs more than the grace period allows.

Cleanup: the throwaway profile used for re-verification was force-ended
before this write-up and its directory removed. `%APPDATA%\Claude` was not
touched at any point in this item.

## 4. Click-to-focus loose ends

### VS Code integrated terminal — PASS

Retried per the brief (previously `INCONCLUSIVE` — the VS Code window had
closed mid-test for unrelated reasons). This time: launched a fresh VS Code
window on a throwaway folder (`%USERPROFILE%\vscode-focus-test`, never
`C:\cb`), opened its integrated terminal (a Git Bash shell), and — rather
than spawning a nested `claude` (forbidden) — invoked `ClaudeBuddyHook.ps1`
directly with a synthetic JSON payload piped to a real external
`powershell.exe` process (piping into an in-process `&` call doesn't reach
`[Console]::In` the way the hook expects; had to switch to that after the
first attempt threw `ParameterBindingException`).

The resulting status file recorded `"term_program":"vscode","term_pid":67408`
— `67408` was confirmed (via `Get-Process | Where MainWindowHandle -ne 0`) to
be the exact `Code.exe` process owning the visible window, so the hook's
parent-process walk correctly climbed past the pty host and the shell to the
real VS Code window in one hop. ClaudeBuddy picked up the status file and
showed an orb ("V", from the folder name) at the top-right within a few
seconds.

Test: minimized the VS Code window, confirmed via screenshot it was gone,
then clicked the orb. VS Code restored from minimized and came to the
foreground immediately. Cleaned up with a `state: ended` payload (status file
removed) and closed the throwaway VS Code instance.

**Click-to-focus works correctly for VS Code's integrated terminal.**

### Multiple Windows Terminal windows — confirmed unfixable, not fixed

Bounded look, per the brief. First reproduced the actual mechanism: opened a
second Windows Terminal window (`wt.exe -w -1 new-tab`) alongside the one
hosting this very session. `Get-CimInstance Win32_Process -Filter
"Name='WindowsTerminal.exe'"` showed **one process for both windows** —
confirms Windows Terminal's monarch/peasant model puts every window of a
given launch context in a single process, which is exactly why
`Process.MainWindowHandle` (one handle per process) can't name the right one.

Then checked the two routes the brief suggested:

- **`EnumWindows` + title, the same technique `WindowsAppQuit` already uses
  for quit.** Works mechanically — enumerating that one pid's top-level
  windows returned both (`MINGW64:/c/Users/warre` and `claude`), each with
  its actual window title.
- **UI Automation.** Also works mechanically, and better than expected: WT's
  tab strip exposes real `TabItem` UIA elements with live `Name` properties
  for every tab, not just the active one — `AutomationElement.FindAll` on
  each top-level window returned its tab(s) by name correctly.

So both routes can enumerate windows/tabs and read their titles. The problem
is upstream of that: **Claude Code sets its console/tab title to the literal,
static string `"claude"` — not anything session-specific.** That's directly
visible in the test above: the window hosting this very session (a live
Claude Code CLI run) shows exactly `claude` as both its window title and its
WT tab title, with no cwd, session id, or chat name in it. Two or more
concurrent Claude Code sessions in separate WT windows — the exact scenario
orb-focus exists for — would therefore present **identical** titles to any
matching heuristic. Title-matching can't disambiguate them, mechanically
capable or not, so it isn't a heuristic worth shipping: a confidently wrong
window is worse than today's "best-effort, sometimes wrong" behaviour, per
the brief.

The only way around that would be to make the hook stamp something
session-unique into the console title at status-write time (the console
title is shared by everything attached to one conpty, so a child process
*can* set it) and have the focuser match on that marker. That's not a
correctness fix, though — it would visibly rename tabs/windows the user
never asked to rename, and fight with any title the user (or their shell
prompt) already set. Flagging it as a product-behaviour option rather than
picking it:

- **Option A (status quo):** leave Windows Terminal multi-window as a
  documented limitation — click focuses *a* window belonging to the right
  process, not provably the right one.
- **Option B:** hook writes a unique marker into the console title (e.g. via
  a Win32 `SetConsoleTitle` call framed so it's restored afterward), focuser
  matches on it. Fixes the disambiguation but is a visible, standing product
  change to what the user sees in their own title bar, not merely an
  internal implementation detail — needs sign-off, not a unilateral call
  here.

No public API from Windows Terminal itself exists to ask "which window/tab
is running pid X" — confirmed no such surface while checking the above, only
the generic top-level-window enumeration already used for quit. **Bottom
line: still unfixable within this brief's bounds, now with the mechanism and
the reason confirmed empirically rather than assumed. Left undone**, per the
instruction not to ship an undefendable heuristic.

Cleanup: the extra Windows Terminal window opened for this test was closed
via a targeted `WM_CLOSE` to its own hwnd only, leaving the window hosting
this session (and every other window/instance on the machine) untouched.
