using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-167's "vibe summary" turn-finished sound, from SpeechRequest.
// SpeakTurnSummary down through OrbWindow.SpeakTurnSummary — the layer
// SpeakScopeUiTests already proves the ordinary speak button through, now
// exercised for the path TurnSounds.Deliver reaches instead of a button
// click.
//
// Same boundary SpeakScopeUiTests and OrbWindowSpeakTests both keep: every
// case here stops at SpeechRequest.UtteranceForTests or SpeechSummary.
// SummarizerForTests, never at a real /usr/bin/say. See either file's header
// for why that is not optional.
[Collection("Settings")]
public class TurnSummarySpeechTests : IDisposable
{
    private readonly SpeakScope _scopeWas = ClaudeBuddySettings.SpeakScope;

    public void Dispose()
    {
        ClaudeBuddySettings.SpeakScope = _scopeWas;
        SpeechSummary.SummarizerForTests = null;
        SpeechRequest.UtteranceForTests = null;
        TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
    }

    // Long enough that SpeechPlan.For routes it through the summary leg
    // rather than speaking it whole — built from the threshold so it cannot
    // silently drift out of sync with the rule it exists to cross.
    private static string LongReply() =>
        string.Join(" ", Enumerable.Repeat("the assistant did a great deal", 60))
            .PadRight(SpeechPlan.ShortEnoughChars + 1, '.');

    // --- SpeechRequest.SpeakTurnSummary --------------------------------------

    // Exercises the same SpeakSummaryAsync(..., TurnFinished) overload the
    // section header above used to call directly with a hand-picked request
    // number — which is exactly the bug this version doesn't have.
    // _requestGeneration is one counter shared by the whole process for the
    // life of the test run, so a literal like `request: 1` is only ever
    // right by accident; going through SpeakTurnSummary lets it mint its own
    // NextRequest() the way production code does, which is also the only
    // way ShouldStillSpeak's check means anything.
    //
    // A fake summariser that resolves synchronously (Task.FromResult) lets
    // await complete without yielding, which is what makes the whole chain —
    // TurnFinished prompt in, spoken text out — observable right after this
    // call returns, with no dispatcher pump needed.
    [AvaloniaFact]
    public void ALongReplyIsSummarisedAndTheResultIsSpoken()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full; // proves the forced override below
        var reply = LongReply();

        string? seenBySummariser = null;
        SpeechSummary.SummarizerForTests = text =>
        {
            seenBySummariser = text;
            return Task.FromResult<string?>("Fixed the bug. Next: write the tests.");
        };

        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

        SpeechRequest.SpeakTurnSummary(reply, "session-x");

        Assert.Equal(reply, seenBySummariser);
        Assert.Equal("Fixed the bug. Next: write the tests.", spoken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToSayNeverTouchesTheSummariserOrTheSpeaker(string? reply)
    {
        var summariserCalled = false;
        SpeechSummary.SummarizerForTests = _ => { summariserCalled = true; return Task.FromResult<string?>("x"); };
        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

        SpeechRequest.SpeakTurnSummary(reply, "session-x");

        Assert.False(summariserCalled);
        Assert.Null(spoken);
    }

    // Always Summary scope, regardless of what the user has the ordinary
    // speak button set to — choosing "Vibe summary" as an orb's finished
    // sound is itself the request for a summary. Full is picked deliberately
    // here to prove the global setting is not what decides this.
    [AvaloniaFact]
    public void AShortReplyIsSpokenInFullEvenWithTheGlobalScopeSetToFull()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;

        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;
        var summariserCalled = false;
        SpeechSummary.SummarizerForTests = _ => { summariserCalled = true; return Task.FromResult<string?>("x"); };

        SpeechRequest.SpeakTurnSummary("Fixed the nested-team case.", "session-x");

        Assert.Equal("Fixed the nested-team case.", spoken);
        Assert.False(summariserCalled); // under SpeechPlan's threshold — no round trip needed
    }

    // The long-reply arm starts the summary leg synchronously up to its first
    // await — TextToSpeech.Enter(Preparing) runs before anything is awaited,
    // the same "hourglass goes up before the round trip" guarantee
    // SpeakScopeUiTests pins for the ordinary speak button. Asserting it here
    // is what proves SpeakTurnSummary's NeedsSummary branch actually ran
    // rather than silently falling through to Utter.
    [AvaloniaFact]
    public void ALongReplyEntersPreparingSynchronouslyBeforeAnyRoundTrip()
    {
        ClaudeBuddySettings.SpeakScope = SpeakScope.Full;

        // Never lets the round trip finish within this test, so the state
        // this asserts is unambiguously the synchronous part of the call.
        var never = new TaskCompletionSource<string?>();
        SpeechSummary.SummarizerForTests = _ => never.Task;

        try
        {
            SpeechRequest.SpeakTurnSummary(LongReply(), "session-x");

            Assert.Equal(TextToSpeech.SpeakState.Preparing, TextToSpeech.State);
        }
        finally
        {
            never.SetResult("unused");
        }
    }

    // --- OrbWindow.SpeakTurnSummary ------------------------------------------

    private static string WriteTranscript(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "cb-uitests-turnsummary-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [AvaloniaFact]
    public void ASessionWithNoTranscriptAnywhereSpeaksNothing()
    {
        var spoken = (string?)null;
        SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "claude-buddy",
            Cwd = "",
            TranscriptPath = "",
        });

        orb.SpeakTurnSummary();

        Assert.Null(spoken);
    }

    // Safe to press for real, the same reason SpeakScopeUiTests presses the
    // ordinary button for real: SpeechRequest's seam stands in for the one
    // excluded line, so this reaches SpeakTurnSummary's whole local branch —
    // FindSpeakableText, SpeechPlan, and Utter — without starting a process.
    [AvaloniaFact]
    public void ALocalSessionWithATranscriptSpeaksItsLastTurnThroughTheTurnSummaryPath()
    {
        const string AssistantSaid =
            """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";
        var path = WriteTranscript(AssistantSaid + "\n");

        try
        {
            var spoken = (string?)null;
            SpeechRequest.UtteranceForTests = (text, _, _) => spoken = text;

            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.ClaudeCode,
                State = "idle",
                Title = "claude-buddy",
                Cwd = "/tmp/does-not-matter",
                TranscriptPath = path,
            });

            orb.SpeakTurnSummary();

            Assert.Equal("Fixed the nested-team case.", spoken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void AGatewayOrbWithOpenClawDisabledFiresTheRemotePathWithoutThrowing()
    {
        var wasEnabled = ClaudeBuddySettings.OpenClawEnabled;
        try
        {
            ClaudeBuddySettings.OpenClawEnabled = false;

            var orb = new OrbWindow(Guid.NewGuid().ToString());
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.OpenClaw, State = "idle", Title = "Nova"
            });

            orb.SpeakTurnSummary();
        }
        finally
        {
            ClaudeBuddySettings.OpenClawEnabled = wasEnabled;
        }
    }

    // SpeakTurnSummaryRemoteAsync driven directly and awaited, the same
    // pattern OrbWindowSpeakTests uses for SpeakRemoteAsync: a real
    // (in-memory) history entry found synchronously, so
    // LastAssistantTextAsync returns without its own poll loop and the
    // Dispatcher.UIThread.Post(...) line actually runs.
    [AvaloniaFact]
    public async Task SpeakTurnSummaryRemoteAsyncFindsARealHistoryEntryAndSchedulesTheRead()
    {
        var wasEnabled = ClaudeBuddySettings.OpenClawEnabled;
        var agent = "nova" + Guid.NewGuid().ToString("N")[..8];
        var sessionId = $"openclaw:agent:{agent}:discord:channel:1";
        try
        {
            ClaudeBuddySettings.OpenClawEnabled = true;

            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor(sessionId, "Nova")!;
            chat.SetHistory(new[]
            {
                new HistoryTurn(ChatRole.Assistant, "hello from the agent", null, "",
                    DateTimeOffset.UtcNow, null, null),
            });

            var orb = new OrbWindow(sessionId);
            orb.UpdateFrom(new SessionStatus
            {
                Source = SessionSource.OpenClaw, State = "idle", Title = "Nova"
            });

            await orb.SpeakTurnSummaryRemoteAsync();
        }
        finally
        {
            ClaudeBuddySettings.OpenClawEnabled = wasEnabled;
        }
    }
}
