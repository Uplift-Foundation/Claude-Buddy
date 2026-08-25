using ClaudeBuddy.Tests;

// The orb-geometry sweep, run by hand.
//
// Everything this used to hold inline — the case matrix and the invariants —
// now lives in ArrangementSweep.cs so that tests/UnitTests can compile it in and
// run the same cases as a real test. That matters for more than tidiness: this
// project is a plain console exe rather than a test-SDK project, so it
// contributes nothing to tools/coverage.sh, and OrbArrangement.cs read 0.0%
// coverage while actually being the most exhaustively verified file in the repo.
// One matrix, two callers, and the number stops lying.
//
// This half stays because the grouped failure report is what makes a regression
// readable by hand, and because `dotnet run --project tests/ArrangementTests` is
// in CLAUDE.md and in people's muscle memory. Non-zero exit means something
// regressed, and each failure prints the exact case so it can be reproduced.
var (cases, failures) = ArrangementSweep.RunAll();

Console.WriteLine($"{cases} cases");

if (failures.Count == 0)
{
    Console.WriteLine("all passed");
    return 0;
}

Console.WriteLine($"{failures.Count} failures\n");
Console.Write(ArrangementSweep.Report(failures));

return 1;
