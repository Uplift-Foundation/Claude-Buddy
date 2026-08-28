using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// TranscriptHandoff.EndsBackgrounded over rows — the rule that decides whether
// a status file is the husk a backgrounded turn left behind, and so whether an
// orb that looks perfectly healthy is in fact a duplicate of the fork's.
//
// Every fixture row below is the shape of a row captured off a real transcript
// (session 6d3a9d57, CLI 2.1.251, the machine the duplicate "Ub" orbs were
// photographed on), with the user's paths and account identifiers scrubbed and
// nothing else changed. The dialog parser was once written against an invented
// fixture and failed on every real dialog; these were captured first.
public class TranscriptHandoffTests
{
    // The marker row itself, verbatim but for the cwd. Note "userType":
    // "external" riding along inside it — a real reason the user-row needle
    // has to be the anchored "type":"user" and not anything looser.
    private const string Marker =
        @"{""parentUuid"":""1b5cf160-79bf-4e2b-a01f-6511aee6b36b"",""isSidechain"":false,""type"":""system"",""subtype"":""informational"",""content"":""Backgrounding after the current tool finishes…"",""isMeta"":false,""timestamp"":""2026-08-28T17:53:15.295Z"",""uuid"":""4f19d42a-80a5-4f9e-afe6-f234587acbf5"",""level"":""warning"",""userType"":""external"",""entrypoint"":""cli"",""cwd"":""/Users/w/project"",""sessionId"":""6d3a9d57-10c6-4e9d-bf25-38194fae23c0"",""version"":""2.1.251"",""gitBranch"":""develop""}";

    // The housekeeping Claude Code appended after the handoff on the real
    // machine: two cost-state rows around a bridge-session row. None of them
    // is conversation, and none of them may clear or hide the marker.
    private const string CostState =
        @"{""type"":""cost-state"",""sessionId"":""6d3a9d57-10c6-4e9d-bf25-38194fae23c0"",""totalCostUSD"":11.502379,""totalAPIDuration"":749824,""totalToolDuration"":93368,""totalLinesAdded"":66,""totalLinesRemoved"":71,""totalDuration"":1025384,""startTime"":1787938572801,""modelUsage"":{""claude-fable-5"":{""inputTokens"":112,""outputTokens"":41909,""costUSD"":11.50041}},""hasUnknownModelCost"":false}";

    private const string BridgeSession =
        @"{""type"":""bridge-session"",""sessionId"":""6d3a9d57-10c6-4e9d-bf25-38194fae23c0"",""bridgeSessionId"":""cse_00000000000000000000000000"",""lastSequenceNum"":0,""ownerAccountUuid"":""00000000-0000-0000-0000-000000000000"",""ownerOrganizationUuid"":""00000000-0000-0000-0000-000000000000""}";

    // The turn as it was being handed away: an assistant row mid-tool-call,
    // which is the last conversational row a real husk holds.
    private const string AssistantRow =
        @"{""parentUuid"":""51bd71d5-92ed-441c-930e-afdb0feae11d"",""isSidechain"":false,""message"":{""model"":""claude-fable-5"",""id"":""msg_011CeVaBXRDdJ8kGLqvZNVYx"",""type"":""message"",""role"":""assistant"",""content"":[{""type"":""tool_use"",""id"":""toolu_0195PZZHuZKSNyGSyYYHpk6k"",""name"":""Bash"",""input"":{""command"":""dotnet test"",""description"":""Run tests""}}],""stop_reason"":""tool_use""},""type"":""assistant"",""uuid"":""1b5cf160-79bf-4e2b-a01f-6511aee6b36b"",""timestamp"":""2026-08-28T17:53:11.000Z""}";

    private const string UserRow =
        @"{""parentUuid"":""4f19d42a-80a5-4f9e-afe6-f234587acbf5"",""isSidechain"":false,""type"":""user"",""message"":{""role"":""user"",""content"":[{""type"":""text"",""text"":""actually, keep going here""}]},""uuid"":""9c1d030b-f39c-4e4c-a635-208ee5b8c04d"",""timestamp"":""2026-08-28T18:01:00.000Z""}";

    [Fact]
    public void TheRealTailReadsAsHandedOff()
    {
        // Exactly what the captured transcript ends with: the turn, the
        // marker, and three housekeeping rows. This is the husk.
        Assert.True(TranscriptHandoff.EndsBackgrounded(new[]
        {
            AssistantRow, Marker, CostState, BridgeSession, CostState,
        }));
    }

    [Fact]
    public void TheMarkerAloneIsEnough()
    {
        Assert.True(TranscriptHandoff.EndsBackgrounded(new[] { Marker }));
    }

    [Fact]
    public void AUserRowAfterTheMarkerMeansTheSessionLivedOn()
    {
        // The self-correcting direction: whatever the tail held earlier, a
        // person typing in this session again must bring the orb back.
        Assert.False(TranscriptHandoff.EndsBackgrounded(new[]
        {
            AssistantRow, Marker, CostState, UserRow,
        }));
    }

    [Fact]
    public void AnAssistantRowAfterTheMarkerMeansTheSameThing()
    {
        // The fork's own transcript is the case this is really about: it
        // inherits the parent's rows, marker included, and the first answer it
        // writes is what separates it from the husk it was forked from.
        Assert.False(TranscriptHandoff.EndsBackgrounded(new[]
        {
            Marker, CostState, AssistantRow,
        }));
    }

    [Fact]
    public void AnOrdinaryWorkingTailSaysNothing()
    {
        Assert.False(TranscriptHandoff.EndsBackgrounded(new[]
        {
            UserRow, AssistantRow, CostState,
        }));

        Assert.False(TranscriptHandoff.EndsBackgrounded(Array.Empty<string>()));
    }

    [Fact]
    public void ASystemRowWithOtherNewsIsSkippedNotJudged()
    {
        // Same row shape as the marker, different content — a future system
        // message appended after a handoff must not clear it, exactly as the
        // housekeeping rows do not. The variant is the real row re-worded.
        var otherSystem = Marker.Replace(
            "Backgrounding after the current tool finishes…",
            "Compacting conversation history…", StringComparison.Ordinal);

        Assert.True(TranscriptHandoff.EndsBackgrounded(new[]
        {
            Marker, otherSystem,
        }));

        // And on its own it asserts nothing.
        Assert.False(TranscriptHandoff.EndsBackgrounded(new[] { otherSystem }));
    }

    [Fact]
    public void MessageTextQuotingTheMarkerCannotForgeIt()
    {
        // Inside a JSON string every quote is escaped, so a row merely
        // *talking about* the marker carries \"type\":\"system\" and never the
        // bare byte sequence. A summary row is the honest carrier for this: it
        // is neither conversation nor housekeeping, so nothing else about it
        // decides the answer.
        var quoting =
            @"{""type"":""summary"",""summary"":""the row was {\""type\"":\""system\"",\""content\"":\""Backgrounding after the current tool finishes…\""}"",""leafUuid"":""4f19d42a-80a5-4f9e-afe6-f234587acbf5""}";

        Assert.False(TranscriptHandoff.EndsBackgrounded(new[] { quoting }));

        // And after a real marker it is skipped like any other unknown row.
        Assert.True(TranscriptHandoff.EndsBackgrounded(new[] { Marker, quoting }));
    }

    [Fact]
    public void ATornFirstLineIsSkippedLikeAnyOtherUnrecognisedRow()
    {
        // The tail window starts mid-file, and ReadTail drops the partial
        // first line only when it seeked past the beginning — a file smaller
        // than the window arrives whole, so a torn fragment has to be inert
        // here too rather than relied on being absent.
        var torn = CostState[300..];

        Assert.True(TranscriptHandoff.EndsBackgrounded(new[] { torn, Marker }));
    }
}
