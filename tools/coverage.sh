#!/usr/bin/env bash
#
# Line and branch coverage for the three xUnit suites, as one number.
#
#   tools/coverage.sh                     # whole-app coverage
#   tools/coverage.sh --base upstream/develop   # ...plus coverage of new lines
#
# Two collectors, not one, and that is not an accident:
#
#   * tests/UnitTests and tests/IntegrationTests run on VSTest, so they use
#     coverlet.collector via `--collect:"XPlat Code Coverage"`.
#   * tests/UiTests runs on the Microsoft Testing Platform (it had to move to
#     xUnit v3 for Avalonia.Headless.XUnit 12.x — see its csproj), and VSTest
#     data collectors do not apply there at all. It uses
#     Microsoft.Testing.Extensions.CodeCoverage's own `--coverage` instead.
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

OUT="${TMPDIR:-/tmp}/claude-buddy-coverage"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "==> tests/UnitTests"
dotnet test tests/UnitTests \
  --collect:"XPlat Code Coverage" \
  --results-directory "$OUT/unit" \
  | tail -2

echo "==> tests/IntegrationTests"
dotnet test tests/IntegrationTests \
  --collect:"XPlat Code Coverage" \
  --results-directory "$OUT/integration" \
  | tail -2

# --coverage-output is relative to the test binary's own TestResults directory,
# so the file is fished out of there afterwards rather than written straight to
# $OUT.
echo "==> tests/UiTests"
dotnet test tests/UiTests -- \
  --coverage --coverage-output-format cobertura --coverage-output ui.cobertura.xml \
  | tail -2

UI_REPORT="$(find tests/UiTests/bin -name ui.cobertura.xml -print -quit)"
if [[ -z "$UI_REPORT" ]]; then
  echo "tests/UiTests produced no cobertura report" >&2
  exit 1
fi
cp "$UI_REPORT" "$OUT/ui.cobertura.xml"

echo
python3 tools/merge-coverage.py \
  "$OUT/**/coverage.cobertura.xml" "$OUT/ui.cobertura.xml" "$@"
