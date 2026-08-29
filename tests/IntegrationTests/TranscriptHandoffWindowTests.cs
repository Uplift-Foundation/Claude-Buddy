using Xunit;

namespace ClaudeBuddy.Tests;

// TranscriptHandoff's I/O half against real files on disk: the stat, the
// cache keyed on it, and the tail window. The decision over rows is pure and
// covered in tests/UnitTests/TranscriptHandoffTests; what can only be asserted
// here is that a file that grows is re-read, that one that has not grown is
// answered from the cache, and that the window's limit is the stated one.
//
// Row shapes are the same real capture TranscriptHandoffTests documents:
// session 6d3a9d57, CLI 2.1.251, identifiers scrubbed.
public class TranscriptHandoffWindowTests
{
    private const string Marker =
        """{"parentUuid":"1b5cf160-79bf-4e2b-a01f-6511aee6b36b","isSidechain":false,"type":"system","subtype":"informational","content":"Backgrounding after the current tool finishes…","isMeta":false,"timestamp":"2026-08-28T17:53:15.295Z","uuid":"4f19d42a-80a5-4f9e-afe6-f234587acbf5","level":"warning","userType":"external","entrypoint":"cli","cwd":"/Users/w/project","sessionId":"6d3a9d57-10c6-4e9d-bf25-38194fae23c0","version":"2.1.251","gitBranch":"develop"}""";

    private const string CostState =
        """{"type":"cost-state","sessionId":"6d3a9d57-10c6-4e9d-bf25-38194fae23c0","totalCostUSD":11.502379,"totalDuration":1025384,"modelUsage":{"claude-fable-5":{"inputTokens":112,"outputTokens":41909,"costUSD":11.50041}},"hasUnknownModelCost":false}""";

    private const string UserSaid =
        """{"parentUuid":"4f19d42a-80a5-4f9e-afe6-f234587acbf5","isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"text","text":"actually, keep going here"}]},"uuid":"9c1d030b-f39c-4e4c-a635-208ee5b8c04d"}""";

    [Fact]
    public void TheHusksOwnTailReadsAsHandedOffFromDisk()
    {
        var path = WriteTempFile(UserSaid, Marker, CostState);

        Assert.True(TranscriptHandoff.EndsBackgrounded(path));

        // Asked again without the file changing: the cache answers, and it
        // answers the same thing. A husk's transcript never grows again, so
        // this is the every-two-seconds case for the rest of the husk's life.
        Assert.True(TranscriptHandoff.EndsBackgrounded(path));
    }

    [Fact]
    public void AFileThatGrowsIsReReadAndAResumedSessionGetsItsOrbBack()
    {
        var path = WriteTempFile(UserSaid, Marker, CostState);
        Assert.True(TranscriptHandoff.EndsBackgrounded(path));

        // The self-correcting direction, end to end: a user row appended after
        // the marker has to flip the cached answer, because the cache is keyed
        // on the file's length and mtime rather than on the path alone.
        File.AppendAllText(path, UserSaid + "\n");

        Assert.False(TranscriptHandoff.EndsBackgrounded(path));
    }

    [Fact]
    public void AMissingFileAssertsNothing()
    {
        // The positive answer is itself the hiding, so an unreadable
        // transcript must leave the orb alone — opposite direction to
        // BackgroundJobs.IsLive, and the comment on the rule says why.
        var missing = Path.Combine(
            Path.GetTempPath(), "cb-absent-" + Guid.NewGuid() + ".jsonl");

        Assert.False(TranscriptHandoff.EndsBackgrounded(missing));
        Assert.False(TranscriptHandoff.EndsBackgrounded(""));
        Assert.False(TranscriptHandoff.EndsBackgrounded((string?)null));
    }

    // A stated limit, not a silent one — the same standing
    // TranscriptIdentityWindowTests gives the 256KB identity window. The
    // handoff window is smaller because its question is about how the file
    // *ends*, and everything observed after a real marker is under 2KB of
    // housekeeping; a marker buried deeper than the window reads as "not
    // handed off", which brings back the duplicate orb rather than hiding a
    // live session — the fail-open direction.
    [Fact]
    public void AMarkerPushedOutOfTheWindowIsNotFoundAndThatIsTheDocumentedLimit()
    {
        // Filler that is neither conversation nor marker, so the answer below
        // is about the window and not about the rows: a cost-state per line.
        var filler = string.Join("\n",
            Enumerable.Range(0, 200).Select(_ => CostState));

        var path = WriteTempFile(Marker, filler);

        Assert.True(new FileInfo(path).Length > TranscriptHandoff.TailWindowBytes,
            "the fixture has to be bigger than the window for this to mean anything");

        Assert.False(TranscriptHandoff.EndsBackgrounded(path));
    }

    [Fact]
    public void ARewriteThatKeepsTheLengthIsStillNoticedThroughTheMtime()
    {
        // The cache key is length *and* mtime, and the mtime half has to carry
        // its own weight: a transcript is append-only today, but the key must
        // not quietly become "length alone" the day something rewrites one in
        // place. The replacement is padded to the byte, so only the mtime says
        // anything changed.
        var path = WriteTempFile(Marker);
        Assert.True(TranscriptHandoff.EndsBackgrounded(path));

        var markerBytes = new FileInfo(path).Length;
        var replacement = UserSaid + new string(' ',
            (int)markerBytes - System.Text.Encoding.UTF8.GetByteCount(UserSaid + "\n")) + "\n";
        File.WriteAllText(path, replacement);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow + TimeSpan.FromSeconds(2));

        Assert.Equal(markerBytes, new FileInfo(path).Length);
        Assert.False(TranscriptHandoff.EndsBackgrounded(path));
    }

    [Fact]
    public void TheCacheCapStartsOverRatherThanGrowingForever()
    {
        // Nothing ever unkeys a session's entry, so the cap is what stands
        // between the cache and a slow leak across weeks of sessions. Filling
        // it past 512 has to change no answer — the cap costs one extra read
        // per entry when it fires, and nothing else.
        var first = WriteTempFile(Marker);
        Assert.True(TranscriptHandoff.EndsBackgrounded(first));

        for (var i = 0; i < 513; i++)
        {
            Assert.False(TranscriptHandoff.EndsBackgrounded(WriteTempFile(UserSaid)));
        }

        Assert.True(TranscriptHandoff.EndsBackgrounded(first));
    }

    private static string WriteTempFile(params string[] rows)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "cb-integrationtests-handoff-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, string.Join("\n", rows) + "\n");
        return path;
    }
}
