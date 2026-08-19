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

## Codex specifically

`docs/codex-findings.md` is the reference for everything measured about the
Codex CLI — the rollout format, the hook events and their payloads, and a
clearly separated list of what has *not* been verified. Read it before changing
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
