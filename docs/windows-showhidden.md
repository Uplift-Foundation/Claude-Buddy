# Windows: verify "Bring to front" shows a hidden or minimized instance

For a Claude Code instance running **unattended on the Windows machine**, in the
interactive session started by a `/IT` scheduled task. Read the "Your situation",
"Ground rules", and "You get exactly one turn" sections of
`docs/windows-recheck.md` first — all three apply unchanged, including the
pre-stop cleanup checklist.

One item. Short. Don't expand it.

## Read this before you click anything

A previous attempt at this exact task **force-quit Owner's real signed-in
Claude Desktop** and then killed itself. It was driving the tray menu with
simulated `mouse_event` clicks at computed coordinates, and in that menu the
**Default** row sits directly above the throwaway profile's row — so one
mis-targeted click reaches the live instance's submenu, including its Force
quit. The run then exited with `STATUS_CONTROL_C_EXIT`, which is what happens
when a console window is closed, i.e. a stray click almost certainly also hit
the X on its own console.

Rules that follow from that:

- **Never simulate a click on window chrome** — no X buttons, no title bars.
  Where you need a window closed, hidden, or shown, post the message to a
  specific `hwnd` you resolved from a specific pid. That is precise, and it
  cannot land somewhere else.
- **Never click the Default row or its submenu**, for any reason. Default is
  Owner's account. If you must confirm something about Default, read it —
  process list, command line, window state — never click it.
- Prefer resolving and messaging windows by pid/hwnd over the menu entirely.
  The one thing that genuinely needs the menu is "Bring to front", because
  that is the code under test; get there by opening the *profile's* submenu,
  and verify by screenshot which submenu is open **before** clicking an item
  in it.
- If a click doesn't land where you expected, stop clicking and reassess from
  a fresh screenshot rather than trying again a few pixels over.

## The bug that was fixed

Closing Claude Desktop's window sends it to the tray instead of quitting, so a
profile can be running with no visible window. In that state "Bring to front" did
nothing: it used `Process.MainWindowHandle`, which only reports *visible* windows
and so returned zero.

Fixed on the Mac side, unverified:

- `WindowsForegroundWindow.BringToFront` now shows a hidden window (`SW_SHOW`) as
  well as restoring a minimized one (`SW_RESTORE`), in that order — a window can
  be both, and showing an iconic window leaves it iconic.
- New `WindowsForegroundWindow.ShowAndFocus(pid)` finds the window by enumerating
  the process's top-level windows rather than asking for the main one. Candidates
  must have a title and must not be tool windows, because Chromium processes own
  several windows that aren't the app. Ranked visible → minimized → hidden.
- `ClaudeDesktopManager.FocusWindows` now calls `ShowAndFocus(pid)`.

## Verify

On a throwaway profile you create — never `%APPDATA%\Claude`:

1. **Hidden case (the actual bug).** Launch the profile, wait for its window,
   then hide it by posting `WM_CLOSE` **to that window's own hwnd** — resolve
   the hwnd by enumerating top-level windows for the profile's own pid. Claude
   Desktop turns `WM_CLOSE` into hide-to-tray, so this produces exactly the
   state under test.
   Confirm it's genuinely hidden and still running: no visible window, but the
   process tree alive, and `Process.MainWindowHandle` for the main pid reads
   **zero** (worth recording — it's the thing that made the old code fail).
   Then click **Bring to front** in the profile's submenu. The window should
   reappear and be foreground. Confirm with a screenshot and by checking
   `GetForegroundWindow` belongs to that pid.
2. **Minimized case.** Minimize the window normally, click Bring to front, and
   confirm it restores and comes forward. This worked before; make sure it still
   does.
3. **Already-visible case.** With the window open but behind something else,
   click Bring to front and confirm it comes forward and is *not* disturbed
   otherwise (not resized, not re-minimized).
4. **Right window chosen.** If the profile happens to own more than one real
   window, note which came forward. Not a failure either way — just record it.

Also confirm the fix didn't disturb Quit: click Quit on the profile and check it
still ends the tree in a few seconds (it should — Quit deliberately still uses
`MainWindowHandle`, so that a hidden app isn't un-hidden purely to be killed).

If the hidden case still fails, that's the important result — say exactly what
happened: did the window stay hidden, did it flash and vanish, did the process
die? And record whether `ShowAndFocus` found any candidate window at all (a quick
`EnumWindows`-equivalent listing of that pid's top-level windows with their
titles, visibility and styles would settle it).

## Reporting

Branch `windows-showhidden` off `main`. Write `docs/windows-showhidden-findings.md`,
commit and push it **before** doing the Quit re-check in case anything goes wrong
after. ~2 cropped screenshots, deleted after reading. No nested `claude`. Clean up
the profile and any instance you started, and leave Owner's running instance
alone.
