using System.IO;
using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.Tests;

// What the summariser is *run as*, rather than what it is asked.
//
// The prompt and the cleaning were both covered when CB-165 shipped, and the
// thing that was actually wrong was neither: the subprocess inherited the app's
// working directory, so `claude -p` discovered the project's CLAUDE.md and
// answered in its persona instead of summarising. CB-174.
//
// These assert the invocation itself, which is why the class exists separately
// from SpeechSummaryTextTests — covering what a process is asked says nothing
// about the context it is asked in.
public class SpeechSummaryInvocationTests
{
    [Fact]
    public void ItRunsFromADirectoryWithNoProjectInstructionsAboveIt()
    {
        var info = SpeechSummary.StartInfoFor("/usr/local/bin/claude");

        Assert.False(string.IsNullOrEmpty(info.WorkingDirectory));
        Assert.Equal(SpeechSummary.NeutralWorkingDirectory, info.WorkingDirectory);
    }

    // The regression in its own words: never the directory the app happens to
    // be running in. A summariser started there reads whatever CLAUDE.md sits
    // at or above it and stops being a summariser.
    [Fact]
    public void ItNeverInheritsTheAppsOwnWorkingDirectory()
    {
        var info = SpeechSummary.StartInfoFor("/usr/local/bin/claude");

        Assert.NotEqual(Directory.GetCurrentDirectory(), info.WorkingDirectory);
    }

    // The negative control. Without this, a bug that set WorkingDirectory to
    // some *other* wrong-but-constant path would pass both cases above — they
    // only say "not the cwd", and this says the directory is real and usable.
    [Fact]
    public void TheNeutralDirectoryExists()
    {
        Assert.True(Directory.Exists(SpeechSummary.NeutralWorkingDirectory));
    }

    [Fact]
    public void ItStillAsksTheModelItAlwaysAsked()
    {
        var info = SpeechSummary.StartInfoFor("/usr/local/bin/claude");

        Assert.Equal("/usr/local/bin/claude", info.FileName);
        Assert.Equal(new[] { "-p", "--model", SpeechSummary.Model }, info.ArgumentList);
    }

    // Reading the reply back requires stdin and stdout; CreateNoWindow keeps a
    // console from flashing on Windows. Asserted because losing any of them
    // fails at runtime only, on one platform, in a path excluded from coverage.
    [Fact]
    public void ItRedirectsWhatItNeedsAndShowsNoWindow()
    {
        var info = SpeechSummary.StartInfoFor("/usr/local/bin/claude");

        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
    }
}
