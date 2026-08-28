# Working in this repository

Notes for Claude Code (and anyone else) working here. The README is the real
documentation — how the app works, how the hooks work, how to build installers.
This file only covers *how work gets landed*, which the code can't tell you.

`AGENTS.md` is the same content for Codex, which does not read this file. Two
files because each CLI looks for its own name and neither reads the other's —
**if you change one, change both.** A symlink would look like one file until
someone checks the repo out on Windows.

## How a feature gets built

**Every feature is a ticket, a team, and a round trip through QA — in that
order.** The backlog is Jira project **CB**, board 70:

<https://uplifttech.atlassian.net/jira/software/projects/CB/boards/70/backlog>

No feature work starts from a chat message alone. A request that arrives in a
terminal, in an issue, or in conversation becomes a CB ticket first, and from
then on the ticket is what moves — every agent in the chain leaves the board in
a state that says where the work actually is, so someone reading the board and
someone reading the terminal see the same thing.

**Prefer the autonomous path at every step.** The escalations below are for an
agent that is genuinely not confident, not checkpoints to hit out of habit. An
agent that can defend its plan, its tests and its screenshots should carry the
feature through to done and say so; one that cannot should ask early, while the
answer is still cheap, rather than after building the wrong thing.

### The team

Features are built by an **agent team**, not by one session doing everything.
That is the same mechanism this app draws orbs for: Claude Code spawns each
member as its own `claude` process carrying `--agent-name`, `--team-name` and
`--parent-session-id`, and `AgentTeam.cs` reads the last of those straight off
the process to link a member to its lead — deliberately not out of the
transcript, so a team that has gone quiet still draws its arrows. Building Claude Buddy with the thing Claude Buddy visualises is
deliberate — a team whose shape looks wrong on the board is a bug report about
the app, and you get it for free.

The roles, and who hands to whom:

- **Product manager** — writes the requirement, owns the ticket's status, and
  makes the final call on done.
- **Architect** and **engineer** — take a refined ticket and build it. Spawn as
  many of each as the work genuinely splits into; one per independent surface
  beats one agent doing all of them in sequence.
- **QA** — tests what the engineers build and hands failures back. A loop, not a
  gate at the end: build a piece, test a piece, send it back, repeat until QA
  has nothing left to return.

### Product manager: requirement first, then plan

The PM agent creates the requirement in CB as a **`Feature`** and leaves it in
**Refinement**, which is where CB's workflow puts a new issue anyway. CB's issue
types are `Epic`, `Feature`, `Story`, `Task`, `Bug` and `Subtask` (one word, no
hyphen); `Feature` sits at the same level as `Story` rather than above it, so an
`Epic` is still what groups a multi-ticket effort.

**CB's board has four columns, and they are now confirmed** — read off CB-1, the
first ticket filed, which is what this paragraph used to ask for. They are
**Refinement → Development → Testing → Done**, with transition ids 11, 21, 31 and
41; every transition is global, so any status can be reached from any other. The
name this file guessed was "Refining", which is a status on project FMN and not
one CB has: a team-managed project owns its workflow separately, and the guess
was wrong. Nothing else here needed correcting.

Then the PM writes a plan, and **plans on a stronger model than the one that
will implement it.** Planning is where a wrong decision is cheapest to fix and
most expensive to miss, so spend the capability there and let implementation run
cheaper:

| Feature | Plan with | Implement with |
| --- | --- | --- |
| Complex — a new subsystem, a cross-platform surface, anything touching the hook or transcript contracts | `fable` | `opus` or `sonnet` |
| Simpler — a setting, a bounded UI change, a fix with an obvious shape | `opus` or `sonnet` | `sonnet` |

Those are the `model:` values the Agent and Workflow tools accept (`fable`,
`opus`, `sonnet`, `haiku`). `effort:` is the other dial and moves the same way —
high while planning, lower for the mechanical stages afterwards.

**If the PM is not confident the requirement and plan are enough to build from
autonomously, it asks a human before spending a team on it.** Ask on GitHub,
against the repo, with a link to the CB ticket — and because this repo is public
while the Jira site is not, put enough of the requirement in the comment that
the question stands on its own for someone who cannot open the link. Repo admins
are the escalation point (`wtvamp` and `lunarjuice` at the time of writing;
`gh api repos/Uplift-Foundation/Claude-Buddy/collaborators` prints the current
list).

If it *is* confident, it hands straight to the architects and engineers, and the
feature proceeds without a human in the loop.

### Build and QA, in a loop

Engineers and architects build, QA agents test, and the work goes back and forth
until it stops coming back. The substance of QA's half is **Every feature ships
with its tests** below — unit, integration and UI, covering 100% of the lines
the branch adds — so "QA passed" means those exist and are green, not that
somebody had a look.

**A feature works on Windows and macOS, or it is not a feature.** CI enforces
the shape of that already: the matrix is `macos-latest`/`osx-arm64` and
`windows-latest`/`win-x64`, and every suite runs on both legs. Parity is more
than a green build, though — a feature no install path wires up is equally
unfinished. Before calling it done, check `tools/build-macos-dmg.sh`,
`tools/build-macos-app.sh`'s Resources copy, `tools/ClaudeBuddy.iss`,
`tools/install-hooks.sh`/`.ps1` and the README's install section, and satisfy
yourself that any platform-specific gate is real (WSL) rather than accidental.

### PR, screenshots, and the call on done

The feature goes up as a PR against `develop`, per **Pull requests** below.

**Don't attach screenshots by hand — they are already automatic.** `ci.yml`
captures a PNG per `tests/UiScreenshots` scenario on both runners;
`publish-screenshots.yml` then picks those artifacts up on a `workflow_run`
trigger, pushes them to the `screenshots` branch and comments on the PR with
real `raw.githubusercontent.com` image URLs, labelled per rid. (The two-workflow
split is not stylistic: a `pull_request` run from a fork gets a read-only token
and every PR here is one. Both files' header comments have the full story.) A
feature with a visible surface adds its capture to that suite, and the PR
comment is what everyone reviews.

The PM agent reviews that comment — **both** rids, since a macOS-only
implementation shows itself precisely there — and **if it can approve the
feature as done autonomously, it should**: approving the PR itself, and moving
the ticket accordingly.

**Approving is the agent's own call, not a checkpoint to hand back.** An agent
that has read both rids, can point at the tests covering the change and can say
what it confirmed on a real machine is holding everything a human reviewer would
be handed — asking a person to look anyway is the "checkpoint out of habit" this
file already warns against, and it costs a round trip to be told what the agent
already knew. So approve it:

```bash
gh pr review <number> --repo Uplift-Foundation/Claude-Buddy --approve --body "..."
```

The body is the part that matters, and it is the same distinction the rest of
this file keeps: name both rids, say which suites ran, and separate what was
confirmed on a machine from what is assumed. "LGTM" from an agent is worth
nothing to the next person; "routing verified end to end on a real Mac, Windows
not reproduced and not fixed" is worth the whole review.

GitHub refuses an approving review on a PR the same account opened, which is the
common case here since one person's token opens and reviews it. That is a
mechanical limit, not a reason to escalate — post the identical body as a review
comment instead. The written record is the point, not the green tick.

Approving is not the same as landing it. The ticket moves to Done when the
change is actually on `develop`, so an approved-but-unmerged PR stays in Testing
until the merge goes through.

Only where the agent genuinely cannot approve does it ask a human to pull the
branch, install it and approve by hand:

- **If someone is driving the feature in Claude Code**, ask them in the terminal.
  They are already there and the round trip costs seconds.
- **If nobody is watching the terminal**, ask on GitHub, on the PR.

Either way, name the specific thing you could not confirm from the screenshots.
"The Windows flyout renders, but I can't tell from the capture whether the mic
button is enabled" is a request someone can settle in a minute. "Needs manual
approval" is not.

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

That includes the tests — see **Every feature ships with its tests** below. A PR
that adds or changes behaviour without covering it isn't ready to review,
however well the behaviour itself works, and a line the tests can't reach is
named in the body rather than left for the next person to discover.

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

## Every feature ships with its tests

**A feature request is not finished when the feature works. It is finished when
the feature works and the code that makes it work is covered.** Tests land in
the same branch as the behaviour they cover — not a follow-up, not an issue
filed against `develop` — and the target for the lines that branch adds or
changes is **100%**: every new method, every arm of every new conditional,
every error path you wrote a `catch` for.

Cover it at all three levels the suites already separate, rather than only the
one that is easiest to reach from where the change happens to live:

- **Unit** (`tests/UnitTests`) — the decisions. If the change added a rule about
  which orb wins, which name is used, or where something is drawn, that rule
  belongs in a function with no window and no settings behind it, and that
  function gets a case per outcome. If new logic can't be reached without
  constructing a window, that is a seam to fix before writing the test, not a
  reason to skip it — the same argument that keeps `OrbArrangement`, `OrbGlyph`
  and both transcript parsers pure.
- **Integration** (`tests/IntegrationTests`) — the seams with what this process
  does not own: the hook scripts run as real subprocesses, files on disk,
  settings round-tripped through a real file. Anything touching a format
  someone else defines is covered here *as well as* by a unit test of the
  parsing, because the two fail differently — the parser gets a field wrong,
  the seam gets the whole exchange wrong.
- **UI** (`tests/UiTests`) — the headless Avalonia path. A new window, panel,
  control, binding or click handler gets driven with a synthesized click or a
  real `UpdateFrom`, and asserted on what a user would have seen. `ChatPanel`'s
  `FakeChatSession` is the pattern for anything that would otherwise need a
  live session behind it. A new *visible* surface also gets a capture in
  `tests/UiScreenshots`, which renders through real Skia rather than the null
  renderer; its cases are hand-written one per scenario, so adding a `UiTests`
  scenario does not add its screenshot for you.

A change to geometry, transcript parsing or orb initials extends the three
console suites too — `dotnet test tests/Tests.sln` does not run them, and CI
failing on `ArrangementTests` after a green `dotnet test` is a bad way to find
that out. CI runs every suite on both runners, so a test that only passes on
the machine you wrote it on blocks the build.

**100% means the diff, not the repository.** The app is nowhere near it — as of
this writing `UnitTests` alone covers about 3% of the assembly, and whole areas
of it (`TerminalFocuser`, the tray, the installers) make real
`tmux`/`ps`/`osascript` calls that a headless runner must not execute. Holding
new work to 100% is how that number climbs without a rewrite; holding the
existing repository to it would make the rule something everybody quietly
ignores, which is worse than not having one.

Where a line genuinely cannot be covered — an OS call with no seam, a `catch`
for something only the other platform throws — **name it in the PR body and say
why**, in the same voice this project already uses to separate "confirmed on a
real machine" from "assumed to work". An uncovered line that is named is a known
gap; an uncovered line that isn't is a claim about the suite that isn't true.

To see the number:

```bash
dotnet add tests/UnitTests/ClaudeBuddy.UnitTests.csproj package coverlet.collector
dotnet test tests/UnitTests --collect:"XPlat Code Coverage"
# tests/UnitTests/TestResults/<guid>/coverage.cobertura.xml
```

**Nothing in the repository collects coverage today, and the flag fails
silently without that package.** `--collect:"XPlat Code Coverage"` against a
project with no `coverlet.collector` reference creates an empty `TestResults/`
directory, prints no warning and exits 0 — a run that measured nothing, and
reads exactly like one that measured everything. Add the package to whichever
suite you are measuring and take it back out before you push, unless you are
deliberately wiring collection into all three suites and CI, which is its own
change and needs its own reasoning.

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
runs every one of them, plus `tests/UiScreenshots`, on both runners, before
packaging — a failing test blocks the
build the same way a failed `dotnet publish` already did.

**Run the UI suite in Release before pushing, because CI does and
`dotnet test` does not.**

```bash
dotnet test tests/Tests.sln            # Debug — what everyone runs
dotnet test tests/UiTests -c Release   # what CI actually runs
```

`dotnet test` defaults to Debug; `ci.yml` builds Release. That gap is not
theoretical and it is not about optimisation changing behaviour — Release simply
runs faster, which reorders a parallel suite and closes the gaps between writes
to a scratch directory. CB-3 landed six `SessionScanTests` that were green in
Debug on three separate machines and red in Release on both CI legs, every
attempt: no exception in the app, just a scan that found no sessions, because a
timing assumption held at Debug speed and not at Release speed.

The other three suites have been clean in both so far. It is `tests/UiTests` that
is worth the extra half-minute, being the one with a dispatcher, real timers and
process-wide statics in it. If a test passes in one configuration and not the
other, the answer is to make it independent of what else is running — never a
sleep, never a widened tolerance. This branch has fixed four flakes of that shape
and each commit says why.

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
`tests/UiTests` and `tests/UiScreenshots` run on the Microsoft Testing Platform
(both moved to xUnit v3 for `Avalonia.Headless.XUnit` 12.x — see their csprojs)
where VSTest data collectors do not apply at all, so they use
`Microsoft.Testing.Extensions.CodeCoverage`'s own coverage switch instead. That
package is **pinned to 17.14.2** in both, for the same reason everything else in
those csprojs is pinned: 18.x depends on `Microsoft.Testing.Platform` 2.x while
xunit.v3 3.2.2 brings the `mtp-v1` packages, and the mix throws
`TypeLoadException` for `IDataConsumer` before one test runs. Bump xunit.v3 and
you have to re-check both pins.

That leaves four cobertura files measuring the *same* assembly, which is what
`tools/merge-coverage.py` is for: a line counts as covered if **any** suite
covered it. Adding the reports up instead is wrong in both directions at once —
it double-counts the denominator while undercounting the numerator, since a
line only a UI test reaches is reported unhit by the other three.

`tests/UiScreenshots` counts as of CB-3 and did not before. CI has always run
it, and it is the only suite that draws through **real Skia** rather than the
null renderer — so a few things are reachable only there, a bitmap actually
written to disk most obviously (`ClaudeDesktopBundles.WriteTinted`, whose pixel
maths is tested there for exactly this reason). Leaving it out meant those lines
were verified and counted nowhere, which is the same invisible-verification
problem the console suites had.

**Before quoting a number, check the run actually produced one.** Two of the
three times a coverage figure has been wrong here, it was not wrong about the
code — the run had not measured what it claimed to, and said so on a line
nobody read. Both checks below cost seconds and each has already cost hours by
being skipped.

**`merged N report(s)` is the first line of the output, and it is a
self-check.** There are exactly four cobertura files, for the reasons above, so
**anything other than `merged 4` means the number printed underneath it is
fiction.** Both directions have been seen live, within one afternoon on CB-6:

- `merged 1` — three reports missing, because another agent working in the same
  clone ran `rm -rf bin obj tests/*/bin tests/*/obj` while this one was
  measuring. The binaries the report maps against were deleted underneath it.
- `merged 6` — two stale extras, left in the MTP suites' own `TestResults`
  directories from an earlier run. `coverage.sh` fishes reports out of there and
  does not clear it first, so an old one is simply merged in alongside.

**"Quiet tree" means the process table, not the working tree.** `git status`
being clean proves nothing about what is running; neither does an agent roster
that looks idle, because an agent between tool calls is indistinguishable from
one mid-`dotnet test`. Check `pgrep -fl "dotnet|coverage.sh|testhost"` for a
test host, a `dotnet test` or a second `coverage.sh`, then rebuild clean. **A
number taken while anything else was building does not leave the session that
took it.**

The failure this catches is worth recognising by sight, because it does not
look like a measurement error — it looks like a regression. On CB-6 a phantom
`56/60 = 93.3%` branch figure was reported as a real drop, sent back to an
engineer as work, and never reproduced: six later runs all gave `56/56 =
100%`. What gave it away was reading the lines it named as uncovered. Two were
**closing braces** and one was a range-slice assignment — none of which can
hold a branch in the source at all. Branch arms attributed to punctuation is the
signature of a report mapped against a binary that is stale or no longer there,
and it is quicker to spot than to re-run.

That is now three documented cases of an unreproducible number in this file,
and only one of them was the code's fault. The next one will also read as a
regression. Check `merged 4` and the process table first.

Four things the number does not say, worth remembering before quoting it:

- **The number is only reproducible because the settings-touching UI classes are
  serialised — keep them that way.** `ClaudeBuddySettings` is a process-wide
  static and nearly every visual class reads it while being constructed, so two
  test classes running in parallel with one of them flipping a setting do not
  race to a failure, they race to a *different set of executed lines*. Before
  CB-3 serialised them, three consecutive runs of `tests/UiTests` over an
  identical binary reported 1914, 2024 and 1914 covered lines in
  `SettingsWindow.cs`. That swing is bigger than most real changes, so it reads
  as one — and it cost this ticket an hour of chasing a 145-line "regression"
  that was scheduling. Anything new that reads or writes settings joins
  `[Collection("Settings")]` in `tests/UiTests/SettingsCollection.cs`, whose
  comment has the rest of the story.

- **`--base` is the number that matters when reviewing a change.** A file-level
  percentage is dominated by whatever was already in the file; the added-lines
  figure is the one that says whether the new code is tested.
- **The three console suites still contribute nothing to it** as suites —
  `ArrangementTests`, `GlyphTests` and `TranscriptTests` are plain exes, not
  test-SDK projects. Their *cases* do count now, because CB-3 moved each one's
  matrix into a class that `tests/UnitTests` compiles in and runs (see
  `ArrangementSweep`, `GlyphSuite`, `TranscriptSuite`), so `OrbArrangement` no
  longer reads 0% while being the most exhaustively verified file in the repo.
  Running the exes is still the way to get the grouped failure report.
- **What has been excluded is printed next to the number.** An excluded file and
  a deleted one look identical in a report, and a percentage can be walked to
  100% by excluding whatever refuses to be covered. `merge-coverage.py` therefore
  reads the attributes back out of the sources and reports files held out
  entirely, further sites inside measured files, and files absent for no stated
  reason at all. Read the headline as coverage **of what remains**.
- **The two engines disagree about `[ExcludeFromCodeCoverage]` on a *method*, and
  the merge compensates.** coverlet honours it — an excluded method's body is not
  instrumented at all — while `Microsoft.CodeCoverage`, which both MTP suites
  use, instruments it anyway and reports every line unhit. Both honour it on a
  *class*, which is how the gap went unnoticed until CB-3 had 157 member-level
  exclusions for it to show up in. So the merge is not a plain union:
  **coverlet's view of which lines exist is the authority**, and the MTP reports
  contribute hits for those lines only. If you ever change that, an exclusion on
  a method silently stops meaning anything.

Everything else about orb behavior is still verified by running the app.
Two things make that survivable:

- The status directory comes from the temp path, so `TMPDIR=<dir>` plus
  hand-written `<session-id>.txt` files gives a second instance its own fake
  sessions without touching real ones.
- The cloned-bundle cache honours `CLAUDE_BUDDY_BUNDLE_ROOT`, added by CB-3 for
  the same reason: without it, the only place a test of `ClaudeDesktopBundles`
  could write is the real `~/Library/Application Support/ClaudeBuddy/bundles` —
  the live cache, holding real cloned `.app` bundles whose icons a user is
  looking at. That the seam did not exist is the whole reason nothing in that
  file was covered.
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
