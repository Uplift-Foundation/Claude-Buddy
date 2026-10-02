#!/usr/bin/env bash
#
# Line and branch coverage for the four xUnit suites, as one number.
#
#   tools/coverage.sh                     # whole-app coverage
#   tools/coverage.sh --base upstream/develop   # ...plus coverage of new lines
#
# Two collectors, not one, and that is not an accident:
#
#   * tests/UnitTests and tests/IntegrationTests run on VSTest, so they use
#     coverlet.collector via `--collect:"XPlat Code Coverage"`.
#   * tests/UiTests and tests/UiScreenshots run on the Microsoft Testing
#     Platform (both had to move to xUnit v3 for Avalonia.Headless.XUnit 12.x —
#     see their csprojs), and VSTest data collectors do not apply there at all.
#     They use Microsoft.Testing.Extensions.CodeCoverage's own `--coverage`
#     instead.
#
# That package is version-pinned for the same class of reason as everything else
# in that csproj: 18.x depends on Microsoft.Testing.Platform 2.x, while
# xunit.v3 3.2.2 brings the mtp-v1 packages, and mixing them throws
# TypeLoadException for IDataConsumer before a single test runs. 17.14.2 is the
# newest that shares platform v1. If you bump xunit.v3, re-check this pin.
#
# The three suites above (ArrangementTests, GlyphTests, TranscriptTests) are
# plain console exes, not test-SDK projects, so they contribute nothing here —
# their coverage of OrbArrangement/OrbGlyph/ChatTranscript is real but invisible
# to this number. Read it as "coverage from the xUnit suites", not as the sum of
# everything this repo verifies.
set -euo pipefail

cd "$(dirname "$0")/.."

# Keyed by which checkout this is, because "rm -rf" two lines down is otherwise
# aimed at somebody else's reports. CLAUDE.md has features built by a team of
# agents each in its own git worktree, and every one of them is told to measure
# coverage — same machine, same TMPDIR, one directory. The failure is silent and
# it lies in both directions: a run that wipes the shared directory mid-flight
# leaves the merge reading whichever reports happen to exist, so a suite that
# passed can be missing from the number entirely, and one worktree's hits can be
# attributed to another's source.
#
# Found exactly that way — a local number quoted OrbArrangement at 0% while
# three engineer worktrees were measuring, because the ui and shots reports had
# been deleted out from under it between being written and being read.
#
# The path is hashed rather than used directly: it can be long, contains
# slashes, and none of that belongs in a directory name. Sixteen hex characters
# of it is plenty to keep concurrent checkouts apart.
# shasum is Perl's and ships with macOS; Git Bash on Windows has sha1sum instead
# and no shasum at all, so `set -e` killed the script here with exit 127 before
# it measured anything (CB-229, found on the Windows box). Same digest either way.
if command -v shasum >/dev/null 2>&1; then HASHER=shasum; else HASHER=sha1sum; fi
CHECKOUT_KEY="$(printf '%s' "$PWD" | $HASHER | cut -c1-16)"
OUT="${TMPDIR:-/tmp}/claude-buddy-coverage/$CHECKOUT_KEY"
rm -rf "$OUT"
mkdir -p "$OUT"

# A native Windows Python (what `python3` is under Git Bash) does not understand
# MSYS paths: "/c/Users/..." is a path on the current drive to it, and glob()
# over that matches nothing, without an error. CB-229: that dropped both
# coverlet reports and printed a whole-app figure off the two that were left.
# So every path handed to Python goes through here, and the *glob* is resolved
# in bash with find rather than shipped to Python as a pattern. cygpath only
# exists on Windows; elsewhere this is the identity.
native() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

# Stale reports from an earlier run. The MTP suites write theirs under their own
# bin/ and the script fishes them out with find, which would happily return an
# old file if this run's suite failed before writing one -- the "merged 6" story
# in CLAUDE.md is the same hazard from the other side.
find tests/UiTests/bin tests/UiScreenshots/bin \
  \( -name ui.cobertura.xml -o -name shots.cobertura.xml \) -delete 2>/dev/null || true

# A red suite must not stop the others being measured -- `set -e` used to abort
# at the first one, so on any machine with a failing test everything after it
# was never collected -- but it must not be able to hide either. Failures are
# remembered here and re-announced after the merge, and the exit status is
# non-zero whenever there was one.
RED=()

echo "==> tests/UnitTests"
dotnet test tests/UnitTests \
  --collect:"XPlat Code Coverage" \
  --results-directory "$OUT/unit" \
  | tail -2 || RED+=("tests/UnitTests")

echo "==> tests/IntegrationTests"
dotnet test tests/IntegrationTests \
  --collect:"XPlat Code Coverage" \
  --results-directory "$OUT/integration" \
  | tail -2 || RED+=("tests/IntegrationTests")

# --coverage-output is relative to the test binary's own TestResults directory,
# so the file is fished out of there afterwards rather than written straight to
# $OUT.
#
# If either MTP suite ever ends with every test passing and then an
# ArgumentOutOfRangeException about a path being "of an invalid length for use
# with domain sockets", or a TimeoutException out of
# Microsoft.CodeCoverage.Interprocess.LoggerClient.ConnectPipe, the cause is not
# here and not upstream: something in the test process has moved TMPDIR. The
# collector puts its IPC socket under TMPDIR, and its server end resolves that
# path before the test assembly is loaded — so a module initializer that
# repoints TMPDIR sends the client looking somewhere the server never bound. It
# then fails one of two ways depending only on how long the wrong path is, which
# is why it reads as two separate bugs. CB-172; the four TestBootstrap.cs files
# carry the full story and CLAUDE_BUDDY_STATUS_ROOT is the seam that replaced it.
#
# **And do not check for the report by asking whether the file is there.** Both
# of those failures still left a 5.3MB cobertura file on disk, freshly written,
# well-formed, with the same line-rate each time — the collector throws on the
# way out having already written something. So "it produced a report" was true
# throughout the whole period this was broken, and anyone who checked that way
# would have concluded the suite was fine. The find/exit-1 guards below catch a
# missing file; only `merged N` catches a present one that nobody should trust.
echo "==> tests/UiTests"
dotnet test tests/UiTests -- \
  --coverage --coverage-output-format cobertura --coverage-output ui.cobertura.xml \
  | tail -2 || RED+=("tests/UiTests")

UI_REPORT="$(find tests/UiTests/bin -name ui.cobertura.xml -print -quit)"
if [[ -z "$UI_REPORT" ]]; then
  echo "tests/UiTests produced no cobertura report" >&2
  RED+=("tests/UiTests (no report)")
else
  cp "$UI_REPORT" "$OUT/ui.cobertura.xml"
fi

# tests/UiScreenshots, which CI has always run and this number never counted.
# It is the only suite that draws through real Skia rather than the null
# renderer, so a handful of things are only reachable there — a bitmap actually
# written to disk, most obviously. Same platform as tests/UiTests, so it
# collects the same way.
echo "==> tests/UiScreenshots"
dotnet test tests/UiScreenshots -- \
  --coverage --coverage-output-format cobertura --coverage-output shots.cobertura.xml \
  | tail -2 || RED+=("tests/UiScreenshots")

SHOTS_REPORT="$(find tests/UiScreenshots/bin -name shots.cobertura.xml -print -quit)"
if [[ -z "$SHOTS_REPORT" ]]; then
  echo "tests/UiScreenshots produced no cobertura report" >&2
  RED+=("tests/UiScreenshots (no report)")
else
  cp "$SHOTS_REPORT" "$OUT/shots.cobertura.xml"
fi

echo
# Resolved here, in bash, and handed over as plain native paths.
# merge-coverage.py refuses outright unless it is given exactly four, so a
# missing one is a refusal with a reason rather than a number.
REPORTS=()
while IFS= read -r f; do REPORTS+=("$(native "$f")"); done < <(find "$OUT" -name '*.cobertura.xml' | sort)

MERGE_RC=0
python3 tools/merge-coverage.py ${REPORTS[@]+"${REPORTS[@]}"} "$@" || MERGE_RC=$?

if (( ${#RED[@]} > 0 )); then
  echo >&2
  echo "!!! RED SUITES: ${RED[*]}" >&2
  echo "!!! The figure above was measured from a run with failing suites." >&2
fi
if (( ${#RED[@]} > 0 || MERGE_RC != 0 )); then
  exit 1
fi
