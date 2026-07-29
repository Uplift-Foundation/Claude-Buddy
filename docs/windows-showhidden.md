# Windows: verify "Bring to front" shows a hidden or minimized instance

For a Claude Code instance running **unattended on the Windows machine**, in the
interactive session started by a `/IT` scheduled task. Read the "Your situation",
"Ground rules", and "You get exactly one turn" sections of
`docs/windows-recheck.md` first — all three apply unchanged, including the
pre-stop cleanup checklist.

One item. Short. Don't expand it.

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
   then close the window with its own **X button** so it goes to the tray.
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
