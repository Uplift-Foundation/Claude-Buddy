using Xunit;

namespace ClaudeBuddy.Tests
{
    // OrbArrangement's geometry, as a test rather than as an exe you have to
    // remember to run.
    //
    // The sweep is not new and nothing here re-states it: the case matrix and
    // the invariants live in tests/ArrangementTests/ArrangementSweep.cs, which
    // this project compiles in, so `dotnet run --project tests/ArrangementTests`
    // and this test are running the identical cases against the identical rules.
    // What is new is that the lines now count. That project is a plain console
    // exe with no test SDK reference, so tools/coverage.sh never saw it, and the
    // most exhaustively verified file in the repository reported 0.0% — a number
    // CLAUDE.md warns about precisely because it reads like a gap and is not
    // one.
    //
    // Kept as one Fact rather than a Theory per case. 20736 xUnit test cases
    // would cost more in discovery and reporting than the sweep costs to run,
    // and the sweep's own grouped report is a better failure message than 20736
    // individual results: one broken rule fails hundreds of cases, and what you
    // need to see is the rule, not the roll call.
    public class OrbArrangementSweepTests
    {
        [Fact]
        public void EveryArrangementKeepsOrbsOnScreenAndApart()
        {
            var (cases, failures) = ArrangementSweep.RunAll();

            Assert.True(
                failures.Count == 0,
                $"{failures.Count} of {cases} arrangement cases failed:\n"
                    + ArrangementSweep.Report(failures));
        }

        // A floor, not an equality, and the asymmetry is the point: adding a
        // shape or a screen to the shared matrix should not fail a test, but
        // losing cases silently should. The sweep is shared between two projects
        // now, so a well-meant edit in one of them can shrink what the other
        // verifies with nothing to show for it — this is the assertion that
        // notices.
        [Fact]
        public void TheSweepStillCoversTheWholeMatrix()
        {
            var cases = ArrangementSweep.Cases().ToList();

            Assert.True(
                cases.Count >= 20736,
                $"the sweep is down to {cases.Count} cases from 20736 — "
                    + "if that was deliberate, raise the floor in this test");

            // Both sweeps present: the anchored half was added after a shipped
            // bug where the shape recentered itself on every orb that joined or
            // left, so an edit that dropped it would remove the regression test
            // for the reason the anchor exists.
            Assert.Contains(cases, c => c.Anchor is null);
            Assert.Contains(cases, c => c.Anchor is not null);
        }
    }
}
