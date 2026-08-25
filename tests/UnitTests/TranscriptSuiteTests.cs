using Xunit;

namespace ClaudeBuddy.Tests
{
    // The transcript and dialog parsers, as a test rather than as an exe you
    // have to remember to run.
    //
    // The cases live in tests/TranscriptTests/TranscriptSuite.cs, which this
    // project compiles in, so `dotnet run --project tests/TranscriptTests` and
    // this test check the identical fixtures. Only the reporting differs.
    //
    // One Fact rather than a Theory per case, unlike the glyph tables next door.
    // The suite is a single linear script whose cases share fixtures and
    // intermediate results — `all` is mapped once and then asserted against a
    // dozen ways — so there is no per-case entry point to hang a Theory row on
    // without restructuring a thousand lines of fixtures that are correct as
    // they stand. The suite's own report names every failure, which is what a
    // Theory would have bought.
    //
    // Worth stating why this matters more than a coverage number: both parsers
    // read formats nobody here controls, and both fail quietly. A mis-mapped
    // transcript silently drops a message; a mis-read dialog puts a button on
    // screen that presses something other than what it says. Until now the only
    // thing standing between a regression in either and a shipped build was
    // somebody remembering to run an exe by hand, because CI ran
    // `dotnet test tests/Tests.sln` and that solution does not contain this
    // suite.
    public class TranscriptSuiteTests
    {
        [Fact]
        public void EveryTranscriptAndDialogCasePasses()
        {
            var failures = TranscriptSuite.RunAll();

            Assert.True(
                failures.Count == 0,
                $"{failures.Count} transcript cases failed:\n  ✗ "
                    + string.Join("\n  ✗ ", failures));
        }
    }
}
