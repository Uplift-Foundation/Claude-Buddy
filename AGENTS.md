# Working in this repository

Notes for Codex (and anyone else) working here. The README is the real
documentation — how the app works, how the hooks work, how to build installers.
This file only covers *how work gets landed*, which the code can't tell you.

This is the same content as `CLAUDE.md`, which Claude Code reads. Two files
because the two CLIs each look for their own name and neither reads the other's;
**if you change one, change both.** They are short enough that keeping them in
step by hand is cheaper than any mechanism for it, and a symlink would be a
trap — it looks like one file until someone checks out the repo on Windows.

## Branching: gitflow

Two long-lived branches:

- **`main`** — released code. Tagged releases come from here. Nothing is
  committed to it directly.
- **`develop`** — the integration branch. Finished work lands here first and
  sits until it's part of a release.

Everything else is short-lived and **named for what it is**:

| Prefix | For | Branches from | Merges to |
| --- | --- | --- | --- |
| `feature/` | new or changed behaviour | `develop` | `develop` |
| `bugfix/` | fixing something that is wrong but not yet released | `develop` | `develop` |
| `release/` | preparing a version (bump, notes, packaging fixes) | `develop` | `main` **and** `develop` |
| `hotfix/` | a fix for released code that can't wait | `main` | `main` **and** `develop` |

**`bugfix/` and `hotfix/` are not a severity judgement — they say where the fix
starts.** A bug that only exists on `develop`, or one that is shipped but can
wait for the next release, is a `bugfix/` off `develop`. A `hotfix/` branches
off `main` because it has to reach released users without waiting for whatever
else is sitting on `develop`. If you're unsure which, ask what a user on the
latest release experiences today: if the answer is "nothing wrong yet", it's a
`bugfix/`.

Name the rest of the branch after the change, not the issue number:
`feature/persist-orb-positions`, not `feature/pr-12`.

## Pull requests

Open every PR **against `develop`** — `feature/` and `bugfix/` both — unless
it's a `release/` or `hotfix/` branch, which target `main`. The repository's
default branch is still `main`, so GitHub will offer the wrong base — pass it
explicitly:

```bash
gh pr create --repo Uplift-Foundation/Claude-Buddy --base develop \
  --head <branch> --title "..." --body "..."
```

Say in the PR body what was actually verified and what wasn't. This project
keeps that distinction deliberately — see `docs/*-findings.md`, which separate
"confirmed on a real machine" from "assumed to work". A PR that claims more than
was tested costs more than one that admits a gap.

**Don't rename a pushed branch to fix its name.** GitHub closes the open PR
instead of retargeting it. Get the prefix right before the first push.

## Remotes

`Uplift-Foundation/Claude-Buddy` is the canonical repository. **Which remote
name points at it varies by clone** — run `git remote -v` rather than assuming.
Clones from the canonical repo call it `origin`; clones from the
`wtvamp/Claude-Buddy` fork call the fork `origin` and the canonical repo
`upstream`.

## Commits

Messages here are prose, not changelog lines: a short summary in the
imperative, then paragraphs explaining **why** the change is right and what was
considered and rejected. Comments in the code follow the same habit — read a
few (`OrbWindow.axaml.cs`, `SessionManager.cs`) before writing new ones, and
match the density rather than stripping or padding it.

## Build and run

```bash
dotnet build                       # quick compile check
dotnet run                         # run the loose binary (no bundle)
./tools/build-macos-app.sh         # "Claude Buddy.app" into dist/
./tools/build-macos-app.sh --install   # ...and copy to /Applications
```

When installing over an existing `/Applications/Claude Buddy.app` on macOS,
build **signed**:

```bash
MACOS_SIGNING_IDENTITY="Developer ID Application: UPLIFT FOUNDATION (5AQ4ULRG3Z)" \
  ./tools/build-macos-app.sh --install
```

macOS ties the Automation (Apple Events) consent to the app's code identity, so
an ad-hoc build silently breaks click-to-focus until the user re-approves it in
System Settings — and that failure is invisible.

`<Version>` in `ClaudeBuddy.csproj` is the single source of truth for the
shipped version.

## Testing

Orb *geometry*: `dotnet run --project tests/ArrangementTests`. It walks every
shape at every end of the spacing slider across a range of orb counts and team
shapes on three screen sizes, and asserts that nothing leaves the work area,
nothing is drawn on top of anything else, and no team member ends up too far
from its lead to be read as one. Run it after any change to `OrbArrangement`.

Transcript and dialog parsing: `dotnet run --project tests/TranscriptTests`. It
covers `ChatTranscript` (Claude Code's JSONL, and reading a permission dialog
off a captured tmux pane) and `CodexTranscript` (Codex's rollout JSONL).

Both parsers read formats nobody here controls, and both fail *quietly*: a
mis-mapped transcript silently drops a message, and a mis-read dialog puts a
button on screen that presses something other than what it says. **Write
fixtures from real output, not from memory** — the dialog parser was first
written against an invented fixture and failed on every real dialog. To capture
one:

```bash
tmux capture-pane -p -t %<pane> > /tmp/pane.txt
dotnet run --project tests/TranscriptTests -- /tmp/pane.txt   # or a .jsonl
```

Both CLIs write `.jsonl`, so the harness picks its parser from the file's first
row rather than its extension and says which one it used. If that line names the
wrong CLI, nothing below it means anything.

Orb *initials* have one too — `dotnet run --project tests/GlyphTests`. It covers
`OrbGlyph`: the two letters an orb wears and the ones the chat panel's header
wears beside it, across kebab and snake case, all three dashes, leading
punctuation, emoji, and the single-letter setting. It also covers
`ChatSpeaker`, which decides *whose* letters those are on a message bubble —
the agent in the session key, or the panel's title for a terminal session, and
never "nobody" once a name is known.

It exists because the initials were wrong for a year and nobody saw it. Every
kebab-case name drew two letters off the front of its first word, so
`claude-buddy` was "Cl" and not "Cb" — invisible partly because it only looks
wrong when the halves start with different letters, and partly because reading
the answer meant looking at the screen. Same rule as the geometry: `OrbGlyph` is
pure and takes the two-letter *setting* as an argument rather than reading it,
so the tests do not depend on the machine they run on.

## The automated suite

Three xUnit suites — `tests/UnitTests`, `tests/IntegrationTests`,
`tests/UiTests` — join the three above rather than replacing them: one
command, `dotnet test tests/Tests.sln`, runs all three; `claudeBuddy.sln`
stays app-only and `Tests.sln` holds only these, so neither `dotnet build`
nor `dotnet test` at the root can trip over the other's projects. CI runs
all six suites, on both runners, before packaging.

They reference `ClaudeBuddy.csproj` with a `<ProjectReference>` rather than
`<Compile Include>`-ing individual files, because `SessionManager` and
`ClaudeBuddySettings` have dependency closures too large for that — and
`<InternalsVisibleTo>` in `ClaudeBuddy.csproj` grants exactly these three
assemblies visibility into anything `internal`.

`UnitTests` covers more pure logic the same way as `ArrangementTests`/
`GlyphTests`, most valuably `SessionManager`'s `Superseded` and
`InheritTerminalInfo` — the rules deciding which status file is the live
orb and which sibling donates terminal coordinates to one that has none.
`IntegrationTests` runs `ClaudeBuddyHook.sh`/`.ps1` as real subprocesses,
asserting the invariant Codex depends on (exit 0, empty stdout/stderr — its
hook stdout is parsed as strict JSON and exit 2 means deny), plus
`TranscriptReader`'s tail/truncation rules and `ClaudeBuddySettings`'
unknown-key round-trip. `UiTests` runs headless via `Avalonia.Headless.XUnit`,
using the real `App` class as its own host (its
`OnFrameworkInitializationCompleted` guard is never true under a headless
lifetime, so the mutex/`SessionManager.Start()`/tray body never runs) —
covers `OrbFlyout`'s real clicks, `OrbWindow.UpdateFrom`, and `ChatPanel`
driven by `FakeChatSession` (an in-memory `IRemoteChatSession`, the reason
that interface exists per its own header comment). It never synthesizes a
click on an orb itself: that reaches `TerminalFocuser`, which fires real
`tmux`/`ps`/`osascript` off-thread with no OS guard at its own entry point.

All three point `CLAUDE_BUDDY_SETTINGS_DIR` at a fresh temp directory via a
`[ModuleInitializer]` before anything else runs — even constructing an
`OrbWindow` reads a colour setting in a field initializer. That env var is
checked in `ClaudeBuddySettings.Directory` before `SpecialFolder.ApplicationData`,
the same pattern as `CLAUDE_BUDDY_PROFILE_ROOT` in `ClaudeDesktopManager.cs`;
without it a test reads and writes the real settings.json.

## Coverage

`./tools/coverage.sh` for whole-app line and branch coverage;
`./tools/coverage.sh --base upstream/develop` adds coverage of just the lines
you added, which is the figure that actually says whether new code is tested —
a file-level percentage is dominated by whatever was already there.

Two collectors, deliberately: `UnitTests`/`IntegrationTests` are VSTest and use
`coverlet.collector`, while `UiTests` runs on the Microsoft Testing Platform
(xUnit v3, forced by `Avalonia.Headless.XUnit` 12.x) where VSTest collectors do
not apply, so it uses `Microsoft.Testing.Extensions.CodeCoverage` — **pinned to
17.14.2**, because 18.x wants `Microsoft.Testing.Platform` 2.x while xunit.v3
3.2.2 brings `mtp-v1`, and the mix throws `TypeLoadException` for
`IDataConsumer` before a test runs. `tools/merge-coverage.py` then unions the
three cobertura files, which all measure the same assembly; summing them would
double-count the denominator and undercount the numerator at the same time.

The number excludes the three console suites — `ArrangementTests`,
`GlyphTests`, `TranscriptTests` are plain exes, not test-SDK projects — so
`OrbArrangement` reads 0% while being the most exhaustively verified file here.
It is "coverage from the xUnit suites", not the sum of what this repo verifies.

## Extra accounts

Both CLIs support a second account through an environment variable —
`CLAUDE_CONFIG_DIR` and `CODEX_HOME` — and each is a separate config file that
the default wiring does not touch. The app keeps a list per CLI
(`claudeCodeProfileDirs`, `codexHomes`), the Settings window edits them on both
platforms, and the installers read them so a repair or uninstall covers every
account rather than just the default one.

This was Windows-only until it wasn't, on the reasoning that "neither concept
exists on macOS". Only the WSL half of that was ever true, and the gap was
invisible: macOS already *consumed* the list in `TranscriptReader` and offered
no way to fill it in, so a second account got wired once by hand if at all and
was never maintained afterwards.

## Codex specifically

`docs/codex-findings.md` is the reference for everything measured about the
Codex CLI — the rollout format, the hook events and their payloads, the TUI's
approval prompt, and a clearly separated list of what has *not* been verified. Read it before changing
`CodexTranscript.cs`, `ClaudeBuddyHook.sh` or `tools/install-codex-hooks.sh`,
and add to it rather than to a commit message when you measure something new.

Two things about Codex support are worth knowing before you are surprised by
them:

- **A Codex orb's name comes from Codex, not from the rollout.** Its transcript
  carries no title record, but `$CODEX_HOME/state_<n>.sqlite` has a `threads`
  table holding both `name` (what `/rename` set) and `title` (Codex's own, from
  your first message). The hook prefers the first, exactly as the Claude Code
  path prefers `/rename` over an auto-title, and falls back to reading the first
  message out of the rollout for the brief window before a thread is written to
  the database. There is no `/color` equivalent, so a Codex orb keeps the plain
  ring. Two sessions started with the same first message in different
  directories get the same title, which is why the working directory is part of
  the position key and not just the title.
- **Codex will not run a hook it has not been told to trust.** A `hooks.json`
  written by anything other than Codex itself starts out untrusted, and editing
  one later changes its hash and asks again. Until it is trusted no hook fires,
  no orb appears, and nothing anywhere reports why.
