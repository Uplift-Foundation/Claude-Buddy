# Claude Buddy

One tiny always-on-top orb per running Claude Code session, stacked in the
top-right corner of your screen. Runs on **Windows and macOS** (Avalonia,
one codebase). Each orb has three states:
- **Slate-blue, gentle breathing** — truly idle, nothing happening.
- **Violet, medium pulse** — Claude is actively generating a response or
  running tools.
- **Amber, fast pulse** — Claude needs something from you specifically: a
  tool-permission approval, or an answer to an interactive question.
  Claude finishing a response and waiting for you to type whatever's next
  does *not* trigger this — that's deliberate, not a bug; see the matcher
  note below if you want it back.

Each orb shows the first letter of **what the chat is named**, in preference
order: the name you gave it with `/rename`, else the title Claude Code
generates once there's enough conversation to summarize, else the working
directory's name. A `/rename` wins even if Claude Code has since re-titled
the session, and the letter changes over as soon as a name appears — so two
sessions in one repo stop looking identical. Hover for the name and the full
path; the right-click menu leads with both (plus reset that session to idle /
exit Claude Buddy entirely).

If you've given a session a color with **`/color`**, that color becomes the
orb's **border and letter**. The fill is left alone deliberately — it's the
state signal, and amber-means-Claude-needs-you only works if it means that on
every orb. So color says *which* session, fill says *what it's doing*.

**Only colors you set with `/color` show up.** Claude Code also gives every
session an automatic accent (the color of its prompt border and name chip),
but that one is per-process and isn't written to the transcript — or anywhere
else on disk — so the hook has nothing to read and those orbs keep the plain
hairline border. That's deliberate rather than a gap worth papering over: a
derived stand-in would end up disagreeing with the color the terminal is
showing, and a ring is more useful when it always means "I chose this". Run
`/color` in a session and its orb picks the color up on the next hook fire,
within a couple of seconds.

Names and colors come out of the session's own transcript, where Claude Code
records them as `{"type":"custom-title",...}`, `{"type":"ai-title",...}` and
`{"type":"agent-color","agentColor":"green"}` — the hook reads the newest of
each and the app never has to guess. **Left-click an orb to jump to that
session's terminal**, best-effort:
- macOS: the exact iTerm2 pane or Terminal.app tab when possible,
  otherwise just activating the terminal app. **tmux sessions land on the
  right pane** — see below. The first click asks for macOS Automation
  permission to control your terminal — approve it once.
- Windows: the terminal window the session runs in (native sessions);
  WSL sessions fall back to activating Windows Terminal / VS Code, since
  the Windows-side parent chain can't be traced from inside WSL. Jumping
  to the exact Windows Terminal *tab* isn't possible — WT doesn't expose
  its tabs to other processes.

Click-to-focus needs the hook script from this version; sessions started
under an older copy just won't respond to clicks until they're restarted.

### tmux (macOS)

A session running inside tmux needs two separate things to happen, and doing
only one of them leaves you looking at the wrong thing:

1. **tmux has to select the pane.** The attached client is probably showing
   some other window, so activating its terminal alone would drop you
   somewhere else entirely. The hook records `$TMUX_PANE` (a server-unique
   pane id like `%3`) plus the socket path from `$TMUX`, and a click runs
   `select-window` + `select-pane` against them. If nothing is attached, the
   pane is still selected, so it's already current next time you attach.
2. **The right terminal window has to come forward.** Which app that is
   *can't* be recorded when the hook runs — you can detach a tmux session and
   reattach it from a different terminal, or from none — so it's resolved on
   every click from the live client's tty, by walking up that tty's process
   tree until it hits an `.app` bundle. That works for any terminal without a
   case per app: iTerm2, Terminal.app, Ghostty, WezTerm, kitty, Alacritty,
   VS Code. iTerm2 and Terminal.app additionally get the *exact tab* selected,
   since they expose a session's tty to AppleScript; everything else gets
   activated and relies on step 1 to have put the right pane on screen.

Details worth knowing:
- If the session is attached from several terminals at once, the most
  recently active client wins. If no client is on that session, the most
  recently active client elsewhere gets switched to it.
- iTerm2's native tmux integration (`tmux -CC`) is handled specially: the
  control client's tty is a hidden control tab, so that case skips exact-tab
  selection and just activates iTerm2, which mirrors tmux windows as native
  tabs and follows the pane selection itself.
- Inside tmux the hook deliberately does **not** record `ITERM_SESSION_ID`.
  It's inherited from whenever the pane was created and is stale as often as
  not; jumping to the wrong pane is worse than not jumping.
- The app can't rely on `PATH` to find `tmux` — launched from Finder or Login
  Items it gets the bare system `PATH`, with no Homebrew in it — so the hook
  records the tmux binary's location, with the usual install paths as
  fallbacks.
- **WSL + tmux is not covered.** The Windows hook is PowerShell running
  outside the Linux environment, so it never sees `$TMUX`; clicks on those
  orbs behave as they always have (activate the terminal window).

There's also a **status-bar icon** — macOS menu bar, Windows notification
area — that's there whether or not any session is running. Its color tracks
the most urgent session (amber if any session needs you, violet if any is
working, otherwise slate), and its menu lists the live sessions by chat name
(falling back to folder name, same as the orbs, and truncated if it runs
long) — click one to jump to its terminal, same as clicking its orb. Two
sessions that end up with the same label get a short session-id suffix so you
can tell which is which. The menu is also the app's only permanent control
surface, since with zero sessions there are no orbs to right-click:
- **Show orbs** — hide the orbs and run status-bar-only. Sessions keep being
  tracked, so the icon and menu stay live. Resets to shown on relaunch.
- **Reset all sessions to idle** — the bulk version of an orb's
  right-click reset, for clearing out orbs left behind by Ctrl+C'd sessions
  (see the pruning note below).
- **Quit Claude Buddy**.

On macOS the menu opens on a left-click of the menu-bar icon. On Windows it's
a **right-click**, and there's one wrinkle worth knowing: Windows 11 does not
put newly registered tray icons on the taskbar. It files them in the hidden
overflow behind the **`^`** chevron, so after the first launch you'll find
Claude Buddy there — drag it onto the taskbar once to pin it (that's what
sets `IsPromoted` in `HKCU\Control Panel\NotifyIconSettings`, which Windows
then remembers). Nothing to configure in the app; it's how Windows 11 treats
every new icon.

### Claude Desktop profiles (macOS)

Unrelated to session monitoring, and sharing nothing with it but the menu:
the status-bar menu can run several copies of the **Claude Desktop** app side
by side, each signed into a different Anthropic account. Claude Desktop signs
into one account at a time and keeps that login in its user-data directory
(`Cookies` → `sessionKey`, `config.json` → `oauth:tokenCache`) rather than the
Keychain, so a second account is a second directory — selected with
`CLAUDE_USER_DATA_DIR`, which the app honors, and it takes no single-instance
lock, so the instances genuinely coexist.

Profiles are **discovered from disk**, not configured: any directory in
`~/Library/Application Support` named `Claude` or `Claude-<something>` that
looks like a real profile (or is empty). `Claude` shows as **Default**,
`Claude-work` as **work**. Each gets a submenu with launch/bring-to-front,
quit, and reveal logs; a filled dot means it's running. **New profile**
creates `Claude-Profile-N` and launches it — sign in there with the second
account. Renaming one means quitting it and renaming the folder, which is
what **Reveal profiles folder** is for. The section is hidden entirely if
`Claude.app` isn't installed.

Each profile gets a **colour**, derived from its folder name so it survives
restarts and needs no config, and that one colour shows up on four surfaces:

- **The tray menu** — a real swatch beside each row (filled = running, hollow =
  stopped). Colour is identity, fill is state, exactly as with the orbs.
- **The Dock** — each created profile launches from its own APFS clone of
  `Claude.app` whose icon is Claude's mark recoloured. 1.5 MB of real disk for a
  754 MB bundle. Default keeps the bundle you installed, icon and all.
- **The window itself** — the frontmost instance gets a coloured border and a
  faint wash, drawn by a click-through overlay pinned to its frame.
- **Light or dark** — each profile's own `userThemeMode`, set from its submenu
  while it's stopped.

Details worth knowing:

- **Why the Dock clone is safe.** A custom Finder icon lives in an `Icon\r` file
  at the bundle root plus a `com.apple.FinderInfo` xattr — both *outside*
  `Contents/`, which is what the code signature seals. The result: `codesign
  --verify` passes, `spctl` still reports "Notarized Developer ID", and the
  CDHash is byte-identical to Anthropic's. That last part is the point — the
  running code identity is unchanged, so the `Claude Safe Storage` keychain ACL
  still matches (stored logins keep decrypting) and existing TCC grants still
  apply. Renaming the app would mean editing `Info.plist`, which forces a
  re-sign and loses all of it, so every clone still calls itself "Claude".
  Only `codesign --verify --strict` objects, over the xattr.
- **Clones go stale after a Claude update.** Squirrel only updates
  `/Applications/Claude.app`, so **Dock icons → Rebuild after a Claude update**
  re-clones. Bundles live in `~/Library/Application Support/ClaudeBuddy/bundles/`
  and are pure cache — deleting them only costs the colours. Each is named
  exactly `Claude.app` inside a per-profile directory, because the process scan
  matches on the path suffix `/Claude.app/Contents/MacOS/Claude`; naming bundles
  after profiles would silently break running-detection for cloned instances.
- **Why the window tint is an overlay rather than real theming.** There is no way
  in: the app has no accent-colour concept (its theme is a `body` class driven by
  `prefers-color-scheme`), Chromium removed `--user-stylesheet` years ago (0
  occurrences in the shipped Electron binary), and remote debugging — the one
  route that could inject CSS — is refused unless `CLAUDE_CDP_AUTH` carries an
  Ed25519 signature over `timestamp.base64(userDataDir)`, verified against a key
  embedded in `app.asar`, bound to that exact profile path and valid for five
  minutes. So the tint is drawn over the app instead. Frames come from
  `CGWindowListCopyWindowInfo`, which gives bounds and owner pid with **no**
  permission prompt (only titles and images need Screen Recording).
- **The tint only follows the frontmost instance.** The overlay is topmost, so
  showing it for a background window would drop a coloured rectangle on top of
  whatever app you were actually using. Windows on other Spaces are skipped too:
  they still count as "on screen" to CGWindowList but report coordinates in that
  Space's frame, far outside any display. Toggle it under **Dock icons → Tint the
  active window**; like the orb toggle, it resets on relaunch. Verified
  pixel-exact against a live window, and click-through, so clicks reach Claude.
  Only tested on a single display.
- **`Claude-3p` and `Claude-dev` are skipped.** `-3p` is Claude Desktop's own
  sidecar config directory (`configLibrary/`, `deploymentMode`) that a normally
  launched instance reads and writes — offering it as a profile would point a
  second Chromium at a directory the running app is already using, and
  concurrent access to one user-data directory corrupts leveldb and SQLite.
- **Default is launched differently, on purpose** — plain `open -n -b`, with no
  `CLAUDE_USER_DATA_DIR`. Setting the variable suppresses the app's own
  resolution of that sidecar directory, so a tray launch could re-trigger the
  enterprise deployment-mode chooser on an already-configured profile, and it
  would start a second log history under `Claude/Logs/`. One consequence:
  Default's logs are at `~/Library/Logs/Claude`, everyone else's are at
  `<profile>/Logs`, and Reveal logs knows the difference.
- **Running instances are detected by scanning processes, not by tracking the
  ones we launched** — `proc_listallpids` + `proc_pidpath` to find Claude
  Desktop main processes, then `sysctl KERN_PROCARGS2` to read
  `CLAUDE_USER_DATA_DIR` out of each one's environment. So an instance you
  started from the Dock shows up too, and the state survives restarting Claude
  Buddy. (Not `ps eww`: it prints the environment space-separated, and every
  profile path contains a space — `Application Support` — so its output can't
  be parsed back into paths.)
- **Quit is a real quit**, an Apple Event via `NSRunningApplication`, so it
  runs the app's shutdown and can be refused — by an unsaved-work dialog, or
  by a Cowork VM or local-agent session. A refusal shows up as *"allow
  Automation"* if it was a permission problem, and after a timeout the item
  becomes **Force quit**, which needs a second deliberate click. Nothing here
  ever escalates to a kill on its own.
- **The auto-updater is shared.**
  `~/Library/Caches/com.anthropic.claudefordesktop.ShipIt/` is keyed by bundle
  id, not by profile, so two instances updating at once can collide. Nothing
  the app can do about it.
- **Each profile is a separate device** as far as the server is concerned —
  its own `ant-did`.
- `CLAUDE_BUDDY_PROFILE_ROOT` overrides the directory profiles are discovered
  in, which is how to try this out without touching your real one.

It works by watching a small folder in the OS temp directory
(`%TEMP%\claude_buddy\` on Windows, `$TMPDIR/claude_buddy/` on macOS) that
fills up with one JSON status file per session — `<session_id>.txt`,
containing `{"state": "...", "cwd": "...", "title": "...", "color": "...", ...}`
— written by a tiny script that Claude Code hooks invoke:
`ClaudeBuddyHook.ps1` (PowerShell, Windows/WSL) or
`ClaudeBuddyHook.sh` (bash, macOS). No network calls, no polling of Claude
Code itself, no persistent process beyond the hook calls themselves.

A session's orb disappears when its `SessionEnd` hook fires (clean exits
like `/exit`) or — since `SessionEnd` is documented as unreliable on
ungraceful termination, notably Ctrl+C — once its file hasn't been touched
in 5 minutes, whichever comes first. **Exception**: a session sitting on
`waiting` (amber) is never pruned by the 5-minute timer, deliberately —
nothing else refreshes that file while you're away from an unanswered
prompt, so timing it out would hide the orb exactly when it's trying hardest
to get your attention. If a session gets Ctrl+C'd right at a prompt, its
orb will sit there indefinitely; right-click → "Reset this session to idle"
clears it manually, after which the normal 5-minute rule applies.

**Scope**: this only tracks Claude Code sessions that read a `settings.json`
you've wired up per step 2 below. Each Claude Code install — WSL (per Linux
user), native Windows, macOS — has its own, unrelated `settings.json`, so a
session won't show up until you add the matching hooks to *its* config.
The app itself doesn't care where a status file came from. On Windows, both
WSL and native Windows hooks ultimately run `powershell.exe` as a normal
Windows process, so `$env:TEMP` resolves to the same real folder either way
and their orbs happily stack together in one running `ClaudeBuddy.exe`.
This is just a matter of wiring more hook configs, not a hard limitation —
a different WSL user's install is the one combination left unwired, since
that would need hooks added inside *their* Linux user account.

## 1. Build and launch it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer,
on either platform.

### macOS — build the app bundle

```bash
./tools/build-macos-app.sh             # -> dist/Claude Buddy.app
./tools/build-macos-app.sh --install   # ...and copy it to /Applications
./tools/build-macos-app.sh --rid osx-x64   # cross-build for Intel
```

Then launch it like any other app: double-click it in Finder, or

```bash
open "dist/Claude Buddy.app"      # or: open -a "Claude Buddy"
```

Nothing appears in the Dock and nothing opens a window — **look for the orb
in the menu bar**, that's the app running. Quit it from that menu.

The bundle is worth using over the loose binary for reasons beyond
double-clickability: it's `LSUIElement`, so macOS itself treats it as a
menu-bar app; it declares `NSAppleEventsUsageDescription`, without which
macOS won't even offer the Automation prompt that click-to-focus depends on;
and it has a stable code identity, so that Automation grant attaches to
"Claude Buddy" rather than to whichever terminal launched a bare binary.
It's ad-hoc signed, which means each rebuild changes the signature and macOS
may ask for Automation permission again — expected, not a bug.

### Windows (and the loose-binary route on macOS)

```
dotnet publish -c Release -r win-x64     # Windows
dotnet publish -c Release -r osx-arm64   # macOS on Apple silicon
dotnet publish -c Release -r osx-x64     # macOS on Intel
```

The binary lands in `bin/Release/net8.0/<rid>/publish/ClaudeBuddy` (`.exe`
on Windows) — it's self-contained, so you can copy that one file anywhere
(e.g. a `Tools` folder) and run it without needing .NET installed
separately. For local hacking on either platform, plain `dotnet run` works
too.

Run it once to sanity-check: until a session writes a status file you should
see **zero orbs** and a slate-colored status-bar icon whose menu says "No
Claude Code sessions" — that's correct, not broken. Left-click-drag an orb
to reposition it once one appears; dragging is only honored until the next
time a session is added or removed, at which point the whole stack reflows
back to its default layout.

The icons are generated, not checked in as hand-drawn art — rerun
`python3 tools/make-icons.py` (stdlib only) after editing it to regenerate
`Assets/`.

## 2. Wire up Claude Code

Each Claude Code install you want tracked (WSL, native Windows, macOS, ...)
needs its own copy of these hooks added to *its own* `settings.json` —
installs don't share config. Repeat this section once per install.

Pick the snippet that matches where the Claude Code session you're wiring
up actually runs, then open **that install's** `~/.claude/settings.json`
(create it if it doesn't exist) and merge in the snippet's contents:

- **Claude Code on macOS** → `claude-hooks-snippet-macos.json`. First copy
  the hook script into place and make it executable:
  ```bash
  mkdir -p ~/.claude/claude-buddy
  cp ClaudeBuddyHook.sh ~/.claude/claude-buddy/
  chmod +x ~/.claude/claude-buddy/ClaudeBuddyHook.sh
  ```
  The snippet references it via `$HOME`, so there's no username to replace.
- **Claude Code running inside WSL** → `claude-hooks-snippet-wsl.json`.
  Copy `ClaudeBuddyHook.ps1` to a local Windows folder (e.g.
  `%LOCALAPPDATA%\ClaudeBuddy\`) and replace every `<YOUR_USERNAME>` in the
  snippet with your Windows username. `~/.claude/settings.json` here means
  the Linux user's home directory (e.g. `/home/<user>/.claude/settings.json`)
  — a completely separate file from any Windows-side config.
- **Claude Code installed natively on Windows** (not through WSL) →
  `claude-hooks-snippet-windows.json`. Same `ClaudeBuddyHook.ps1` copy and
  `<YOUR_USERNAME>` replacement; `~/.claude/settings.json` here means
  `C:\Users\<YOUR_USERNAME>\.claude\settings.json`. One copy of the .ps1 is
  enough for both Windows-side variants — every install's hooks can point
  at the same file.

All snippets do the same thing and differ only in how they invoke the hook
script (see the platform notes below) — the hook logic, matchers, and
states are identical.

**If you already have a `hooks` block with other events in it**, don't
replace the whole thing — add `SessionStart`, `Notification`,
`UserPromptSubmit`, `PreToolUse`, `Stop`, and `SessionEnd` as sibling keys
inside your existing `hooks` object, and if you already have any of those
six keys, append these entries to their existing arrays instead of
overwriting them.

What each hook does — every one of them invokes the hook script with a
state (`idle`, `generating`, or `waiting`), which reads `session_id` and
`cwd` off the hook's own stdin JSON and writes/updates that session's
status file:
- **`SessionStart`**: fires when a Claude Code session starts (including
  `/clear` and `/compact`, which re-fire it) → `idle`, so the orb appears
  right away instead of waiting for the first prompt or tool call.
- **`UserPromptSubmit`**: fires when you send Claude a message → `generating`.
- **`PreToolUse`** (matcher `.*`, all tools): fires right before any tool
  call, including the moment right after you approve a permission
  prompt → `generating`, keeping the orb violet through multi-step tool use.
- **`Notification`** (matchers `permission_prompt` and
  `elicitation_dialog`): fires when Claude is genuinely blocked on you —
  a tool-approval dialog, or an interactive question tool (like
  `AskUserQuestion`) waiting for your answer → `waiting`. There's also an
  `idle_prompt` matcher (fires whenever Claude finishes a turn and is
  waiting for your *next free-form message*, approval-related or not) —
  deliberately left out here since it fires constantly and isn't a
  reliable "needs you" signal; add it back to the `Notification` array if
  you'd rather have that broader behavior.
- **`Notification`** with matcher `elicitation_complete`: fires right
  after you answer an interactive question → `generating`, so the orb
  doesn't stay stuck amber while Claude processes your answer (there's no
  `PreToolUse` between answering and Claude resuming, so without this the
  gap would show amber even though Claude's already back to work).
- **`Stop`**: fires when Claude's turn is fully done (no more tool calls,
  nothing pending) → `idle`.
- **`SessionEnd`**: invokes the script with `ended`, which **deletes** the
  session's status file (rather than writing to it) so its orb disappears
  immediately on a clean exit. It's a nice-to-have, not the primary cleanup
  mechanism — it's documented as unreliable on ungraceful termination
  (Ctrl+C notably; the hook gets cancelled before it can run), so the app
  still prunes stale files as a fallback (see `StaleAfter` in
  `SessionManager.cs`, and the "waiting is never pruned" note above).

Run `/hooks` inside Claude Code afterward to confirm all six events are
registered — do this separately for each install, since `/hooks` only
shows the config for the session you run it in.

### Platform notes

**macOS**: the hooks call `bash` with the script's absolute path — nothing
else needed. The script writes to `$TMPDIR/claude_buddy/`, which is the
same per-user folder .NET's `Path.GetTempPath()` returns, so the app and
hooks agree automatically. No `jq` dependency; the script extracts
`session_id`/`cwd`/`transcript_path` with `sed`, and the chat name and color
with `grep`.

**WSL, chat names and colors**: the PowerShell hook reads the same transcript
records as the bash one, but a WSL session's `transcript_path` is a Linux path
that `powershell.exe` can't open, so those orbs keep the folder-name fallback
and the plain border. Native Windows sessions get both normally.

**Encoding, on Windows PowerShell 5.1 specifically**: the hook reads the
transcript with `-Encoding UTF8` and writes its status file with
`[System.IO.File]::WriteAllText` and a no-BOM `UTF8Encoding`, rather than
`Get-Content`/`Set-Content` defaults. Both are load-bearing and were caught on
a real Windows box: 5.1 reads UTF-8 as the ANSI codepage (turning `café` into
`cafÃ©`) and writes ANSI on the way out (turning it into `caf?` — actual data
loss, since chat names carry em dashes and accents far more often than paths
do). The BOM matters too: `System.Text.Json` treats a leading BOM as an
invalid start of value, so a BOM would make the app skip the file and drop
that orb entirely. PowerShell 7 defaults are already correct; being explicit
is right on both.

**WSL** (hooks execute via a Linux shell that then calls out to Windows):
`claude-hooks-snippet-wsl.json` uses `powershell.exe`'s full path
(`/mnt/c/WINDOWS/System32/WindowsPowerShell/v1.0/powershell.exe`) plus
`-ExecutionPolicy Bypass` — both load-bearing, not stylistic:
- **Full path, not just `powershell.exe`**: hook commands run in a
  stripped-down environment that doesn't include the Windows PATH
  entries WSL normally injects into interactive shells, so a bare
  `powershell.exe` can't be found.
- **`-ExecutionPolicy Bypass`**: without it, running a `.ps1` file (as
  opposed to an inline `-Command` string) can hit `AuthorizationManager
  check failed` depending on the machine's default execution policy and
  the script's location/zone.

**Native Windows** (hooks execute directly as a Windows process, no Linux
shell in between): `claude-hooks-snippet-windows.json` calls plain
`powershell.exe` — it's already on the native Windows PATH, so no
`/mnt/c/...` prefix is needed or correct here (that path doesn't exist
outside WSL). `-ExecutionPolicy Bypass` is still needed for the same
reason as WSL.

Both Windows-side variants land in the same real `%TEMP%\claude_buddy\`
folder, since `powershell.exe` resolves `$env:TEMP` to the actual Windows
temp directory regardless of which shell launched it — so a WSL session
and a native Windows session can run side by side and show up as two
independent orbs in the same `ClaudeBuddy.exe`.

These symptoms (and an earlier WSL-only one from before this script
existed — unescaped `$env:TEMP` getting mangled by the outer Linux shell
before PowerShell ever saw it) all look identical from the outside: the
hook fires, but the status file never updates and the orb never reacts. If
you suspect a hook isn't actually reaching the script, temporarily add a
throwaway sibling hook to confirm the hook itself is firing before
debugging further downstream — `echo fired >> /tmp/some.log` on
WSL/macOS, or `cmd.exe /c echo fired >> %TEMP%\some.log` on native
Windows.

## 3. (Optional) Launch it automatically

- **Windows**: press `Win+R`, type `shell:startup`, and drop a shortcut to
  `ClaudeBuddy.exe` in the folder that opens.
- **macOS**: install the bundle (`./tools/build-macos-app.sh --install`),
  then System Settings → General → Login Items → **+** → pick
  **Claude Buddy** from /Applications.

It'll then start quietly whenever you log in.

## Notes / things you might want to tweak

- **Chat names and colors**: both hook scripts pull the newest
  `custom-title` / `ai-title` / `agent-color` records out of the session's
  transcript (`transcript_path`, straight off the hook payload) and record
  them as `title` and `color`. All three come from one read of the file's
  tail, with a full scan only as a fallback for when a long run of tool output
  has pushed them all out of that window — this runs on every tool call, so it
  stays cheap (~15 ms on a 4 MB transcript). If Claude Code ever changes those
  records' shape, the matches simply fail and everything falls back to folder
  names and the plain orb. Consumers: `OrbWindow.UpdateFrom` (glyph, tooltip,
  context menu), `OrbWindow.ApplyAccent` (border + letter color) and
  `TrayController.DisplayName`.
- **Color palette**: `AgentColors` at the top of `OrbWindow.axaml.cs` maps
  `/color` names to hex. Claude Code renders its accents as xterm-256 indices,
  so these are the matching cube values — but only `green` (index 35) and the
  two auto-assigned accents seen in other sessions (37 teal, 175 pink) are
  confirmed; the rest are same-band guesses for their hue. To correct one,
  set that color in a session and read the escape sequence Claude Code emits:
  `tmux capture-pane -p -e | grep -o $'\033\[38;5;[0-9]*m'`. An unrecognized
  name (one added to Claude Code later) falls back to the plain border and
  white letter, so add a line there rather than expecting a crash.
- **Colors and animation**: `OrbWindow.axaml.cs` has `IdleColor` /
  `GeneratingColor` / `WaitingColor` at the top, and the breathing/pulse
  timings live in `ApplyState()` / `StartPulse()` — easy to retune speed,
  scale, or swap in different colors.
- **Stacking layout and staleness**: `SessionManager.cs` has the stacking
  math (`ReflowPositions()`) and the `StaleAfter` constant (5 minutes)
  that controls how long an idle/generating session's orb sticks around
  before being pruned — `waiting` is exempt, see above.
- **macOS + Spaces**: orbs follow you across Spaces and show alongside
  full-screen apps. Avalonia doesn't expose `NSWindow.collectionBehavior`,
  so `MacOSWindowExtensions.cs` sets it (`canJoinAllSpaces` +
  `fullScreenAuxiliary`) through the native window handle when each orb
  opens — that's the file to tweak if you'd rather orbs stay put.
- **Status-bar icon and menu**: `TrayController.cs`. Two things there are
  load-bearing rather than stylistic: its single `NativeMenu` is repopulated
  in place (assigning a *new* `NativeMenu` to an already-exported `TrayIcon`
  throws "The menu being updated does not match" on macOS), and the menu is
  only rebuilt when a signature of the session list actually changes —
  otherwise the 2-second poll would dismiss the menu while you're reading
  it. Icon art comes from `Assets/tray-*.png`, drawn by
  `tools/make-icons.py`. The Claude Desktop section folds its own digest into
  that same signature, and additionally holds rebuilds back while the menu is
  open (`NativeMenu.Opening` / `Closed`), since submenus make people linger.
  The tray *icon* is never held back — it's the urgent half.
- **Claude Desktop profiles**: `ClaudeDesktopManager.cs` (discovery, the
  process scan, launch/quit/reveal), `ClaudeDesktopSection.cs` (the menu
  block), `MacOSProcessScan.cs` (libproc + `sysctl`), `MacOSAppActivation.cs`
  (`NSRunningApplication`). `TrayController` calls two methods on the section
  and knows nothing else about it, so removing the feature is a small revert
  plus deleting those four files.
- **Bundle metadata**: `tools/build-macos-app.sh` writes `Info.plist`
  inline — bundle id, version, `LSUIElement`, and the Automation usage
  string all live there.
- **Click-to-focus coverage**: `TerminalFocuser.cs` maps what the hook
  scripts record (`term_program`, iTerm session UUID, tty, tmux socket/pane
  on macOS; `term_pid` on Windows) to an AppleScript that selects the right
  window, an `open -a` activation, or a `SetForegroundWindow` call. Adding a
  terminal only means adding a case if you want *exact tab* selection for it
  — plain activation already works for anything that lives in an `.app`.
  Focus work runs on a background thread (it shells out and waits), so a
  click can't stall the orb animations.
- **Sound**: no audio right now, purely visual per your original ask. If
  you later want a soft sound on the waiting transition, that's one line
  in `OrbWindow.ApplyState()` — e.g. shell out to `afplay` on macOS or
  play a system sound on Windows.
