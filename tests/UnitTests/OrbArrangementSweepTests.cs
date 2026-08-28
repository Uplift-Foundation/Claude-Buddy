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
    // Kept as one Fact rather than a Theory per case. 62208 xUnit test cases
    // would cost more in discovery and reporting than the sweep costs to run,
    // and the sweep's own grouped report is a better failure message than 62208
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
                cases.Count >= 62208,
                $"the sweep is down to {cases.Count} cases from 62208 — "
                    + "if that was deliberate, raise the floor in this test");

            // All three sweeps present. The anchored half was added after a
            // shipped bug where the shape recentered itself on every orb that
            // joined or left, and the grouped half after one where a shape drawn
            // in its own band stopped honouring the anchor at all — so an edit
            // that dropped either would remove the regression test for the
            // reason that half exists.
            Assert.Contains(cases, c => c.Anchor is null && c.Split is null);
            Assert.Contains(cases, c => c.Anchor is not null && c.Split is null);
            Assert.Contains(cases, c => c.Split is not null);

            // And the grouped half is grouped: a split that puts every orb in
            // group 0 exercises the same one-band path as an ungrouped case, so
            // its presence alone would not prove the bands are ever cut.
            Assert.Contains(cases, c =>
                c.Split is { } split && split.Build(6).Distinct().Count() > 1);
        }
    }
}
