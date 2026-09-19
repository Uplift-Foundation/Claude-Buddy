using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudeBuddy.Tests;

// Nothing may read the stored credential except through a budget.
//
// **A source-text guard, which this repository does not reach for lightly — and
// this is the case that earns one.** The measured failure is that
// `ICloudCredentialSource.Read()` can block indefinitely: on a real Mac with no
// window server session, the Keychain's data query does not return, at 30 seconds
// and at 60. `ClaudeCliCredentials.ReadWithinAsync` is the only thing standing
// between that and a process that parks, and every other test in the suite proves
// it works — while proving nothing at all about whether it is *used*.
//
// That gap is not hypothetical. CB-164 fixed the arm and the chat panel, both
// verified against a fake that never returns, and left `tools/claude-cloud-probe`
// calling the source directly at three call sites. The probe then still hung for
// a hundred seconds against the same Keychain the app handled correctly in 45 —
// and the probe is the one tool anybody reaches for when this exact thing is going
// wrong. A behavioural test could not have caught it, because the probe has no
// behaviour a test suite can drive; a grep can, and does.
//
// So this is the rule stated once, in the only form that covers a file nothing
// else in the suite executes: **`ReadWithinAsync` is the only thing that may
// invoke a credential source's `Read()`, and it does not spell it as a call.**
//
// That last clause is not pedantry, and it was discovered by this guard failing
// on its own wrapper. `ReadWithinAsync` hands the method *group* to `Task.Run` —
// `Task.Run(source.Read, …)` — so the invocation never appears in source text at
// all, and the forbidden shape `x.Read()` is genuinely absent from every file in
// the repository including the one allowed to do it. The rule is therefore
// simpler and stricter than it was first written: an invocation of a credential
// source's `Read()` is wrong *everywhere*, with no exempt file, because the one
// legitimate use is a thread hop that does not look like a call.
public class CredentialBudgetTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeBuddy.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find ClaudeBuddy.csproj by walking up from " + AppContext.BaseDirectory);
    }

    // The shapes a direct read takes in this codebase: `_credentials.Read()`,
    // `credentials.Read()`, `Source().Read()`, `source.Read()`. Matched on the
    // call rather than on a variable name, so a fourth spelling is caught too.
    //
    // Comments are stripped first. The files being scanned explain at length *why*
    // the direct call is forbidden, and a guard that a file's own explanation of
    // itself can trip is a guard people delete.
    private static readonly Regex DirectRead =
        new(@"\.\s*Read\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex Comments =
        new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly string[] Scanned =
    {
        "ClaudeCloudSessions.cs",
        "ClaudeCloudChat.cs",
        "ClaudeCloudApi.cs",
        "ClaudeCloudRoster.cs",
        Path.Combine("tools", "claude-cloud-probe", "Program.cs"),
    };

    [Theory]
    [InlineData("ClaudeCloudSessions.cs")]
    [InlineData("ClaudeCloudChat.cs")]
    [InlineData("ClaudeCloudApi.cs")]
    [InlineData("ClaudeCloudRoster.cs")]
    [InlineData("tools/claude-cloud-probe/Program.cs")]
    public void NothingReadsACredentialSourceWithoutABudget(string relative)
    {
        var path = Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"expected to find {relative} at {path}");

        var code = Comments.Replace(File.ReadAllText(path), "");
        var offenders = code.Split('\n')
            .Select((line, i) => (Line: line.Trim(), Number: i + 1))
            .Where(l => DirectRead.IsMatch(l.Line))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{relative} calls a credential source's Read() directly, bypassing the budget in "
            + "ClaudeCliCredentials.ReadWithinAsync. That read can block forever — measured on a "
            + "real Mac with no window server session, at 30s and at 60s — so the call parks "
            + "whichever thread makes it. Offending lines: "
            + string.Join(", ", offenders.Select(o => $"{o.Number}: {o.Line}")));
    }

    // **The negative control for the guard itself.** A regex that matched nothing
    // would pass every case above while checking nothing at all, and would read
    // exactly like a clean result — which is the failure mode CLAUDE.md names, a
    // confident negative ending the inquiry. So: the pattern must fire on the very
    // shape it forbids, and must not fire on the budgeted call that replaced it.
    [Fact]
    public void TheGuardMatchesTheShapeItForbidsAndNotTheOneItRequires()
    {
        Assert.Matches(DirectRead, "var read = _credentials.Read();");
        Assert.Matches(DirectRead, "var read = Source().Read();");
        Assert.Matches(DirectRead, "var read = source . Read ( ) ;");

        Assert.DoesNotMatch(DirectRead,
            "var read = await ClaudeCliCredentials.ReadWithinAsync(source, budget, ct);");
        Assert.DoesNotMatch(DirectRead, "var json = File.ReadAllText(path);");
        Assert.DoesNotMatch(DirectRead, "reader.ReadLine();");
    }

    // And the comment stripping works, so the guard cannot be tripped by a file
    // explaining why the direct call is forbidden — the reason those files all
    // mention it in prose.
    [Fact]
    public void AMentionInACommentIsNotAnOffence()
    {
        var stripped = Comments.Replace("// never write credentials.Read() here\nvar x = 1;", "");

        Assert.DoesNotMatch(DirectRead, stripped);
    }

    // **The wrapper reaches the read as a method group, not as a call**, which is
    // what makes the rule above exempt no file at all. Pinned because a
    // well-meaning refactor to `Task.Run(() => source.Read())` would be a
    // behaviourally identical, entirely reasonable-looking edit that turns the
    // guard's clean sweep into a false negative — the file would then contain the
    // forbidden shape legitimately, and whoever hit it would be tempted to add an
    // exemption rather than ask why.
    [Fact]
    public void TheWrapperReachesTheReadAsAMethodGroupRatherThanACall()
    {
        var code = Comments.Replace(
            File.ReadAllText(Path.Combine(RepoRoot, "ClaudeCliCredentials.cs")), "");

        Assert.Contains("ReadWithinAsync", code, StringComparison.Ordinal);
        Assert.Contains("Task.Run(source.Read", code, StringComparison.Ordinal);
        Assert.DoesNotMatch(DirectRead, code);
    }

    // The probe waits for less than the app does, because nobody at a terminal is
    // reading a consent dialog. Pinned because the two constants drifting together
    // would quietly undo the reason there are two.
    [Fact]
    public void TheProbeIsImpatientAndTheAppIsNot()
    {
        // Comments stripped first: the probe's constant explains itself by naming
        // the app's, and a guard a file's own explanation can trip is a guard
        // people delete.
        var probe = Comments.Replace(File.ReadAllText(Path.Combine(RepoRoot, "tools",
            "claude-cloud-probe", "Program.cs")), "");

        Assert.Contains("UnmeasuredProbeReadBudget", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ClaudeCliCredentials.UnmeasuredReadBudget", probe,
            StringComparison.Ordinal);
    }

    // Every scanned file exists where the guard expects it, so a rename cannot
    // silently reduce this suite to checking nothing.
    [Fact]
    public void EveryScannedFileIsWhereTheGuardLooksForIt()
    {
        foreach (var relative in Scanned)
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot, relative)),
                $"{relative} is missing — the guard is scanning a file that no longer exists");
        }
    }
}
