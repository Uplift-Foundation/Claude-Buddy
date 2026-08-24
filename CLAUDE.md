# Working in this repository

Notes for Claude Code (and anyone else) working here. The README is the real
documentation — how the app works, how the hooks work, how to build installers.
This file only covers *how work gets landed*, which the code can't tell you.

`AGENTS.md` is the same content for Codex, which does not read this file. Two
files because each CLI looks for its own name and neither reads the other's —
**if you change one, change both.** A symlink would look like one file until
someone checks the repo out on Windows.

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
wait for the next release, is a `bugfix/` off `develop`: it rides the normal
train and there is nothing to fix on `main`. A `hotfix/` branches off `main`
because it has to reach released users without waiting for whatever else is
sitting on `develop`, and it merges to both so the fix isn't lost at the next
release. If you're unsure which, ask what a user on the latest release
experiences today: if the answer is "nothing wrong yet", it's a `bugfix/`.

Docs and chores go on `feature/` unless they're correcting something wrong, in
which case `bugfix/` says more.

Name the rest of the branch after the change, not the issue number:
`feature/persist-orb-positions`, not `feature/pr-12`.

Releases before 0.1.2 used flat names (`release-0.1.1-beta`) and features went
straight to `main`; that's history, not a pattern to copy.

## Pull requests

Open every PR **against `develop`** — `feature/` and `bugfix/` both — unless it's
a `release/` or `hotfix/` branch, which target `main`. The repository's default
branch is still `main`, so GitHub will offer the wrong base — pass it explicitly:

```bash
gh pr create --repo Uplift-Foundation/Claude-Buddy --base develop \
  --head <branch> --title "..." --body "..."
```

Say in the PR body what was actually verified and what wasn't. This project
keeps that distinction deliberately — see `docs/*-findings.md`, which separate
"confirmed on a real machine" from "assumed to work". A PR that claims more than
was tested costs more than one that admits a gap.

**Don't rename a pushed branch to fix its name.** GitHub closes the open PR
instead of retargeting it, and reopening is refused once the old head ref is
gone; you have to open a fresh PR. Get the prefix right before the first push.

## Remotes

`Uplift-Foundation/Claude-Buddy` is the canonical repository; branches and PRs go
there. **Which remote name points at it varies by clone** — run `git remote -v`
rather than assuming:

- Clones made from the canonical repo call it **`origin`** and have no
  `upstream`, so `git fetch upstream` fails outright.
- Clones made from the **`wtvamp/Claude-Buddy`** fork call the fork `origin` and
  the canonical repo `upstream`. Older branches still track the fork; don't add
  to them.

## Commits

Messages here are prose, not changelog lines: a short summary in the
imperative, then paragraphs explaining **why** the change is right and what was
considered and rejected. Comments in the code follow the same habit — read a
few (`OrbWindow.axaml.cs`, `SessionManager.cs`) before writing new ones, and
match the density rather than stripping or padding it.

End Claude-authored commits with the `Co-Authored-By:` and `Claude-Session:`
trailers.

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
System Settings — and that failure is invisible. Same reason the bundle id is
never renamed casually; the comment at the top of `tools/build-macos-app.sh`
has the full story.

`<Version>` in `ClaudeBuddy.csproj` is the single source of truth for the
shipped version — the packaging scripts and the release workflow parse it out of
there.

## Testing UI behavior

Orb *geometry* has a test suite — `dotnet run --project tests/ArrangementTests`.
It walks every shape at every end of the spacing slider across a range of orb
counts and team shapes (nested teams, everything in one team, lead cycles, a
lead that points at nothing) on three screen sizes, and asserts that nothing
leaves the work area, nothing is drawn on top of anything else, and no team
member ends up too far from its lead to be read as one. Run it after any change
to `OrbArrangement`; it exits non-zero and prints the exact case that failed.

That exists because the arrangement was fixed by eye half a dozen times and each
fix broke a case the previous one had fixed. Keep the geometry in
`OrbArrangement` — pure, no windows, no settings — so it stays testable, with
`SessionManager` only mapping orbs onto its inputs and its answers back.

Transcript and dialog parsing has one too — `dotnet run --project
tests/TranscriptTests`. It covers `ChatTranscript`: turning Claude Code's JSONL
into chat turns, and reading a permission dialog off a captured tmux pane. It
also covers `CodexTranscript`, which does the first of those for Codex's
rollout JSONL. Same rule as the geometry — both parsers are pure, and the chat
session only decides which bytes to hand them.

Both parsers read formats nobody here controls, and both fail *quietly*: a
mis-mapped transcript silently drops a message, and a mis-read dialog puts a
button on screen that presses something other than what it says. Write fixtures
from real output, not from memory — the dialog parser was first written against
an invented fixture and failed on every real dialog. To capture one:

```bash
tmux capture-pane -p -t %<pane> > /tmp/pane.txt
dotnet run --project tests/TranscriptTests -- /tmp/pane.txt   # or a .jsonl
```

Both CLIs write `.jsonl`, so the harness decides which parser to use by looking
at the file's first row rather than its extension — hand it a Codex rollout
from `~/.codex/sessions/<yyyy>/<mm>/<dd>/` and it says so in its first line of
output. If that line names the wrong CLI, nothing below it means anything.

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

Three more suites, all xUnit rather than the bespoke console-exe pattern
above, live in `tests/UnitTests`, `tests/IntegrationTests` and `tests/UiTests`.
One command runs all three: `dotnet test tests/Tests.sln`. They join the three
suites above rather than replacing them — `Tests.sln` holds only the xUnit
projects, so it can't accidentally try to `dotnet test` an exe with no test
SDK reference, and `claudeBuddy.sln` stays app-only. CI (`.github/workflows/ci.yml`)
runs all six, on both runners, before packaging — a failing test blocks the
build the same way a failed `dotnet publish` already did.

They reference `ClaudeBuddy.csproj` directly with a `<ProjectReference>`
rather than compiling individual files in with `<Compile Include>` the way
the three suites above do. That convention holds for a file with a small
dependency closure; `SessionManager` and `ClaudeBuddySettings` do not have
one, and pulling either in that way would mean compiling most of the app a
second time. A `<ProjectReference>` also needs `<InternalsVisibleTo>` to see
anything not `public` — granted in `ClaudeBuddy.csproj` to exactly the three
new test assembly names, nothing else.

**`tests/UnitTests`** covers more of the same pure, no-window logic as the
suites above — most valuably `SessionManager`'s `Superseded` and
`InheritTerminalInfo`, the rules that decide which of several status files
for one process is the live orb and which sibling donates terminal
coordinates to one that has none. Both are `internal`, made reachable the
same way.

**`tests/IntegrationTests`** drives `ClaudeBuddyHook.sh`/`.ps1` as real
subprocesses — payload on stdin, a scratch `TMPDIR`/`-TempDir`, asserting the
one invariant everything else depends on: exit 0, empty stdout, empty
stderr, always. Codex reads a hook's stdout as strict permission-request
JSON and treats exit code 2 as a deny, so a hook that ever prints anything
starts silently refusing the user's own approvals — this is what actually
enforces the rule the hook scripts' own comments state. It also covers
`TranscriptReader`'s tail-window and truncation rules against temp `.jsonl`
files, and `ClaudeBuddySettings`' round-trip of unknown keys — the protection
against exactly the downgrade that once silently erased three settings from
a real file (see the comment on `_unknownKeys` in `ClaudeBuddySettings.cs`).

**`tests/UiTests`** runs headless, via `Avalonia.Headless.XUnit` — no display,
no window ever actually shown. It uses the real `App` class as its own test
host rather than a parallel stand-in: under Avalonia's headless lifetime,
`App.OnFrameworkInitializationCompleted`'s guard for
`IClassicDesktopStyleApplicationLifetime` is never true, so its entire body —
mutex, `SessionManager.Start()`, tray icon — never runs, with no test code
needed to arrange that. It drives `OrbFlyout` with real synthesized clicks,
`OrbWindow.UpdateFrom` against hand-built `SessionStatus` values, and
`ChatPanel` through `FakeChatSession` — an in-memory `IRemoteChatSession`,
which `RemoteChat.cs`'s own header comment says the interface exists to make
possible, used here for the first time for exactly that. It does not
synthesize a click on an orb or a chat panel's send-via-mouse path: an orb
click reaches `TerminalFocuser`, which fires real `tmux`/`ps`/`osascript`
processes off-thread with no OS guard at its own entry point, so a headless
click on a CI runner would be a real, unpredictable side effect rather than
a test.

All three new suites need settings.json out of the way, since even
constructing an `OrbWindow` reads a color setting in a field initializer —
see the next paragraph for why that file cannot otherwise be pointed
elsewhere. Each project's `TestBootstrap.cs` sets `CLAUDE_BUDDY_SETTINGS_DIR`
to a fresh temp directory via a `[ModuleInitializer]`, before any test can
run and before any settings static constructor can fire.

## Coverage

```bash
./tools/coverage.sh                            # whole-app line and branch coverage
./tools/coverage.sh --base upstream/develop    # ...and just the lines you added
```

**Two collectors, not one, and it has to stay that way.** `tests/UnitTests` and
`tests/IntegrationTests` run on VSTest and use `coverlet.collector`;
`tests/UiTests` runs on the Microsoft Testing Platform (it moved to xUnit v3
for `Avalonia.Headless.XUnit` 12.x — see its csproj) where VSTest data
collectors do not apply at all, so it uses
`Microsoft.Testing.Extensions.CodeCoverage`'s own `--coverage` instead. That
package is **pinned to 17.14.2** for the same reason everything else in that
csproj is pinned: 18.x depends on `Microsoft.Testing.Platform` 2.x while
xunit.v3 3.2.2 brings the `mtp-v1` packages, and the mix throws
`TypeLoadException` for `IDataConsumer` before one test runs. Bump xunit.v3 and
you have to re-check that pin.

That leaves three cobertura files measuring the *same* assembly, which is what
`tools/merge-coverage.py` is for: a line counts as covered if **any** suite
covered it. Adding the reports up instead is wrong in both directions at once —
it double-counts the denominator while undercounting the numerator, since a
line only a UI test reaches is reported unhit by the other two.

Two things the number does not say, worth remembering before quoting it:

- **`--base` is the number that matters when reviewing a change.** A file-level
  percentage is dominated by whatever was already in the file; the added-lines
  figure is the one that says whether the new code is tested.
- **The three console suites contribute nothing to it.** `ArrangementTests`,
  `GlyphTests` and `TranscriptTests` are plain exes, not test-SDK projects, so
  `OrbArrangement` reads 0% here while actually being the most exhaustively
  verified file in the repo (3456 cases). Read the number as "coverage from the
  xUnit suites", never as the sum of what this repo verifies.

Everything else about orb behavior is still verified by running the app.
Two things make that survivable:

- The status directory comes from the temp path, so `TMPDIR=<dir>` plus
  hand-written `<session-id>.txt` files gives a second instance its own fake
  sessions without touching real ones.
- Settings now honour `CLAUDE_BUDDY_SETTINGS_DIR`, an env-var override
  checked before `SpecialFolder.ApplicationData` — the same pattern as
  `CLAUDE_BUDDY_PROFILE_ROOT` in `ClaudeDesktopManager.cs`. Without it a test
  instance reads and writes the real
  `~/Library/Application Support/ClaudeBuddy/settings.json`; a manual run
  that skips setting it should still back that file up first.

Read window geometry back out of the window server (`CGWindowListCopyWindowInfo`
by owner pid) rather than eyeballing a screenshot when a change is about
*where* an orb sits — and don't synthesize mouse events on a machine someone is
using, because their real input interleaves with yours and the result is
nonsense.
