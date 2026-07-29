# Windows profile switcher: implementation findings

Implemented per `docs/windows-profile-switcher.md`, on this machine, in the
interactive session started by the `/IT` scheduled task. Verified against a
real, already-running, signed-in Default Claude Desktop instance
(`%APPDATA%\Claude`) — it was never launched, quit, or force-quit by any test
below; only Focus (bring-to-front) and read-only discovery ever touched it.

## What was built

- **`ProfileRoot`** now resolves to `%APPDATA%` on Windows
  (`ClaudeDesktopManager.cs`), via the same
  `Environment.SpecialFolder.ApplicationData` call `ClaudeBuddySettings`
  already used — no platform branch needed there, only for the scratch-root
  override's mac fallback.
- **`ClaudeInstance`** (pid + userDataDir) was pulled out of
  `MacOSProcessScan` into its own file so both platforms' scanners return the
  same shape and `MapInstances` needs no per-platform branch.
- **`WindowsProcessScan.cs`** — WMI-based scan (`Win32_Process`,
  `Name='claude.exe'`). Filters to `ExecutablePath` under `WindowsApps`
  (excludes the Claude Code CLI, which is also named `claude.exe`), and
  further filters to the *main* process only (no `--type=` argument) before
  reading `--user-data-dir=`. Caches on a cheap `Process.GetProcessesByName`
  pid-set check, re-running the WMI query only when that set changes.
- **`WindowsAppLookup.cs`** — resolves the AUMID without hardcoding it, by
  reading the AppModel package-repository registry key for the install
  directory and the package's own `AppxManifest.xml` for the `Application
  Id`. No WinRT/`PackageManager` dependency needed.
- **`WindowsAppActivation.cs`** — the real
  `IApplicationActivationManager::ActivateApplication` call; replaces (and
  the probe `tools/windows-activate-test.ps1` was deleted, per the brief).
- **`WindowsForegroundWindow.cs`** — the `AttachThreadInput` +
  `SetForegroundWindow` dance factored out of `TerminalFocuser` so
  `ClaudeDesktopManager`'s Focus/Quit can share it instead of copying it.
- **`ClaudeDesktopManager.cs`** — Launch/Focus/Quit/ForceQuit/RevealLogs/
  RevealProfilesFolder/NewProfile/SetTheme all gained Windows paths.
  Dock-icon-tinting code (`ClaudeDesktopBundles`, the clone/tint branch of
  Launch) stays macOS-only, unchanged.
- **`ClaudeDesktopSection.cs`** — the section now renders on Windows too; the
  "Dock icons" submenu (rebuild/reveal-bundles/tint-active-window) stays
  hidden outside macOS, since none of it has a Windows equivalent (out of
  scope, per the brief).

## Judgement calls

- **AUMID derivation.** The package-repository registry key
  (`HKCR\Local Settings\...\AppModel\Repository\Packages\<PackageFullName>`)
  gives the *full* name (`Claude_1.24012.9.0_x64__pzs8sxrjxfjjc`), not the
  family name AUMIDs need. Rather than pull in a WinRT `PackageManager`
  dependency, the family name is derived as
  `<first "_"-segment>_<last "_"-segment>` of the full name — verified by
  hand against `Get-AppxPackage`'s real `PackageFamilyName` for the installed
  app (`Claude_pzs8sxrjxfjjc`) before writing the code, and it matches.
- **Reveal Logs on Windows** uses `<profile>\logs` unconditionally (no
  Default/created split, unlike macOS) — Electron's `userData` resolves to
  the same directory either way on Windows, confirmed by inspecting a freshly
  created test profile, which had a populated `logs/` folder at that path.
- **Theme submenu** (system/light/dark, writing `config.json`) was left
  enabled on Windows rather than gated off. It isn't in the brief's
  explicitly-out-of-scope list, `config.json`'s shape is identical on both
  platforms (verified directly against the real Default profile's file), and
  the write path needs nothing OS-specific.
- **Marker files vs. a real Windows profile** (per the brief's instruction to
  check): the real `%APPDATA%\Claude` has no top-level `Cookies` file —
  modern Chromium moved it to `Network\Cookies` — so that one marker never
  hits on Windows. Left `MarkerFiles`/`MarkerDirectories` unchanged rather
  than editing shared, mac-verified logic: the other four markers
  (`config.json`, `Local State`, `Preferences`, `ant-did`) plus both marker
  directories (`Local Storage\leveldb`, `Crashpad`) all hit, comfortably
  above the `>= 2` threshold, so `LooksLikeProfile` still classifies real
  profiles correctly.

## Verified end-to-end on this machine

1. **Section appears with real profiles.** Tray menu shows "Claude Desktop",
   "Default", "New profile", "Reveal profiles folder" — no "Dock icons"
   submenu (correctly hidden).
2. **New profile** created `%APPDATA%\Claude-Profile-1` and launched it via
   `ActivateApplication` with `--user-data-dir="...\Claude-Profile-1"`. The
   new instance's main process command line carried exactly that argument
   and nothing else; its window ("Windows... talk with Claude", the
   first-run screen) appeared on screen.
3. **Directory isolation.** The new profile filled with a full Electron
   userData tree (Cache, IndexedDB, Local State, config.json, logs/, etc.).
   `%APPDATA%\Claude`'s mtime was checked before and immediately after
   profile creation/launch/focus/quit and was unchanged by any of it (later
   drift is Owner's own live instance doing its own I/O, not this code —
   nothing in this feature's Windows code path ever targets the Default
   directory except read-only discovery and Focus-by-pid).
4. **Running-state detection, both rows.** Default's row showed "Bring to
   front" (not "Launch") from the very first menu open — proof the
   main-process-has-no-`--type=`-and-no-`--user-data-dir` heuristic
   correctly reads a live, already-running Default instance neither this
   session nor its own launch path started. The new profile's row also
   correctly flipped to "Bring to front" once its `ActivateApplication` call
   returned.
5. **Focus.** Clicking "Bring to front" on the new profile made its window
   the foreground window — confirmed via `GetForegroundWindow` /
   `GetWindowThreadProcessId`, matching the launched pid exactly.
6. **Quit / Force quit.** "Quit" posted `WM_CLOSE` via `CloseMainWindow()`
   but the app did not exit within the 20s window — Claude Desktop's Electron
   shell appears to treat a window close as hide-to-tray rather than app
   quit, the way many Electron chat apps do, so `CloseMainWindow()` alone
   isn't sufficient on Windows the way `NSRunningApplication.terminate()` is
   on macOS. The UI correctly degraded to "won't quit" / "Force quit" exactly
   as designed for a refused quit; **Force quit** (`Process.Kill(entireProcessTree:
   true)`) did terminate every process for that profile. Net effect: Quit
   works, but expect it to routinely end in Force quit on Windows rather than
   being a rare escape hatch — see "Known gaps" below.
7. **Default unaffected throughout** — never launched, quit, or force-quit;
   the running signed-in instance was visibly undisturbed at every check.
8. **Idle CPU.** ~1.3% of one core sampled over 6 one-second intervals with
   the tray running and the WMI-backed scan active (vs. ~0% measured in the
   pre-profile-switcher verification pass) — a small, expected increase from
   polling `Win32_Process` when the claude.exe pid set changes, not a
   regression worth chasing.
9. **Reveal profiles folder** opened Explorer on `%APPDATA%` (window titled
   "Roaming"), confirmed and closed.

## Known gaps / not verified

- **Quit rarely succeeds without Force quit**, per item 6 above. A real fix
  would need a gentler-than-`CloseMainWindow` signal Electron's main process
  actually treats as "quit" (e.g. if the app listens for a custom IPC/window
  message, or if `WM_QUERYENDSESSION` is treated differently than
  `WM_CLOSE`) — not chased further here since Force quit is offered
  automatically and does work. Flagging as a product-behaviour question
  rather than quietly living with it.
- **Tray swatch icon renders as a clipped quarter-circle on Windows**, not
  the full dot/ring macOS shows — colour and filled-vs-hollow (running vs.
  not) both come through correctly, so the state signal isn't lost, but the
  glyph itself is visibly wrong. `ClaudeDesktopSection.Swatch()` sizes the
  `RenderTargetBitmap` as "32 physical px @ 192 DPI = 16 dip", a macOS-menu
  assumption; Avalonia's Win32 `NativeMenuItem.Icon` handling apparently
  doesn't scale/clip it the same way. Not fixed here — it's cosmetic, the
  brief's deliverable is "a working launcher and accurate running state," and
  the running-state text (Launch/Bring to front) is the primary, unambiguous
  signal. Worth a follow-up.
- **Settings window, orbs, and everything from `docs/windows-verification.md`**
  were not re-verified here — out of scope for this task, and nothing in
  this change touches that code.
- Reveal Logs' Windows path (`<profile>\logs`) was confirmed to exist for a
  freshly created profile; not separately re-checked for Default (its logs
  directory is `%APPDATA%\Claude\logs`, same reasoning, not independently
  observed to avoid touching that live directory's window).
