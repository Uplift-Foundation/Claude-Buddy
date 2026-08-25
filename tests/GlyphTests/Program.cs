using ClaudeBuddy.Tests;

// The two or three letters an orb wears, and the ones the chat panel's header
// wears beside it, run by hand.
//
// The cases moved to GlyphSuite.cs so that tests/UnitTests can compile them in
// and run the identical tables as real tests — this project is a plain console
// exe with no test SDK reference, so tools/coverage.sh never counted a line of
// what it verifies. The rationale for each case lives with the case, in that
// file.
//
// Run it with `dotnet run --project tests/GlyphTests`. Non-zero exit means
// something regressed, and each failure prints the input and both answers.
var failures = GlyphSuite.RunAll();

if (failures.Count == 0)
{
    Console.WriteLine($"{GlyphSuite.Total} cases, all passed");
    return 0;
}

Console.WriteLine($"{GlyphSuite.Total} cases, {failures.Count} failed\n");
foreach (var failure in failures) Console.WriteLine($"  {failure}");
return 1;
