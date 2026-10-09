using System.IO;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

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
        Assert.Equal(SpeechSummary.Model, ArgAfter(info, "--model"));
    }

    // The regression this pins: the instruction used to go on stdin with the
    // reply, where Claude Code frames it as pasted content and the model does
    // not follow it — so the vibe summary read aloud "it looks like you pasted
    // this, what would you like me to do?". It is the -p argument now, which
    // is the user's own message.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheInstructionIsTheUsersMessageNotPartOfStdin(bool turnFinished)
    {
        var kind = turnFinished ? SpeechSummaryKind.TurnFinished : SpeechSummaryKind.Reply;
        var info = SpeechSummary.StartInfoFor("/usr/local/bin/claude", kind);

        Assert.Equal(SpeechSummary.Instruction(kind), ArgAfter(info, "-p"));
    }

    // The negative control for the case above: the kind actually reaches the
    // command line, rather than every summariser asking the Reply question.
    [Fact]
    public void TheKindChangesTheInstructionSent()
    {
        Assert.NotEqual(
            ArgAfter(SpeechSummary.StartInfoFor("claude", SpeechSummaryKind.Reply), "-p"),
            ArgAfter(SpeechSummary.StartInfoFor("claude", SpeechSummaryKind.TurnFinished), "-p"));
    }

    // Claude Code's own system prompt describes an agent with tools in a
    // repository; replaced, so none of it can be mistaken for the task.
    [Fact]
    public void ItReplacesTheAgentSystemPrompt()
    {
        Assert.Equal(SpeechSummary.SystemPrompt, ArgAfter(SpeechSummary.StartInfoFor("claude"), "--system-prompt"));
    }

    // No tools: an empty value is "none", not "default".
    [Fact]
    public void ItRunsWithNoTools()
    {
        Assert.Equal("", ArgAfter(SpeechSummary.StartInfoFor("claude"), "--tools"));
    }

    // Hooks off, because a SessionStart persona hook fires for `claude -p`
    // and the summary then opened by introducing itself. Parsed rather than
    // string-compared, so the assertion is about what the CLI will read.
    [Fact]
    public void ItTurnsHooksOff()
    {
        var settings = ArgAfter(SpeechSummary.StartInfoFor("claude"), "--settings");

        using var doc = System.Text.Json.JsonDocument.Parse(settings);
        Assert.True(doc.RootElement.GetProperty("disableAllHooks").GetBoolean());
    }

    [Fact]
    public void ItLeavesNoSessionBehind()
    {
        Assert.Contains("--no-session-persistence", SpeechSummary.StartInfoFor("claude").ArgumentList);
    }

    private static string ArgAfter(System.Diagnostics.ProcessStartInfo info, string flag)
    {
        var args = info.ArgumentList;
        var at = args.IndexOf(flag);
        Assert.True(at >= 0 && at + 1 < args.Count, $"{flag} missing or has no value");
        return args[at + 1];
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
