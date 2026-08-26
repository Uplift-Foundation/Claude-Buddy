using Xunit;

namespace ClaudeBuddy.Tests;

// Reading a session's own name off a real transcript file on disk.
//
// The parsing itself is pure and covered in tests/UnitTests. What can only be
// asserted against a real file is the *window*: TranscriptReader reads only the
// last TailBytes (262144, private const — hardcoded here for the same reason
// TranscriptReaderTests hardcodes it, InternalsVisibleTo does not reach private
// members), because real transcripts reach tens of MB and the scan runs every
// two seconds.
//
// That window is shared with ClaudeBuddyHook.sh on purpose — the hook decides a
// title from the same 256KB — so the two cannot disagree about what a session is
// called within it. Where they *do* differ is stated and asserted below.
//
// Row shapes are taken verbatim off a real machine (26 Aug 2026), per this
// repo's fixture-provenance rule: the fork whose status file never caught its
// name, in the byte-for-byte form its transcript holds.
public class TranscriptIdentityWindowTests
{
    private const int TailBytes = 262144;

    private const string ForkInherit =
        """{"type":"history-suppression","sessionId":"0e9677a5-8813-4800-8b8b-e786d701c097","cause":"fork_inherit","ts":"2026-08-26T17:32:07.036Z"}""";

    private const string CustomTitle =
        """{"type":"custom-title","customTitle":"evidence (2)","sessionId":"0e9677a5-8813-4800-8b8b-e786d701c097"}""";

    private const string AgentColor =
        """{"type":"agent-color","agentColor":"green","sessionId":"cd7a3a49-fb64-47a7-9dec-471ee884afc1"}""";

    private const string UserSaid =
        """{"type":"user","uuid":"u1","message":{"role":"user","content":"put it in memory"}}""";

    [Fact]
    public void ANameNearTheEndIsFound()
    {
        var path = WriteTempFile(ForkInherit, CustomTitle, AgentColor, UserSaid);

        var identity = TranscriptIdentity.From(TranscriptReader.TailLines(path));

        Assert.Equal("evidence (2)", identity.Title);
        Assert.Equal("green", identity.Color);
    }

    [Fact]
    public void ANameAtTheVeryStartOfASmallFileIsStillInTheWindow()
    {
        // The case CB-11 was filed for: the fork's name is its *second* row, and
        // the job went quiet immediately afterwards, so the file is small and the
        // whole of it is inside the tail.
        var path = WriteTempFile(ForkInherit, CustomTitle);

        Assert.Equal("evidence (2)",
            TranscriptIdentity.From(TranscriptReader.TailLines(path)).Title);
    }

    [Fact]
    public void APartialFirstRowIsDroppedRatherThanMisread()
    {
        // Seeking into the middle of a file lands mid-row, and half a row must
        // not be parsed as a whole one. TranscriptReader drops it; what matters
        // here is that a *name* in the surviving rows is still found.
        var filler = new string('x', TailBytes);
        var path = WriteTempFile(
            """{"type":"user","uuid":"big","message":{"role":"user","content":" """ + filler + "\"}}",
            CustomTitle);

        var lines = TranscriptReader.TailLines(path);

        Assert.All(lines, line => Assert.DoesNotContain("\"uuid\":\"big\"", line));
        Assert.Equal("evidence (2)", TranscriptIdentity.From(lines).Title);
    }

    // A stated limit, not a silent one.
    //
    // The hook, finding nothing in the tail, falls back to grepping the whole
    // file. This does not, deliberately: that fallback is a multi-megabyte read,
    // and the hook pays it once per tool call while the scan would pay it every
    // two seconds for every session that has no name — which is precisely the
    // set this feature looks at.
    //
    // The cost of not having it is nil in practice and no worse than today in
    // principle. Claude Code re-emits these records as a session goes, so a
    // long-running session keeps one near the end; and a session long enough to
    // push its only name past 256KB of output is not the quiet-fork case this
    // exists for. When it does happen the orb keeps falling back to its folder
    // name, which is exactly what it did before this feature existed.
    [Fact]
    public void ANamePushedOutOfTheWindowIsNotFoundAndThatIsTheDocumentedLimit()
    {
        var filler = string.Join("\n", Enumerable.Range(0, 400).Select(i =>
            $"{{\"type\":\"user\",\"uuid\":\"u{i}\",\"message\":{{\"role\":\"user\","
            + $"\"content\":\"{new string('y', 1000)}\"}}}}"));

        var path = WriteTempFile(CustomTitle, filler);

        Assert.True(new FileInfo(path).Length > TailBytes,
            "the fixture has to be bigger than the window for this to mean anything");

        Assert.Null(TranscriptIdentity.From(TranscriptReader.TailLines(path)).Title);
    }

    [Fact]
    public void AFileThatIsNotThereIsSilence()
    {
        // The scan asks about a path a status file named, and a session that is
        // ending can delete it in between. Empty, not a throw.
        var missing = Path.Combine(Path.GetTempPath(), "cb-absent-" + Guid.NewGuid() + ".jsonl");

        Assert.Empty(TranscriptReader.TailLines(missing));
        Assert.True(TranscriptIdentity.From(TranscriptReader.TailLines(missing)).IsEmpty);
    }

    [Fact]
    public void TheNewestNameInTheFileWins()
    {
        // /rename appends rather than rewrites, so a real file holds every name
        // the session has had. Read off the same machine: the fork's transcript
        // carried its custom-title twice, at rows 2 and 45.
        var path = WriteTempFile(
            CustomTitle,
            UserSaid,
            """{"type":"custom-title","customTitle":"evidence (3)","sessionId":"0e9677a5"}""");

        Assert.Equal("evidence (3)",
            TranscriptIdentity.From(TranscriptReader.TailLines(path)).Title);
    }

    private static string WriteTempFile(params string[] rows)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "cb-integrationtests-identity-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, string.Join("\n", rows) + "\n");
        return path;
    }
}
