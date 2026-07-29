# Windows re-check: two fixes made blind

For a Claude Code instance running **unattended on the Windows machine**, in the
interactive session started by a `/IT` scheduled task. Read the "Your situation"
and "Ground rules" sections of `docs/windows-verification.md` first; they apply
here unchanged, including never touching `%APPDATA%\Claude` with a second
instance.

Two changes were made on the Mac side without being run on Windows. Both were
deduced from findings your predecessors reported. Your job is to find out whether
they actually work — and to say so plainly if they don't, because a fix that was
reasoned into existence and never tested is exactly the kind that quietly doesn't
work.

Short task. Don't expand it.

## You get exactly one turn

You are running as `claude -p`. When you stop, the process exits — there is no
next turn, nobody re-invokes you, and anything you were "going to pick up
afterwards" simply never happens. A previous attempt at this exact task ended by
saying *"I'll pick this up when it completes"* while waiting on a background
check; the run exited there, pushed nothing, and left a test profile and a live
Claude Desktop instance behind for the Mac side to clean up.

So:

- **Never defer work to a later turn.** If you need to wait, wait *inside* this
  turn — `Start-Sleep` in a foreground command, then carry on.
- **Never end your turn with work outstanding.** Before you stop: findings
  committed and pushed, test profiles deleted, instances you launched quit.
- If you genuinely cannot finish something, push what you have and write down
  what's missing. Stopping silently mid-check is the one outcome that helps
  nobody.

## 1. Quit should no longer fall through to Force quit

The previous run found Quit never worked: `Process.CloseMainWindow()` posts
`WM_CLOSE`, and Claude Desktop treats that as hide-to-tray, so the window vanished
and the app kept running until Force quit killed it.

`WM_CLOSE` turns out to be the weakest signal Windows has. Its shutdown sequence
is `WM_QUERYENDSESSION` → `WM_ENDSESSION` → `WM_CLOSE` → kill, and Electron acts
on `WM_ENDSESSION` while still ignoring `WM_QUERYENDSESSION`
(electron/electron#44598). So `QuitWindows` now keeps `WM_CLOSE` as the opening
move and escalates: 1.5s later, if the process is still alive, it sends
`WM_QUERYENDSESSION` then `WM_ENDSESSION` to **every top-level window of the
process** — not `MainWindowHandle`, which returns 0 once the app has hidden
itself. See `WindowsAppQuit.cs`.

Verify:

- Create a throwaway profile from the menu, let it start, then use **Quit** on it.
- Does the process tree actually exit, and within the 20s "Quitting…" window?
- Does the row settle to not-running, rather than offering "Force quit"?
- Roughly how long did it take? If it exits only at ~1.5s+, the escalation is what
  did it; if it exits almost immediately, `WM_CLOSE` was enough this time and the
  escalation is untested — say which.
- Confirm Force quit still works when needed.
- **Do not test Quit against Owner's real signed-in Default instance.** Use a
  profile you created.

If it still doesn't quit, that's a legitimate result. Report it, say what the
process did (still running? renderers gone, main alive?), and don't go hunting for
a third mechanism — the point of this check is to learn whether this one works.

## 2. The menu swatch should be a whole dot again

You reported it drawing as a clipped quarter-circle. Diagnosis: the bitmap was 32
physical pixels declared at 192 dpi, so its dip size was 16x16 while its pixel
buffer was 32x32, and Avalonia's Win32 `NativeMenuItem.Icon` path takes the dip
size but reads pixels 1:1 — cropping to the top-left quadrant. It now renders 16
pixels at 96 dpi off macOS (`ClaudeDesktopSection.Swatch`), same geometry in dips,
1:1 pixels. macOS keeps its 2x bitmap unchanged.

Verify with a cropped, zoomed screenshot of the menu: is it a full dot/ring, and
does filled-vs-hollow still distinguish running from not-running? If it's still
wrong, describe precisely what it looks like now — that tells the Mac side whether
the diagnosis was wrong or just the correction.

## Also worth a moment

Nothing on this branch changed the orbs, the tray icon, click-to-focus, or the
settings window, but a lot landed at once. Give the app one smoke test — launch it,
confirm the tray icon and menu still appear, one orb still shows and still
click-focuses its terminal — so a regression doesn't hide behind "we only changed
two things". If it's fine, one line saying so is enough.

## Reporting

Branch `windows-recheck` off `main`. Append to `docs/windows-recheck-findings.md`,
commit and push **per item, not at the end** — a run died once and lost 40 minutes
of findings. Budget ~2 cropped screenshots per item and delete them after reading.
Don't spawn a nested `claude`. Clean up any profile you create.

Push item 1 before you start item 2. The quit check is the one that matters; if
anything goes wrong later, that result must already be safe on the remote.

Cleanup checklist before you stop, since the last attempt skipped all of it:

- Any profile directory you created under `%APPDATA%` is deleted.
- Any Claude Desktop instance you launched is gone (check by command line for
  your profile's path — and use `*` wildcards with PowerShell `-like`, not `%`).
- `%APPDATA%\Claude` and Owner's running instance are untouched.
- `docs/windows-recheck-findings.md` is committed and the branch is pushed.
