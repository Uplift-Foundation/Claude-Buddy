# Windows + WSL integration: verify from a fresh clone

For someone with a real Windows machine (WSL installed is a bonus but not
required — see the notes below on what you can still cover without it) who has
**never cloned this repo before**. This walks through building the installer
from source, since PR #11 (WSL orb support + multi-account `CLAUDE_CONFIG_DIR`
support) just merged to `main` and hasn't shipped in a release yet — there is
no `.exe` to download for this.

## Scope — what this is and isn't

`.github/workflows/verify-windows-installer.yml` already installs the native
build silently on a CI runner and checks the file layout, shortcuts, registry
entry, and that `settings.json` gains/loses the right hook entries. **Don't
re-verify plain native single-profile install/uninstall from scratch** — that
part is covered.

What CI *can't* cover, because it needs a real desktop and (for parts of it)
WSL:

1. The installer wizard as a person actually sees it, especially the new
   **WSL task checkbox**.
2. Whether orbs actually appear for a **WSL** Claude Code session, not just a
   native one.
3. The Settings window's new **WSL** section and **Claude Code profiles**
   section — nobody but the original author has ever clicked through these.
4. Whether **uninstall** actually removes hooks everywhere they got wired —
   native, WSL default, and any extra profiles — with nothing left behind.

That's the whole point of this pass. Don't expand it into a general app
review.

If you don't have WSL installed, skip the WSL-specific steps (marked below)
and still do everything else — native-only coverage with the new Settings UI
is still useful signal.

## What you need

- A Windows 10/11 machine you can install software on.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet
  --version` should print `8.x`).
- Inno Setup 6, for compiling the installer:
  ```powershell
  winget install -e --id JRSoftware.InnoSetup
  # or: choco install innosetup
  ```
- *(Optional, for the WSL-specific steps)* WSL with at least one distro that
  has Claude Code installed and on `PATH`.

## 1. Clone and build

```powershell
git clone https://github.com/Uplift-Foundation/Claude-Buddy.git
cd Claude-Buddy
.\tools\build-windows-installer.ps1
```

This publishes the app and compiles the installer in one step, producing
`dist\ClaudeBuddy-<version>-win-x64-setup.exe`. It's unsigned (no
`WINDOWS_CERT_THUMBPRINT` set), so expect a SmartScreen warning in the next
step — that's normal for this build, not a bug.

## 2. Run the installer like a real user

Double-click the `.exe` in `dist\`. Not `/SILENT` — the point is to see the
wizard.

- SmartScreen: what does it say, and how many clicks to get past it ("More
  info" → "Run anyway")?
- **No UAC prompt should appear** — this is a per-user install. A UAC prompt
  means something regressed.
- Does the license page show the MIT text properly?
- Task checkboxes — is everything present, ticked by default, and clearly
  worded?
  - "Wire up Claude Code hooks (required for orbs to appear)"
  - **"Also wire up hooks for Claude Code running under WSL"** — this should
    only appear if WSL is installed on the machine. If you don't have WSL,
    confirm it's simply absent (not shown, greyed out, or shown-but-broken).
    If you do have WSL, confirm it's present and ticked.
  - "Start Claude Buddy automatically when I sign in"
- Finish with "Start Claude Buddy now" ticked.

## 3. Confirm native orbs work

- Notification area icon present? Slate-colored with no sessions running?
- Start a Claude Code session in Windows Terminal / PowerShell (plain
  `claude`, no `CLAUDE_CONFIG_DIR`). Does an orb appear? Send a prompt — does
  it go violet while generating, back to slate when idle? Trigger a
  permission prompt — amber?
- Click the orb — does the right terminal window come forward?
- Exit the session — does the orb disappear?

If nothing appears, check `%TEMP%\claude_buddy\` for status files and run
`/hooks` in the session to see whether the entries actually registered.

## 4. *(WSL only)* Confirm WSL orbs work

- Open a WSL terminal, run plain `claude` there too (same default `~/.claude`
  profile, no `CLAUDE_CONFIG_DIR`).
- Does a **second, independent orb** appear for this session, alongside the
  native one from step 3 if it's still running? Same color behavior
  (idle/generating/waiting)?
- Exit — does that orb disappear on its own, leaving the native one (if
  still running) untouched?

## 5. Settings window — WSL section

Open Settings from the tray menu.

- Is there a **WSL integration** section listing your distro(s), each with a
  checkbox?
- Uncheck a distro's box. Restart a `claude` session in that distro — orb
  should **not** appear.
- Re-check the box. Restart the session again — orb should come back.

## 6. Settings window — Claude Code profiles

This covers `CLAUDE_CONFIG_DIR`-based multi-account support. If you don't
already have a second account set up, create a throwaway one to test with —
in WSL:

```bash
mkdir -p ~/.claude-test
alias testclaude="CLAUDE_CONFIG_DIR=~/.claude-test claude"
```

(Or the native-Windows equivalent, setting `CLAUDE_CONFIG_DIR` before
launching `claude` in PowerShell, if you'd rather test the native side.)

In Settings → **Claude Code profiles**:

- Click **Browse…**, navigate to the profile folder (for the WSL example
  above, that's `\\wsl.localhost\<YourDistro>\home\<you>\.claude-test`), and
  select it. It should be accepted — the picker validates against your
  Windows home directory **or** any WSL distro's home, so a WSL-only profile
  with no Windows-side folder should work fine.
- Click **Add**. Confirm the entry shows up in the list.
- **Close and reopen the Settings window** — the entry should still be
  there.
- **Fully exit Claude Buddy (tray menu → Exit) and relaunch it, then reopen
  Settings** — the entry should *still* be there. (This exact case — an
  added profile silently vanishing after a full app restart — was a real bug
  fixed in this PR; it's worth deliberately re-checking, not just trusting
  that it works now.)
- Restart your test session (`testclaude` in the example above, or the
  native equivalent) — an orb should appear for it too, independent of the
  default-profile orb.

## 7. Uninstall via Apps & Features

Go through Settings → Apps the way a person would, not `unins000.exe`
directly.

- Is the entry named "Claude Buddy" with a sensible version and icon?
- Does uninstalling stop the running app (tray icon gone)?
- Check **every** `settings.json` you wired in this session and confirm none
  of them still reference `ClaudeBuddyHook.ps1`:
  - Native default: `Get-Content "$env:USERPROFILE\.claude\settings.json"`
  - The extra profile you added in step 6 (native or WSL path, matching
    whichever you tested).
  - *(WSL only)* The default WSL profile:
    `\\wsl.localhost\<distro>\home\<you>\.claude\settings.json`
- Afterward, start a fresh Claude Code session (native and WSL, if
  applicable) and run `/hooks` — nothing should point at the now-deleted
  hook script.

## 8. *(Optional)* Sign out and back in

If you left "Start Claude Buddy automatically" checked: does it start by
itself? Confirm exactly **one** instance in the tray, not two.

## What to write up

Create `docs/windows-wsl-integration-verify-findings.md`: what you did, what
happened, and what's wrong — in the style of the other `*-findings.md` files
in this folder. Be specific about exact wording and click counts for
SmartScreen and the wizard checkboxes, and about anything confusing in the
new Settings UI (the profile picker's error messages, the WSL checkbox
wording) — that's going into user-facing documentation.

If something is broken, describe it plainly rather than trying to fix it in
the same pass — a clear repro is worth more than a speculative patch.
