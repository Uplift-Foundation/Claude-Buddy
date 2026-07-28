# Windows verification brief

For a Claude Code instance running **on the Windows machine, inside Owner's
interactive desktop session**. Everything in this project's recent history was
built and verified on macOS; the Windows half of the same code has never been
run. That's the gap you're closing.

Why you and not the Mac-side instance: this app has no main window — it lives in
the notification area and draws floating orbs — so verifying it requires a real
interactive desktop. A process launched over SSH gets a session with no desktop,
where none of that renders. The Mac side can build here remotely but cannot see
anything. It also can't read much output back: the network path between the two
machines drops large packets, which is why this brief travels through git.

Owner is at the keyboard. Ask him what things look like — several checks below
are visual and he is the sensor. Don't guess at appearance.

## Ground rules

- **Never touch `%APPDATA%\Claude`.** That's a real signed-in Claude Desktop
  account. Concurrent access to one Electron userData directory corrupts its
  leveldb and SQLite stores. The multi-profile feature is macOS-only and gated
  off on Windows — leave it alone rather than trying to exercise it.
- **Work on a branch.** Start from `claude-desktop-profile-switcher`, branch to
  `windows-verification`, and push there. Don't push to `main` and don't merge.
- Report exact error text. A paraphrased exception is not a bug report.
- If something is broken because the feature genuinely doesn't apply to Windows,
  say so and move on. Not every gap is a defect.

## Setup

A clone may already exist at `C:\cb`, put there over SSH by the Mac side, along
with a `publish\` directory and a build log at `C:\Users\warre\cbbuild.txt`.
Reuse it if it's there.

```
cd C:\cb
git fetch origin
git checkout -B windows-verification origin/claude-desktop-profile-switcher
dotnet publish ClaudeBuddy.csproj -c Release -r win-x64 -o publish
```

The project targets net8.0 with `RollForward=LatestMajor`; the .NET 10 SDK on
this box builds it. Record any warning you didn't expect.

## What to verify

Run `publish\ClaudeBuddy.exe`. Expect no window — that's correct. Then work
through these, recording PASS / FAIL / N/A and the details:

1. **It starts and stays up.** Still running after ~15s. If it exits, run it
   from a console and capture the exception.
2. **Notification-area icon.** Windows 11 hides new icons behind the `^`
   chevron, so look there before concluding it's missing. Does it render, and
   does it read as a coloured ring?
3. **Right-click menu.** Record the exact item list. Expect the session list (or
   "No Claude Code sessions"), `Show orbs`, `Reset all sessions to idle`,
   `Settings…`, `Quit Claude Buddy`. There should be **no** Claude Desktop
   section — that's macOS-only and its absence here is correct.
4. **Orbs.** Wire the hooks per the README's Windows section, then start a
   Claude Code session in a terminal. An orb should appear near the top-right
   with the first letter of the chat name, falling back to the folder name.
   Confirm it goes violet while Claude works and slate when idle, and that it
   disappears when the session ends.
   - The hook is `ClaudeBuddyHook.ps1`. It had a real encoding bug on PowerShell
     5.1 that was fixed by reading with `-Encoding UTF8` and writing via
     `UTF8Encoding(false)`. If titles or colours come through mangled, that
     area is the first place to look — and note which PowerShell version ran it.
5. **Click-to-focus.** Click an orb; the terminal hosting that session should
   come forward. Try more than one host if you can — Windows Terminal, VS Code's
   integrated terminal, plain conhost — and say which worked. This is the
   feature most likely to need Windows-specific work: the macOS path uses
   AppleScript and tmux, and the Windows path resolves the terminal through
   `Win32_Process.CommandLine`.
6. **Settings window.** Open `Settings…`. It should appear, take keyboard focus
   (actually type into a field — on macOS this needed an activation-policy
   change, and the Windows path may have its own focus quirk), show
   "No profiles found" (correct here), show both global toggles, and close on
   `Done`.
7. **Settings persist.** Turn `Show orbs` off, quit, relaunch, confirm it's
   still off. Then check `%APPDATA%\ClaudeBuddy\settings.json` is valid JSON
   with **no** UTF-8 BOM — it's read by `System.Text.Json`, which rejects a
   leading BOM as an invalid start of value.
8. **Idle CPU.** With a couple of orbs up, sample the process's CPU over several
   seconds. Use a sampling tool — a lifetime average will lie to you. macOS
   sits near 7% of one core after an optimisation pass; report the Windows
   number for comparison.

## Fixing

You have the machine, so you're better placed to fix Windows-specific problems
than the Mac side is. Do fix them, but keep each fix a focused commit with a
message explaining what was actually observed. Prefer a platform guard over
changing shared behaviour: the macOS paths are verified working and shouldn't
regress to accommodate Windows.

If a fix needs a judgement call about product behaviour rather than
correctness, write down the options instead of picking one.

## Reporting back

Commit a `docs/windows-findings.md` on the `windows-verification` branch with one
short section per numbered item, then push the branch. The Mac side reads it
through GitHub. Tell Owner the branch is pushed so he can pass that along.

`gh` works in this interactive session — its token is DPAPI-protected and does
not decrypt over key-based SSH, which is the other reason this runs here.
