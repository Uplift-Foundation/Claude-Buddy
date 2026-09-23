using System.Runtime.CompilerServices;
using Xunit;

namespace ClaudeBuddy.Tests;

// The guard under the guard.
//
// CrashLog's AsyncLocal scope gives a test's scratch log directory no
// process-wide name, so a test that forgets to isolate cannot create another
// test's directory. LogDirScopeFlowsFromConstructorTests proves that scope is
// live rather than inert, and LogDirIsolationTests proves a writer on a
// foreign flow cannot reach a scoped directory.
//
// Every one of those asserts the same thing: that *CrashLog.Directory* reports
// the scoped value. **None of them asserts that CrashLog.Directory is the only
// way the log directory is reached** — and that is a different claim. A writer
// that called Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR") for
// itself would bypass the scope entirely, land on whatever the process-wide
// variable currently says, and reopen exactly the race the scope was built to
// close. Every existing test would still pass, because every existing test
// asks the property rather than the writer.
//
// That gap is not hypothetical reasoning about a possible future: it is the
// shape of the original defect. PersonaFiles.Reject reached the shared state
// two calls removed while naming nothing, and three pull requests in a row
// failed to enumerate the classes that could get there. The scope fixed the
// *directory*; what holds it fixed is that there is one read site, and nothing
// structural was keeping it that way.
//
// So this is the structural half. It is a source scan rather than a behaviour
// test on purpose — the property it protects is "no other code does this",
// which no amount of exercising the code that exists can establish. A new
// bypass is caught when it is written rather than when it next races.
public class LogDirSingleReadSiteTests
{
    private const string Variable = "CLAUDE_BUDDY_LOG_DIR";

    // The exact text of a read. Matching the call rather than the bare name
    // keeps the many comments that discuss the variable out of the count —
    // they mention it, they do not read it, and a guard that fired on prose
    // would be turned off within a week.
    private const string Read = "GetEnvironmentVariable(\"" + Variable + "\")";

    [Fact]
    public void The_log_directory_variable_is_read_in_exactly_one_place()
    {
        var root = RepositoryRoot();

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsApplicationSource)
            .Select(path => (path, hits: Hits(File.ReadAllText(path))))
            .Where(x => x.hits > 0)
            .OrderBy(x => x.path, StringComparer.Ordinal)
            .ToList();

        var described = string.Join(
            Environment.NewLine,
            offenders.Select(o => $"  {Path.GetRelativePath(root, o.path)} ({o.hits})"));

        // Named rather than counted, so a failure says which file to look at
        // instead of only that the number moved.
        Assert.True(
            offenders.Count == 1 && offenders[0].hits == 1,
            $"{Variable} must be read in exactly one place in application source — "
            + "CrashLog.Directory, which the AsyncLocal scope takes precedence over. "
            + "Anything else bypasses the scope and reopens the race that "
            + "CrashLog.ScopeForTests exists to close. Found:"
            + Environment.NewLine + (described.Length == 0 ? "  (nowhere)" : described));

        Assert.Equal("CrashLog.cs", Path.GetFileName(offenders[0].path));
    }

    private static int Hits(string text)
    {
        var hits = 0;
        var at = text.IndexOf(Read, StringComparison.Ordinal);

        while (at >= 0)
        {
            hits++;
            at = text.IndexOf(Read, at + Read.Length, StringComparison.Ordinal);
        }

        return hits;
    }

    // Application source: the repository root's own .cs files and the
    // directories of app code under it, never the test projects (which
    // legitimately drive the variable — LogDirIsolationTests uses it as the
    // control that proves the scope is doing something) and never build
    // output, where a stale copy of a deleted file would fail this for a
    // reason nobody could act on.
    private static bool IsApplicationSource(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);

        return !parts.Contains("tests")
            && !parts.Contains("bin")
            && !parts.Contains("obj")
            && !parts.Contains("tools");
    }

    // Resolved from this file's own compile-time path rather than from the
    // working directory, which under a test host is the binary's directory and
    // varies by rid and configuration. CallerFilePath is baked in at compile
    // time, so it is correct on both CI legs and on a developer's machine.
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);

        while (directory is not null && !File.Exists(Path.Combine(directory, "ClaudeBuddy.csproj")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        // Loud rather than skipped. A guard that quietly passes when it cannot
        // find the thing it guards is worse than no guard: it reads exactly
        // like a clean run. This repository has paid for a false zero more
        // than once.
        Assert.True(
            directory is not null,
            $"could not find the repository root above {thisFile} — this guard "
            + "cannot run, and must fail rather than pass silently.");

        return directory!;
    }
}
