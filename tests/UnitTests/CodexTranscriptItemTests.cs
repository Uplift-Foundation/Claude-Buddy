using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// The tool rows Codex writes alongside its own messages: a command it ran, a file
// it changed, a summary of what it was thinking.
//
// Driven through CodexTranscript.Map rather than by reaching for the private
// helpers, so what is asserted is the path the app actually takes. The shapes
// below come from Codex's own rollout format, which nobody here controls and
// which has more than one spelling for most things — a command arrives either
// pre-parsed or as an argv array, a reasoning summary is an array that may hold
// strings or objects. Each accepted spelling is a branch, and a branch nothing
// exercises is a branch that quietly stops working when the format shifts.
public class CodexTranscriptItemTests
{
    private static string Row(string item) =>
        "{\"timestamp\":\"2026-08-24T12:00:00.000Z\",\"ordinal\":1,\"type\":\"event_msg\","
        + "\"payload\":{\"type\":\"item_completed\",\"item\":" + item + "}}";

    private static List<string> Texts(string item) =>
        CodexTranscript.Map(new[] { Row(item) })
            .Select(t => t.Turn.Text)
            .ToList();

    private static string Only(string item) => Assert.Single(Texts(item));

    // ---- commands --------------------------------------------------------

    // parsed_cmd is Codex's own reading of what the shell was asked to do, and
    // it wins when present: it is what the user would recognise.
    [Fact]
    public void APreParsedCommandIsPreferred()
    {
        var text = Only("""
        {"type":"CommandExecution",
         "parsed_cmd":[{"cmd":"dotnet build"}],
         "command":["bash","-lc","dotnet build 2>&1 | tail"]}
        """);

        Assert.Contains("dotnet build", text);
        Assert.DoesNotContain("tail", text);
    }

    // A parsed_cmd whose entries are blank falls through to the argv array
    // rather than showing an empty command row.
    [Fact]
    public void ABlankParsedCommandFallsThroughToTheArgv()
    {
        var text = Only("""
        {"type":"CommandExecution",
         "parsed_cmd":[{"cmd":"   "}],
         "command":["bash","-lc","ls -la"]}
        """);

        Assert.Contains("ls -la", text);
    }

    // The shell wrapper is dropped: `bash -lc "…"` shows what was asked of the
    // shell, not the invocation of the shell itself.
    [Fact]
    public void TheShellWrapperIsDropped()
    {
        var text = Only("""
        {"type":"CommandExecution","command":["bash","-lc","git status"]}
        """);

        Assert.Contains("git status", text);
        Assert.DoesNotContain("bash", text);
    }

    [Fact]
    public void TheOtherShellFlagIsDroppedToo()
    {
        var text = Only("""
        {"type":"CommandExecution","command":["sh","-c","git status"]}
        """);

        Assert.Contains("git status", text);
        Assert.DoesNotContain("sh -c", text);
    }

    // A command that is not a shell invocation is shown as it was run, joined
    // back into something readable.
    [Fact]
    public void ADirectCommandIsJoinedRatherThanUnwrapped()
    {
        var text = Only("""
        {"type":"CommandExecution","command":["git","status","--short"]}
        """);

        Assert.Contains("git status --short", text);
    }

    // Two arguments is not enough to be a shell wrapper, so it must not index
    // past the end reaching for parts[2].
    [Fact]
    public void ATwoPartCommandIsNotMistakenForAShellWrapper()
    {
        var text = Only("""{"type":"CommandExecution","command":["bash","-lc"]}""");

        Assert.Contains("bash -lc", text);
    }

    // Nothing usable still draws the label on its own — "it ran something",
    // without saying what. That is Tool()'s deliberate blank-argument branch
    // rather than an oversight: the row is evidence the session did work, and
    // dropping it would make a command whose shape this parser does not
    // recognise look like nothing happened at all.
    [Fact]
    public void ACommandWithNothingUsableStillShowsThatSomethingRan()
    {
        Assert.Equal("· exec", Only("""{"type":"CommandExecution","command":[]}"""));
    }

    [Fact]
    public void ACommandThatIsNotAnArrayStillShowsThatSomethingRan()
    {
        Assert.Equal("· exec", Only("""{"type":"CommandExecution","command":"git status"}"""));
    }

    [Fact]
    public void ACommandItemWithNoCommandAtAllStillShowsThatSomethingRan()
    {
        Assert.Equal("· exec", Only("""{"type":"CommandExecution"}"""));
    }

    // Non-string entries in the argv are skipped rather than rendered as their
    // JSON.
    [Fact]
    public void NonStringArgumentsAreSkipped()
    {
        var text = Only("""
        {"type":"CommandExecution","command":["git",7,"status"]}
        """);

        Assert.Contains("git status", text);
        Assert.DoesNotContain("7", text);
    }

    // ---- file changes ----------------------------------------------------

    [Fact]
    public void ASingleFileChangeNamesTheFile()
    {
        var text = Only("""
        {"type":"FileChange","changes":{"/src/OrbWindow.axaml.cs":{"kind":"modified"}}}
        """);

        Assert.Contains("OrbWindow.axaml.cs", text);
    }

    // Several files in one item get a count rather than one row each — one row
    // per file would bury the reply between them.
    //
    // The number is how many OTHERS, not the total: three files read "A.cs +2".
    // Worth pinning, because "+3" for three files is the obvious misreading and
    // the two differ by exactly one.
    [Fact]
    public void SeveralFileChangesGetACountOfTheRest()
    {
        var text = Only("""
        {"type":"FileChange","changes":{
            "/src/A.cs":{"kind":"modified"},
            "/src/B.cs":{"kind":"modified"},
            "/src/C.cs":{"kind":"added"}}}
        """);

        Assert.Contains("A.cs +2", text);
    }

    [Fact]
    public void OneFileChangeGetsNoCountAtAll()
    {
        Assert.DoesNotContain("+", Only("""
        {"type":"FileChange","changes":{"/src/A.cs":{"kind":"modified"}}}
        """));
    }

    // Same label-only branch as a command with nothing in it.
    [Fact]
    public void AFileChangeWithNothingInItStillShowsThatSomethingChanged()
    {
        Assert.Equal("· edit", Only("""{"type":"FileChange","changes":{}}"""));
        Assert.Equal("· edit", Only("""{"type":"FileChange"}"""));
        Assert.Equal("· edit", Only("""{"type":"FileChange","changes":[]}"""));
    }

    // ---- reasoning summaries ---------------------------------------------

    // Both plausible shapes of summary_text are accepted rather than guessing
    // which one the format will settle on — the file's own comment says it has
    // never been seen populated.
    [Fact]
    public void ASummaryOfPlainStringsIsRead()
    {
        var text = Only("""
        {"type":"Reasoning","summary_text":["Checking the arrangement","then the glyphs"]}
        """);

        Assert.Contains("Checking the arrangement", text);
    }

    [Fact]
    public void ASummaryOfObjectsIsReadToo()
    {
        var text = Only("""
        {"type":"Reasoning","summary_text":[{"text":"Checking the arrangement"}]}
        """);

        Assert.Contains("Checking the arrangement", text);
    }

    // Reasoning differs from the two above: its row is the summary text itself
    // rather than a labelled tool row, so with nothing to say there is nothing
    // to draw. An empty "thinking" row would be noise, not evidence.
    [Fact]
    public void AnEmptyOrAbsentSummaryProducesNoRow()
    {
        Assert.Empty(Texts("""{"type":"Reasoning","summary_text":[]}"""));
        Assert.Empty(Texts("""{"type":"Reasoning"}"""));
        Assert.Empty(Texts("""{"type":"Reasoning","summary_text":"thinking"}"""));
    }

    // ---- an item type nobody handles -------------------------------------

    // The format grows. An item type this version has never heard of has to be
    // skipped rather than throwing — a single unknown row must not cost the
    // whole transcript.
    [Fact]
    public void AnUnknownItemTypeIsSkippedQuietly()
    {
        Assert.Empty(Texts("""{"type":"SomethingNewInCodex","whatever":true}"""));
    }

    [Fact]
    public void AMalformedRowDoesNotStopTheRowsAroundIt()
    {
        var lines = new[]
        {
            "{ not json at all",
            Row("""{"type":"CommandExecution","command":["git","status"]}"""),
        };

        var turns = CodexTranscript.Map(lines);

        Assert.Contains("git status", Assert.Single(turns).Turn.Text);
    }
}
