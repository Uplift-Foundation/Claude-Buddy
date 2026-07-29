# Windows: "Bring to front" shows a hidden or minimized instance — findings

Verifying the fix in `WindowsForegroundWindow.BringToFront` / `ShowAndFocus` and
`ClaudeDesktopManager.FocusWindows`, per `docs/windows-showhidden.md`, on this
machine, in the interactive session started by the `/IT` scheduled task.

## Before starting

`publish\ClaudeBuddy.exe` was already running (pid 63732, started 4:38:33 PM,
same minute as the exe's own build timestamp, no Start Menu/Registry autostart
entry for it anywhere on this machine). Judgement call: treated this as leftover
debris from whatever session last built this branch, not Owner's day-to-day
tool — there is no mechanism that would have relaunched it on its own, and a
crash dump for an earlier `ClaudeBuddy.exe` pid from earlier today shows a
pattern of repeated test launches, not a long-lived daemon. `ClaudeBuddy.exe`
holds a single-instance mutex (`ClaudeBuddy_SingleInstance_Mutex`), so a second
copy cannot run alongside it regardless. It was stopped (`Stop-Process`, safe —
this is the tray monitor, not Claude Desktop, and it keeps no database that
concurrent access could corrupt) to free both the mutex and the exe's file lock
for rebuilding, and **relaunched identically (same exe, no env overrides) once
all testing below was finished**, so the machine was left in the state it was
found in either way.

Rebuilt clean to a *separate* directory, `publish-test\`, rather than
overwriting `publish\` — `dotnet publish ClaudeBuddy.csproj -c Release -r
win-x64 -o publish-test`, same one expected `AVLN3001` warning as always. All
verification below ran `publish-test\ClaudeBuddy.exe` against a scratch profile
root (`CLAUDE_BUDDY_PROFILE_ROOT` pointed at a temp directory), never
`%APPDATA%\Claude`. Because the scratch root started empty, the tray menu's
Claude Desktop section had no Default row at all — only the one throwaway
profile ever appeared, which incidentally removed any possibility of the
mis-click risk the brief warned about (there was no adjacent Default submenu to
hit in the first place).

Tray icon and menu interaction used Windows' own UI Automation tree to resolve
exact coordinates for the tray icon and each menu item by name (via
`System.Windows.Automation`) immediately before every click, and a screenshot
confirmed the correct submenu was open before "Bring to front" was ever
clicked, per the brief. No coordinate was ever reused blind between screenshots.

## 1. Hidden case (the actual bug) — PASS

Created `Profile-1` from the tray menu's "New profile", waited for it to reach
its first-run "Get started" screen, then resolved its main pid's own top-level
windows and posted `WM_CLOSE` directly to that window's own hwnd (not through
the tray app — a raw PowerShell P/Invoke to the specific hwnd, resolved by
enumerating that pid's windows).

Confirmed the bug precondition exactly as described: process stayed alive
(`Get-Process` succeeded), the window's own `IsWindowVisible` flipped to false,
and — the detail worth recording — `Process.MainWindowHandle` for that pid read
**0** afterward, reproducing precisely what made the old code fail. A full
desktop screenshot showed no Claude window anywhere on screen.

Opened the tray menu, hovered/opened the `Profile-1` submenu, confirmed via
screenshot it showed `Bring to front / Quit / Theme — quit to change / Reveal
logs` (unmistakably the one profile that exists here), then clicked **Bring to
front**. Result:

- `Process.MainWindowHandle` went from `0` back to the same hwnd as before
  hiding.
- The window's own `IsWindowVisible` returned to true.
- `GetForegroundWindow()`'s owning pid matched the profile's pid exactly.
- A screenshot confirmed the "Claude for Windows" welcome window on top and
  active (highlighted taskbar entry).

The fix works for the case it was written for.

## 2. Minimized case — PASS

With the same window now visible, minimized it directly
(`ShowWindow(hwnd, SW_MINIMIZE)` against the resolved hwnd — precise, not a
simulated click on any chrome). Confirmed `IsIconic` true, `MainWindowHandle`
still non-zero (a minimized window is still "visible" in Win32 terms — this is
the pre-existing, already-working path).

Clicked **Bring to front** again from the tray menu (same submenu-then-click
sequence, confirmed by screenshot before clicking). `IsIconic` went back to
false, `IsWindowVisible` stayed true, and `GetForegroundWindow()` again matched
the profile's pid. Still works, as expected — this is the path that worked
before the fix too.

## 3. Already-visible case — PASS, undisturbed

Recorded the window's rect (`GetWindowRect`): `Left=448 Top=150 Right=1064
Bottom=758`. Brought an unrelated window (this session's own terminal) to the
foreground over it with a direct `SetForegroundWindow` call on that window's
own hwnd — not a click on anything belonging to Claude Buddy or Claude
Desktop — so the Claude window was left open, visible, not minimized, just
behind something else. A screenshot confirmed only a sliver of its title bar
was visible underneath the terminal.

Clicked **Bring to front** from the tray menu a third time. Result:
`GetForegroundWindow()` matched the profile's pid again, and the rect read back
identical — `Left=448 Top=150 Right=1064 Bottom=758`, unchanged — and
`IsIconic` was still false. Came forward without being resized, moved, or
re-minimized.

## 4. Right window chosen

This profile's process owned 8 top-level windows total. Only two had a
non-empty title: the main `"Claude"` window (visible throughout, rank 0 in
`ShowAndFocus`'s ordering) and a hidden `"DDE Server Window"` (Electron's
OLE/DDE integration window, rank 2 — has a title, isn't a tool window, so it
*is* technically a candidate, but never wins because visible beats hidden in
the ranking). One window was flagged `WS_EX_TOOLWINDOW` and correctly excluded
regardless of title. So: with a real second titled window present in the
process, `ShowAndFocus` still picked the right one every time, for the reason
the code comment says it would (rank ordering, not "only candidate by
default"). Worth having on record since the brief specifically asked whether
the ranking logic would matter in practice — here it did, if only barely.

## Also: Quit still works — PASS, undisturbed

Clicked **Quit** on `Profile-1`'s submenu (same resolve-by-name-then-click
sequence, confirmed open before the click). Timed with a stopwatch from click
to the main pid disappearing: **3.37s**. That's past `WindowsQuitGraceMs`
(2.5s), consistent with the known behaviour that `WM_CLOSE` alone only hides
the window — the process was still alive at the 2.5s mark, and `Quit`'s
internal escalation to `ForceQuitWindows` is what actually ended the tree just
after. Confirmed no `Claude.exe` process for this profile's `--user-data-dir`
survived (`Win32_Process` query came back empty). This fix did not disturb
Quit's existing behaviour.

## Cleanup

- `publish-test\ClaudeBuddy.exe` (pid 68436, the isolated test instance) was
  stopped.
- The scratch profile root (`%TEMP%\claude-buddy-showhidden-test`, including
  `Claude-Profile-1`) was deleted.
- Confirmed no leftover process anywhere had a command line referencing
  `showhidden-test` or `Claude-Profile-1`.
- `%APPDATA%\Claude` (Default) was never touched — `CLAUDE_BUDDY_PROFILE_ROOT`
  kept every scan pointed at the scratch directory for the whole session.
- The original `publish\ClaudeBuddy.exe` (same build that was already running
  before this session started, containing this same fix) was relaunched
  identically, restoring the machine to the state it was found in.
- `publish-test\` build output directory was left in place (untracked, not
  committed) in case a future session wants to reuse it; it holds no profile
  data and is safe to delete at will.

## Bottom line

All four numbered verification items PASS, and Quit is unaffected. The Mac-side
diagnosis and fix both hold up against a real installed Claude Desktop build:
`Process.MainWindowHandle` did read 0 for the hidden window exactly as
predicted, and `ShowAndFocus`'s enumerate-and-rank approach recovered it every
time, without disturbing the minimized or already-visible cases that already
worked.
