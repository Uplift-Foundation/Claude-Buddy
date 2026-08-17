# Working in this repository

Notes for Claude Code (and anyone else) working here. The README is the real
documentation — how the app works, how the hook works, how to build installers.
This file only covers *how work gets landed*, which the code can't tell you.

## Branching: gitflow

Two long-lived branches:

- **`main`** — released code. Tagged releases come from here. Nothing is
  committed to it directly.
- **`develop`** — the integration branch. Finished work lands here first and
  sits until it's part of a release.

Everything else is short-lived and **named for what it is**:

| Prefix | For | Branches from | Merges to |
| --- | --- | --- | --- |
| `feature/` | anything new or changed | `develop` | `develop` |
| `release/` | preparing a version (bump, notes, packaging fixes) | `develop` | `main` **and** `develop` |
| `hotfix/` | a fix that can't wait for the next release | `main` | `main` **and** `develop` |

Use `feature/` for fixes and docs too — the prefix says where the work goes,
not how important it is. Name the rest of the branch after the change, not the
issue number: `feature/persist-orb-positions`, not `feature/pr-12`.

Releases before 0.1.2 used flat names (`release-0.1.1-beta`) and features went
straight to `main`; that's history, not a pattern to copy.

## Pull requests

Open every PR **against `develop`** unless it's a `release/` or `hotfix/`
branch, which target `main`. The repository's default branch is still `main`, so
GitHub will offer the wrong base — pass it explicitly:

```bash
gh pr create --repo Uplift-Foundation/Claude-Buddy --base develop \
  --head feature/<name> --title "..." --body "..."
```

Say in the PR body what was actually verified and what wasn't. This project
keeps that distinction deliberately — see `docs/*-findings.md`, which separate
"confirmed on a real machine" from "assumed to work". A PR that claims more than
was tested costs more than one that admits a gap.

**Don't rename a pushed branch to fix its name.** GitHub closes the open PR
instead of retargeting it, and reopening is refused once the old head ref is
gone; you have to open a fresh PR. Get the prefix right before the first push.

## Remotes

- **`upstream`** → `Uplift-Foundation/Claude-Buddy`, the canonical repository.
  Branches and PRs go here.
- **`origin`** → `wtvamp/Claude-Buddy`, a personal fork. Older branches still
  track it; don't add to them.

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
into chat turns, and reading a permission dialog off a captured tmux pane. Same
rule as the geometry — `ChatTranscript` is pure, and `ClaudeCodeChatSession`
only decides which bytes to hand it.

Both parsers read formats nobody here controls, and both fail *quietly*: a
mis-mapped transcript silently drops a message, and a mis-read dialog puts a
button on screen that presses something other than what it says. Write fixtures
from real output, not from memory — the dialog parser was first written against
an invented fixture and failed on every real dialog. To capture one:

```bash
tmux capture-pane -p -t %<pane> > /tmp/pane.txt
dotnet run --project tests/TranscriptTests -- /tmp/pane.txt   # or a .jsonl
```

Everything else about orb behavior is still verified by running the app.
Two things make that survivable:

- The status directory comes from the temp path, so `TMPDIR=<dir>` plus
  hand-written `<session-id>.txt` files gives a second instance its own fake
  sessions without touching real ones.
- Settings do **not** follow `HOME` on macOS —
  `SpecialFolder.ApplicationData` resolves through the OS, so a test instance
  reads the real `~/Library/Application Support/ClaudeBuddy/settings.json`.
  Back it up and restore it if a test needs to seed values.

Read window geometry back out of the window server (`CGWindowListCopyWindowInfo`
by owner pid) rather than eyeballing a screenshot when a change is about
*where* an orb sits — and don't synthesize mouse events on a machine someone is
using, because their real input interleaves with yours and the result is
nonsense.
