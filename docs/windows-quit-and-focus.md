# Windows: make Quit work, verify two open items

For a Claude Code instance running **unattended on the Windows machine**, in the
interactive session started by a `/IT` scheduled task. Read the "Your situation",
"Ground rules", and "You get exactly one turn" sections of
`docs/windows-recheck.md` first — all three apply here unchanged, including the
pre-stop cleanup checklist and the one-turn rule that killed an earlier run.

Four items. Do them in order and push after each.

## 1. Verify the stranding fix (unverified — do this first)

Your predecessor found that after the first Quit hid the window, a second Quit
click failed instantly with "couldn't quit", and once the force-quit offer
expired there was **no route left to end that instance from the app** — it had to
be killed with `Stop-Process`.

Two changes were made on the Mac in response, neither run on Windows:

- `QuitWindows` now posts `WM_CLOSE` to every top-level window via
  `WindowsAppQuit.RequestClose` instead of `Process.CloseMainWindow()`, which only
  finds *visible* windows and so returned false once the app had hidden itself.
- `ForceQuitOffered` no longer expires on Windows while the process is alive
  (`ClaudeDesktopManager`, the transient state machine). On macOS it still expires.

Verify, on a throwaway profile you create:

- Click Quit. Window hides, row goes to "Quitting…", then reaches "won't quit".
- **Wait past 60s** — the old expiry — and confirm the row is *still* offering
  Force quit rather than falling back to plain "Quit".
- Click Quit a second time while hidden. It must **not** report "couldn't quit";
  it should re-enter "Quitting…" and again reach the force-quit offer.
- Confirm Force quit still ends the tree.

If any of that fails, say exactly which step and what the row said.

## 2. Does terminating the tree corrupt the profile?

This decides item 3, so measure it carefully.

Quit cannot be made to work by asking — `WM_CLOSE` hides, `WM_ENDSESSION` is
ignored, both already established. The remaining question is whether terminating
the process tree is actually harmful, or whether that was over-caution on my part.
Chromium is built to survive abrupt termination (leveldb and SQLite both journal;
a power cut doesn't destroy a Chrome profile), and the corruption risk I was
guarding against is *concurrent access* to one userData directory — a different
failure entirely.

So measure it on a throwaway profile, never on `%APPDATA%\Claude`:

1. Create a profile, launch it, let it fully settle (first-run screen up, disk
   activity done — give it a good 20s).
2. Record the profile's SQLite files. At minimum look for `Cookies`, and any
   `*.sqlite`/`*.db` under the profile root and one level down.
3. Force quit it (`Process.Kill(entireProcessTree: true)` — what the app already
   does).
4. On every SQLite file found, run an integrity check and record the verbatim
   result. `PRAGMA integrity_check;` — if no `sqlite3.exe` is available, use
   .NET (`Microsoft.Data.Sqlite` may not be present; a raw file-header check plus
   step 5 is an acceptable fallback, but say so).
5. Relaunch the same profile and confirm it starts normally and does not present
   a "profile corrupted"/first-run-again state — i.e. it kept whatever state it
   had.
6. Repeat the kill/relaunch cycle **three times** on the same profile. One clean
   kill proves little; a profile that survives three proves the point.

Report the integrity results verbatim. This is the evidence the next item rests
on, so don't summarise it away.

## 3. Make Quit actually quit (only if item 2 came out clean)

If terminating proved safe, change the Windows quit path so **Quit ends the
instance** instead of leaving the user to find Force quit:

- Ask windows to close first (`WindowsAppQuit.RequestClose`) so an app that would
  honour it gets the chance, and so any prompt of its own can appear.
- Give it a short grace period — a couple of seconds — off the UI thread. `Quit()`
  posts to the dispatcher; sleeping there freezes this app's menu and every orb.
- If still alive, terminate the tree, exactly as Force quit does today.
- Keep Force quit in the menu. It becomes redundant on Windows rather than wrong,
  and it stays the escape hatch if a future build does honour a close.
- macOS must be untouched: it has a real graceful quit through
  `NSRunningApplication.terminate()`, and a refusal there is often legitimate
  (unsaved work), which is exactly why the offer-then-confirm design exists.
  Guard by platform.

Then re-verify item 1's flow: Quit should now end the profile within a few
seconds, with the row settling to not-running.

**If item 2 showed any corruption, do not make this change.** Report the
corruption instead — that is a more valuable finding than a working Quit button,
and it would mean the current "offer Force quit and make the user mean it"
behaviour is right.

## 4. The two open click-to-focus items

Both were left unresolved by earlier runs. Neither is worth heroics; spend a
little time and report honestly.

- **VS Code integrated terminal** is `INCONCLUSIVE` — the VS Code window closed
  mid-test for unrelated reasons before anything was measured. Retry it: start a
  Claude Code session in VS Code's integrated terminal with the hook wired, click
  its orb, and say whether the VS Code window comes forward.
- **Multiple Windows Terminal windows**: `Process.MainWindowHandle` names only one
  window per process, so a click can raise the wrong WT window. Recorded as
  unfixable for want of a public API. Spend a bounded look at whether the right
  window can be identified another way — e.g. enumerating the process's top-level
  windows and matching on title, or UI Automation. If there's no reliable route,
  say so and leave the limitation documented; a wrong-window guess is worse than
  the current behaviour, so don't ship a heuristic you can't defend.

## Reporting

Branch `windows-quit-focus` off `main`. Append to
`docs/windows-quit-focus-findings.md`, commit and push **after each of the four
items**. Budget ~2 cropped screenshots per item, deleted after reading. No nested
`claude`. Clean up every profile and instance you create, and leave
`%APPDATA%\Claude` and Owner's running instance alone throughout.
