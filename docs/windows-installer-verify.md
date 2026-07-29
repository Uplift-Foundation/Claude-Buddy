# Windows installer: verify the half CI can't

For a Claude Code instance running **unattended on the Windows machine**, in the
interactive session started by a `/IT` scheduled task. Read the "Your situation"
and "Ground rules" sections of `docs/windows-verification.md` first; they apply
here unchanged, including never touching `%APPDATA%\Claude` with a second
instance.

The installer shipped in `v0.1.0-beta` and was never run by a human. CI does
verify a lot of it — `.github/workflows/verify-windows-installer.yml` installs
silently on a runner and checks the file layout, the shortcuts, the registry
entry, that `settings.json` gains exactly 8 hook entries without losing existing
config, that the wired command actually writes and removes a status file, and
that uninstall cleans all of it up.

**So do not re-verify any of that.** What's left is everything that needs a real
desktop, which is precisely what a runner can't give:

1. the wizard as a person sees it, and
2. whether orbs actually appear afterwards.

Short task. Don't expand it.

## You get exactly one turn

You are running as `claude -p`. When you stop, the process exits — there is no
next turn and nobody re-invokes you. Never defer work; if you need to wait, wait
*inside* this turn with `Start-Sleep`. Before you stop: findings committed and
pushed, and anything you installed either left in a deliberate state or removed.

## 1. Run the wizard like a user

Get the installer from the release rather than building it — the shipped bytes
are what matters:

```powershell
gh release download v0.1.0-beta --repo wtvamp/Claude-Buddy --pattern '*-win-x64-setup.exe'
```

Then double-click it. Not `/SILENT` — the point is the wizard.

- Does SmartScreen appear, and what exactly does it say? It's unsigned, so a
  warning is expected; record the wording and how many clicks it takes to get
  past ("More info" → "Run anyway"). This is the first thing every user meets,
  so it belongs in the README verbatim if it's confusing.
- **Is there a UAC prompt?** There should be none — it's a per-user install. A
  UAC prompt means `PrivilegesRequired=lowest` isn't doing its job.
- Does the license page show the MIT text properly, or is it garbled/empty?
  (`LicenseFile` points at a `.txt` copy specifically because Inno picks text vs
  RTF by extension.)
- Are both task checkboxes present, ticked, and readable — "Wire up Claude Code
  hooks (required for orbs to appear)" and "Start Claude Buddy automatically when
  I sign in"? Is the wording clear enough that someone wouldn't untick the first
  one by accident?
- Does the hook step flash a console window? It runs `SW_HIDE`, so it shouldn't.
  A visible flash is cosmetic but worth knowing.
- Finish with "Start Claude Buddy now" ticked.

## 2. Does it actually work afterwards

- Is there an icon in the notification area? With no sessions running it should
  be slate-colored and its menu should say "No Claude Code sessions". Note
  whether the icon is the generated orb art or a generic placeholder — the `.ico`
  is new and has never been seen rendered.
- Start a Claude Code session in Windows Terminal. **Does an orb appear**, and
  does it show the right letter? Send a prompt: does it go violet while
  generating and back to slate when done? Trigger a permission prompt: amber?
- Click the orb — does the right terminal window come forward?
- Exit the session: does the orb disappear?

If orbs never appear, that's the whole point of this check — dig into why. Look
at `%TEMP%\claude_buddy\` for status files, and run `/hooks` in the session to
see whether the entries registered.

## 3. Sign out and back in

Does Claude Buddy start by itself? That's the startup shortcut. Confirm there's
exactly **one** instance, not two.

## 4. Uninstall from Apps & Features

Not the `unins000.exe` path CI already covers — go through Settings → Apps, the
way a person would.

- Is the entry named "Claude Buddy" with a sensible version and the orb icon?
- Does uninstalling remove the notification-area icon, i.e. was the running app
  actually stopped?
- Afterwards, does a Claude Code session still start cleanly with no hook errors?
  Run `/hooks` and confirm nothing points at the deleted script.

## What to write

`docs/windows-installer-verify-findings.md`, in the style of the other findings
files: what you did, what happened, and what's wrong. Be specific about wording
and click counts for the SmartScreen and wizard bits — those are going into user
documentation.

If something is broken, say so plainly and don't fix it in the same run unless
it's obvious and small. A clear description of a real failure is worth more than
a speculative fix, and the Mac side can act on it.
