# Windows verification brief

For a Claude Code instance running **unattended on the Windows machine**, inside
the logged-on interactive desktop session. Everything in this project's recent
history was built and verified on macOS. The Windows half of the same code has
never been run. That's the gap you're closing.

## Your situation

- **Nobody is watching.** Don't ask questions — no one will answer. Where a
  choice comes up, make it, and write down what you chose and why.
- **You are the only one who can see the screen.** The Mac-side instance drives
  this box over SSH, but an SSH-launched process gets a non-interactive window
  station where GUI programs are invisible. You were started through a scheduled
  task with `/IT` specifically so you'd land on the real desktop. Screenshots
  (recipe below) are your eyes.
- **The Mac side can barely read your output.** The network path between the two
  machines drops large packets, so don't rely on anything you print. Your report
  travels through git.
- **Owner may be using this machine while you work.** Windows you open appear on
  his screen. Don't reboot it, don't kill his RDP session, don't kill the parent
  process that started you, and close what you open when you're done.

## Ground rules

- **Never touch `%APPDATA%\Claude`.** That's a real signed-in Claude Desktop
  account. Concurrent access to one Electron userData directory corrupts its
  leveldb and SQLite stores. The multi-profile feature is macOS-only and gated
  off on Windows — don't try to exercise it.
- **Never report PASS for something you didn't observe.** If a check can't be
  automated from where you're sitting, mark it `INCONCLUSIVE` and say what
  stopped you. An honest gap is useful; a fabricated PASS poisons the whole
  report, because the Mac side can't independently see any of this.
- Record exact error text. A paraphrased exception is not a bug report.
- If something is "broken" only because the feature doesn't apply to Windows,
  say so. Not every gap is a defect.

## Setup

The repo is already cloned at `C:\cb`, built, and checked out on the
`windows-verification` branch, with `publish\ClaudeBuddy.exe` present. The Mac
side did that over SSH. Rebuild if you change code:

```
cd C:\cb
dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish
```

The project targets net8.0 with `RollForward=LatestMajor`; the .NET 10 SDK here
builds it. The one expected warning is `AVLN3001` about `OrbWindow.axaml` — it
occurs on macOS too and is not a Windows problem.

## How to see the screen

```powershell
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$s = [Windows.Forms.SystemInformation]::VirtualScreen
$b = New-Object Drawing.Bitmap $s.Width, $s.Height
[Drawing.Graphics]::FromImage($b).CopyFromScreen($s.Location, [Drawing.Point]::Empty, $b.Size)
$b.Save('C:\cb\shot.png')
```

Then Read `C:\cb\shot.png` — you can look at images directly. Crop to a region
when you need detail, and take a fresh shot after every action rather than
reasoning about what the screen probably looks like now.

`System.Windows.Automation` is available if you need to drive or inspect
controls. Avalonia implements UIA on Windows, so its own windows should be
reachable; the notification-area menu is a native Win32 menu and may not be.
Screenshots are the ground truth when the two disagree.

## What to verify

Run `publish\ClaudeBuddy.exe`. Expect no main window — that's correct, it's a
notification-area app. Record PASS / FAIL / INCONCLUSIVE plus details for each:

1. **It starts and stays up.** Still running after ~15s. If it exits, run it from
   a console and capture the exception.
2. **Notification-area icon.** Windows 11 hides new icons behind the `^`
   chevron — expand that before concluding anything is missing. Does it render,
   and does it read as a coloured ring?
3. **Right-click menu.** Record the exact item list. Expect the session list (or
   "No Claude Code sessions"), `Show orbs`, `Reset all sessions to idle`,
   `Settings…`, `Quit Claude Buddy`. There should be **no** Claude Desktop
   section — that's macOS-only, and its absence here is correct.
4. **Orbs.** An orb should appear near the top-right per session, showing the
   first letter of the chat name, falling back to the folder name; violet while
   Claude works, slate when idle, gone when the session ends.
   - The state comes from `ClaudeBuddyHook.ps1` writing a status file. The
     deterministic way to test the app is to invoke that hook yourself with a
     synthetic payload and watch what the app does — that isolates the app from
     Claude Code's own hook wiring. Then, separately, confirm the real path works
     by running a nested `claude -p` in another directory with the hooks
     configured per the README.
   - That hook had a genuine PowerShell 5.1 encoding bug, fixed by reading with
     `-Encoding UTF8` and writing via `UTF8Encoding(false)`. If titles or
     colours come through mangled, start there — and record which PowerShell
     version ran it (`$PSVersionTable.PSVersion`).
5. **Click-to-focus.** Click an orb; the terminal hosting that session should
   come forward. This is the likeliest place to need Windows-specific work: the
   macOS path uses AppleScript and tmux, while the Windows path resolves the
   terminal through `Win32_Process.CommandLine`. Try more than one host if you
   can — Windows Terminal, VS Code's integrated terminal, plain conhost — and
   say which worked.
6. **Settings window.** Open `Settings…`. It should appear, take keyboard focus
   (actually type into a field — on macOS this needed an activation-policy
   change and Windows may have its own quirk), show "No profiles found"
   (correct here), show both global toggles, and close on `Done`.
7. **Settings persist.** Turn `Show orbs` off, quit, relaunch, confirm it's still
   off. Then check `%APPDATA%\ClaudeBuddy\settings.json` is valid JSON with **no**
   UTF-8 BOM — it's read by `System.Text.Json`, which rejects a leading BOM as an
   invalid start of value.
8. **Idle CPU.** With a couple of orbs up, sample the process's CPU over several
   seconds. Use a sampling approach — a lifetime average will lie to you, which
   already wasted an afternoon on the macOS side. macOS sits near 7% of one core
   after an optimisation pass; report the Windows number.

## Fixing

You have the machine, so you're better placed to fix Windows-specific problems
than the Mac side is. Do fix them. Keep each fix a focused commit whose message
says what you actually observed.

Prefer a platform guard over changing shared behaviour: the macOS paths are
verified working and must not regress to accommodate Windows. If a fix needs a
product-behaviour judgement rather than a correctness one, write down the options
instead of quietly picking one.

Rebuild and re-verify after each fix. A fix you didn't run isn't a fix.

## Reporting back

Commit `docs/windows-findings.md` on the `windows-verification` branch — one short
section per numbered item, with the CPU number and exact errors — then **push the
branch**. That's the channel the Mac side reads, through GitHub.

`gh` works in this interactive session. It does not work over SSH, because its
token is DPAPI-protected and a key-based SSH logon has no password-derived master
key to decrypt it — which is the other reason this work happens here rather than
there.

Finish by pushing even if the run went badly. A report saying "3 of 8 verified,
here's what blocked the rest" is worth far more than silence.
