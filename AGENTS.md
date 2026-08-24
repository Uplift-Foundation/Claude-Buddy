# Working in this repository

Notes for Codex (and anyone else) working here. The README is the real
documentation — how the app works, how the hooks work, how to build installers.
This file only covers *how work gets landed*, which the code can't tell you.

This is the same content as `CLAUDE.md`, which Claude Code reads. Two files
because the two CLIs each look for their own name and neither reads the other's;
**if you change one, change both.** They are short enough that keeping them in
step by hand is cheaper than any mechanism for it, and a symlink would be a
trap — it looks like one file until someone checks out the repo on Windows.

## How a feature gets built

**Every feature is a ticket, a team, and a round trip through QA — in that
order.** The backlog is Jira project **CB**, board 70:

<https://uplifttech.atlassian.net/jira/software/projects/CB/boards/70/backlog>

No feature work starts from a chat message alone. A request becomes a CB ticket
first, and from then on the ticket is what moves, so someone reading the board
and someone reading the terminal see the same thing.

**Prefer the autonomous path at every step.** The escalations below are for an
agent that is genuinely not confident, not checkpoints to hit out of habit. An
agent that can defend its plan, its tests and its screenshots should carry the
feature to done; one that cannot should ask early rather than after building the
wrong thing.

### The team

Features are built by an **agent team**, not one session doing everything — the
same mechanism this app draws orbs for, with each member spawned as its own
`claude` process carrying `--agent-name`, `--team-name` and
`--parent-session-id`, the last of which `AgentTeam.cs` reads off the process
to link a member to its lead. Building Claude Buddy with the thing Claude Buddy visualises is
deliberate: a team that looks wrong on the board is a free bug report.

- **Product manager** — writes the requirement, owns the ticket's status, calls
  done.
- **Architect** / **engineer** — build a refined ticket. As many of each as the
  work genuinely splits into, one per independent surface.
- **QA** — tests what they build and hands failures back. A loop, not a final
  gate: build a piece, test a piece, send it back, until nothing comes back.

### Product manager: requirement first, then plan

The PM agent creates the requirement in CB as a **`Feature`** and leaves it in
**Refinement**, which is where CB's workflow puts a new issue anyway. CB's issue
types are `Epic`, `Feature`, `Story`, `Task`, `Bug`, `Subtask` (one word);
`Feature` is level with `Story`, not above it, so an `Epic` still groups a
multi-ticket effort.

**CB's board has four columns, and they are now confirmed** — read off CB-1, the
first ticket filed, which is what this paragraph used to ask for. They are
**Refinement → Development → Testing → Done**, with transition ids 11, 21, 31 and
41; every transition is global, so any status can be reached from any other. The
name this file guessed was "Refining", which is a status on project FMN and not
one CB has: a team-managed project owns its workflow separately, and the guess
was wrong. Nothing else here needed correcting.

Then it writes a plan, and **plans on a stronger model than the one that
implements**. Planning is where a wrong call is cheapest to fix and most
expensive to miss:

| Feature | Plan with | Implement with |
| --- | --- | --- |
| Complex — new subsystem, cross-platform surface, anything touching the hook or transcript contracts | `fable` | `opus` or `sonnet` |
| Simpler — a setting, a bounded UI change, a fix with an obvious shape | `opus` or `sonnet` | `sonnet` |

Those are the `model:` values the Agent and Workflow tools accept; `effort:` is
the other dial and moves the same way — high while planning, lower for
mechanical stages.

**If the PM isn't confident the requirement and plan support autonomous
implementation, it asks a human before spending a team.** Ask on GitHub with a
link to the CB ticket, and — this repo is public, the Jira site is not — enough
of the requirement inline that the question stands alone for someone who can't
open the link. Repo admins are the escalation point (`wtvamp`, `lunarjuice` at
time of writing; `gh api repos/Uplift-Foundation/Claude-Buddy/collaborators` is
the live list). If it *is* confident, it hands straight to the architects and
engineers and no human is in the loop.

### Build and QA, in a loop

Engineers and architects build, QA tests, back and forth until it stops coming
back. QA's half is **Every feature ships with its tests** below — unit,
integration and UI covering 100% of the lines the branch adds — so "QA passed"
means those are green, not that somebody had a look.

**A feature works on Windows and macOS or it is not a feature.** CI enforces the
shape: `macos-latest`/`osx-arm64` and `windows-latest`/`win-x64`, every suite on
both legs. Parity is more than a green build, though — a feature no install path
wires up is equally unfinished, so check `tools/build-macos-dmg.sh`,
`tools/build-macos-app.sh`'s Resources copy, `tools/ClaudeBuddy.iss`,
`tools/install-hooks.sh`/`.ps1` and the README install section, and confirm any
platform gate is real (WSL) rather than accidental.

### PR, screenshots, and the call on done

The feature goes up as a PR against `develop`, per **Pull requests** below.

**Don't attach screenshots by hand — they're already automatic.** `ci.yml`
captures a PNG per `tests/UiScreenshots` scenario on both runners, and
`publish-screenshots.yml` picks them up on a `workflow_run` trigger, pushes them
to the `screenshots` branch and comments on the PR with real
`raw.githubusercontent.com` URLs labelled per rid. (Two workflows because a
`pull_request` run from a fork gets a read-only token, and every PR here is one.)
A feature with a visible surface adds its capture there; that comment is what
gets reviewed.

The PM agent reviews it — **both** rids, since that is exactly where a
macOS-only implementation shows itself — and **if it can approve the feature as
done autonomously, it should**, moving the ticket accordingly.

If it cannot, it asks a human to pull, install and approve by hand: **in the
terminal** if someone is driving the feature in Claude Code, **on the PR** if
nobody is watching. Either way name the specific thing the screenshots could not
settle — "can't tell whether the mic button is enabled on Windows" is actionable
in a minute; "needs manual approval" is not.

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

That includes the tests — see **Every feature ships with its tests** below. A PR
that adds or changes behaviour without covering it isn't ready to review,
however well the behaviour itself works, and a line the tests can't reach is
named in the body rather than left for the next person to discover.

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

## Every feature ships with its tests

**A feature request is not finished when the feature works. It is finished when
the feature works and the code that makes it work is covered.** Tests land in
the same branch as the behaviour, not a follow-up, and the target for the lines
that branch adds or changes is **100%** — every new method, every arm of every
new conditional, every error path you wrote a `catch` for.

Cover all three levels the suites already separate, not just the one nearest to
where the change happens to live:

- **Unit** (`tests/UnitTests`) — the decisions. A new rule about which orb wins,
  which name is used, or where something is drawn belongs in a function with no
  window and no settings behind it, with a case per outcome. Logic that can't be
  reached without constructing a window is a seam to fix first, not a test to
  skip — the same argument that keeps `OrbArrangement`, `OrbGlyph` and both
  transcript parsers pure.
- **Integration** (`tests/IntegrationTests`) — the seams with what this process
  does not own: hook scripts as real subprocesses, files on disk, settings
  round-tripped through a real file. A format someone else defines is covered
  here *as well as* by a unit test of the parsing; the two fail differently.
- **UI** (`tests/UiTests`) — the headless Avalonia path. A new window, panel,
  control, binding or click handler is driven with a synthesized click or a real
  `UpdateFrom` and asserted on what a user would have seen. `FakeChatSession` is
  the pattern for anything needing a live session behind it. A new *visible*
  surface also gets a hand-written capture in `tests/UiScreenshots` — real Skia
  rather than the null renderer, and its cases don't follow `UiTests`
  automatically.

A change to geometry, transcript parsing or orb initials extends the three
console suites too — `dotnet test tests/Tests.sln` does not run them. CI runs
every suite on both runners, so a test that only passes on your machine blocks
the build.

**100% means the diff, not the repository.** The app is nowhere near it — as of
this writing `UnitTests` alone covers about 3% of the assembly, and whole areas
(`TerminalFocuser`, the tray, the installers) make real `tmux`/`ps`/`osascript`
calls a headless runner must not execute. Holding new work to 100% is how that
number climbs without a rewrite; holding the existing repository to it would
make the rule something everybody quietly ignores.

Where a line genuinely cannot be covered — an OS call with no seam, a `catch`
for something only the other platform throws — **name it in the PR body and say
why**, in the same voice that separates "confirmed on a real machine" from
"assumed to work". A named uncovered line is a known gap; an unnamed one is a
claim about the suite that isn't true.

To see the number:

```bash
dotnet add tests/UnitTests/ClaudeBuddy.UnitTests.csproj package coverlet.collector
dotnet test tests/UnitTests --collect:"XPlat Code Coverage"
# tests/UnitTests/TestResults/<guid>/coverage.cobertura.xml
```

**Nothing in the repository collects coverage today, and the flag fails silently
without that package** — `--collect:"XPlat Code Coverage"` against a project
with no `coverlet.collector` reference creates an empty `TestResults/`, prints
no warning and exits 0, so a run that measured nothing reads exactly like one
that measured everything. Add the package to the suite you are measuring and
take it back out before you push, unless you are deliberately wiring collection
into all three suites and CI, which is its own change.

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
every suite — including `tests/UiScreenshots`, which renders through real
Skia rather than the null renderer — on both runners, before packaging.

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
`coverlet.collector`, while `UiTests` and `UiScreenshots` run on the Microsoft
Testing Platform (xUnit v3, forced by `Avalonia.Headless.XUnit` 12.x) where
VSTest collectors do not apply, so they use
`Microsoft.Testing.Extensions.CodeCoverage` — **pinned to 17.14.2** in both,
because 18.x wants `Microsoft.Testing.Platform` 2.x while xunit.v3 3.2.2 brings
`mtp-v1`, and the mix throws `TypeLoadException` for `IDataConsumer` before a
test runs. `tools/merge-coverage.py` then unions the four cobertura files, which
all measure the same assembly; summing them would double-count the denominator
and undercount the numerator at the same time.

`UiScreenshots` counts as of CB-3 and did not before. CI always ran it, and it
is the only suite drawing through **real Skia** rather than the null renderer, so
a few things are reachable only there — a bitmap actually written to disk most
obviously (`ClaudeDesktopBundles.WriteTinted`). Leaving it out meant those lines
were verified and counted nowhere.

The three console suites still contribute nothing *as suites* —
`ArrangementTests`, `GlyphTests`, `TranscriptTests` are plain exes, not test-SDK
projects. Their **cases** do count now: CB-3 moved each matrix into a class that
`UnitTests` compiles in and runs (`ArrangementSweep`, `GlyphSuite`,
`TranscriptSuite`), so `OrbArrangement` no longer reads 0% while being the most
exhaustively verified file here. Run the exes for the grouped failure report.

Any new UI test class that reads or writes `ClaudeBuddySettings` goes in
`[Collection("Settings")]`. The settings model is a process-wide static that
almost everything visual reads while being constructed, so parallel classes race
to a different set of *executed lines* rather than to a failure — three runs of
an identical binary once reported 1914, 2024 and 1914 covered lines in
SettingsWindow.cs. See `tests/UiTests/SettingsCollection.cs`.

And read the headline as coverage **of what remains**: an excluded file and a
deleted one look identical in a report. `merge-coverage.py` reads the attributes
back out of the sources and prints what was held out, what is excluded inside
measured files, and what is absent for no stated reason.

The two engines also disagree about `[ExcludeFromCodeCoverage]` on a *method*.
coverlet honours it; `Microsoft.CodeCoverage` (both MTP suites) instruments the
body anyway and reports it unhit. Both honour it on a *class*, which is how the
gap survived until CB-3 had 157 member-level exclusions for it to show up in. The
merge is therefore **not** a union: coverlet decides which lines exist, and the
MTP reports only contribute hits for those. Undo that and an exclusion on a method
silently stops meaning anything.

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
