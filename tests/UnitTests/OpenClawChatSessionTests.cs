using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // A gateway conversation as the chat panel sees it: events in, turns out.
    //
    // This was at 0% across 144 instrumented lines and needed nothing to reach —
    // no dispatcher, no gateway, no fake. The constructor takes three strings and
    // every event handler is in-memory. Only SendAsync leaves the process, and it
    // is one line delegating to the class that owns the connection.
    //
    // The rules here are the ones that decide what a user watching an agent work
    // actually sees, and two of them are load-bearing in ways the code does not
    // advertise: the snapshot semantics, and the cap on history.
    [Collection("Settings")]
    public class OpenClawChatSessionTests
    {
        private static OpenClawChatSession Session() =>
            new("openclaw:agent:main:main", "agent:main:main", "main");

        private static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement;

        private static JsonElement AgentText(string text, string? stream = null)
        {
            var streamPart = stream is null ? "" : $"\"stream\":{JsonSerializer.Serialize(stream)},";
            return Event($"{{{streamPart}\"data\":{{\"text\":{JsonSerializer.Serialize(text)}}}}}");
        }

        // --- OnAgentText: the snapshot, not the delta ---

        [Fact]
        public void AnAgentEventStartsATurn()
        {
            var session = Session();
            var added = new List<ChatTurn>();
            session.TurnAdded += added.Add;

            session.OnAgentEvent("agent", AgentText("Working on it"));

            Assert.Single(added);
            Assert.Equal("Working on it", added[0].Text);
            Assert.Equal(ChatRole.Assistant, added[0].Role);
        }

        // data.text is a full snapshot of the turn so far rather than an
        // increment, and using it means a dropped or coalesced event costs
        // nothing. The test that matters is therefore that the second event
        // *replaces* rather than appends — treating it as a delta would produce
        // "OneOne two".
        [Fact]
        public void LaterEventsReplaceTheTurnRatherThanAppendingToIt()
        {
            var session = Session();
            var updates = new List<ChatTurn>();
            session.TurnUpdated += updates.Add;

            session.OnAgentEvent("agent", AgentText("One"));
            session.OnAgentEvent("agent", AgentText("One two"));
            session.OnAgentEvent("agent", AgentText("One two three"));

            Assert.Single(session.History);
            Assert.Equal("One two three", session.History[0].Text);
            Assert.Equal(2, updates.Count);
        }

        // A coalesced event — the gateway skipping straight from "One" to the
        // finished paragraph — loses nothing, which is the property the panel is
        // written against.
        [Fact]
        public void AnEventSkippedEntirelyCostsNothing()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("One"));
            session.OnAgentEvent("agent", AgentText("One two three four five"));

            Assert.Equal("One two three four five", session.History[0].Text);
        }

        // Thinking is shown, because watching an agent think is most of the value
        // of an orb that pulses — but as its own turn rather than mixed into the
        // reply it will eventually give.
        [Fact]
        public void ThinkingIsItsOwnTurnAndItsOwnRole()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("Considering the clamp", "thinking"));
            session.OnAgentEvent("agent", AgentText("The clamp runs first", "assistant"));

            Assert.Equal(2, session.History.Count);
            Assert.Equal(ChatRole.System, session.History[0].Role);
            Assert.Equal(ChatRole.Assistant, session.History[1].Role);
        }

        // ...and back again, so a second thinking pass after a reply is a third
        // turn rather than reopening the first.
        [Fact]
        public void SwitchingBackToThinkingStartsAnotherTurn()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("thought", "thinking"));
            session.OnAgentEvent("agent", AgentText("said", "assistant"));
            session.OnAgentEvent("agent", AgentText("thought again", "thinking"));

            Assert.Equal(3, session.History.Count);
        }

        // With no stream named, it is the assistant talking — the common case.
        [Fact]
        public void AnUnlabelledStreamIsTheAssistant()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("plain"));

            Assert.Equal(ChatRole.Assistant, session.History[0].Role);
        }

        // Nothing to show is no turn, rather than an empty bubble.
        [Theory]
        [InlineData("""{"data":{"text":""}}""")]
        [InlineData("""{"data":{}}""")]
        [InlineData("""{"data":7}""")]
        [InlineData("{}")]
        public void AnEventWithNoTextAddsNothing(string json)
        {
            var session = Session();

            session.OnAgentEvent("agent", Event(json));

            Assert.Empty(session.History);
        }

        // --- Complete: the turn is finished ---

        [Fact]
        public void AFinishedCronRunCompletesTheTurn()
        {
            var session = Session();
            session.OnAgentEvent("agent", AgentText("done thinking"));

            session.OnAgentEvent("cron", Event("""{"action":"finished"}"""));

            Assert.True(session.History[0].IsComplete);
        }

        [Fact]
        public void AnUpsertedTaskCompletesTheTurn()
        {
            var session = Session();
            session.OnAgentEvent("agent", AgentText("working"));

            session.OnAgentEvent("task", Event("""{"action":"upserted"}"""));

            Assert.True(session.History[0].IsComplete);
        }

        // Some other action on the same event name is not a completion, or a
        // cron job merely being scheduled would close a turn mid-sentence.
        [Theory]
        [InlineData("cron", """{"action":"scheduled"}""")]
        [InlineData("task", """{"action":"deleted"}""")]
        [InlineData("cron", "{}")]
        public void AnotherActionDoesNotCompleteTheTurn(string name, string json)
        {
            var session = Session();
            session.OnAgentEvent("agent", AgentText("still going"));

            session.OnAgentEvent(name, Event(json));

            Assert.False(session.History[0].IsComplete);
        }

        // Once complete, the next agent event starts a new turn rather than
        // reopening the finished one.
        [Fact]
        public void ACompletedTurnIsNotReopened()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("first"));
            session.OnAgentEvent("cron", Event("""{"action":"finished"}"""));
            session.OnAgentEvent("agent", AgentText("second"));

            Assert.Equal(2, session.History.Count);
            Assert.Equal("first", session.History[0].Text);
            Assert.Equal("second", session.History[1].Text);
        }

        [Fact]
        public void CompletingWithNothingInFlightIsHarmless()
        {
            var session = Session();

            session.OnAgentEvent("cron", Event("""{"action":"finished"}"""));

            Assert.Empty(session.History);
        }

        // --- OnTool: what the agent reached for ---

        // One line per tool call, in the transcript rather than in a status area,
        // because what an agent reached for is part of what it said.
        [Fact]
        public void AToolCallBecomesItsOwnLine()
        {
            var session = Session();

            session.OnAgentEvent("session.tool",
                Event("""{"data":{"phase":"start","name":"Read"}}"""));

            Assert.Single(session.History);
            Assert.Equal("· Read", session.History[0].Text);
            Assert.Equal(ChatRole.System, session.History[0].Role);
            Assert.True(session.History[0].IsComplete);
        }

        // Only the start. A tool that reported start and finish would otherwise
        // appear twice for one call.
        [Theory]
        [InlineData("""{"data":{"phase":"end","name":"Read"}}""")]
        [InlineData("""{"data":{"name":"Read"}}""")]
        [InlineData("""{"data":{"phase":"start"}}""")]
        [InlineData("""{"data":{"phase":"start","name":""}}""")]
        [InlineData("{}")]
        public void OnlyAToolStartWithANameIsShown(string json)
        {
            var session = Session();

            session.OnAgentEvent("session.tool", Event(json));

            Assert.Empty(session.History);
        }

        // A tool line does not interrupt the turn in flight: the agent is still
        // mid-reply, and the next snapshot has to keep updating the same bubble
        // rather than starting a third.
        [Fact]
        public void AToolLineDoesNotEndTheTurnInFlight()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("Looking"));
            session.OnAgentEvent("session.tool", Event("""{"data":{"phase":"start","name":"Grep"}}"""));
            session.OnAgentEvent("agent", AgentText("Looking, found it"));

            Assert.Equal(2, session.History.Count);
            Assert.Equal("Looking, found it", session.History[0].Text);
            Assert.Equal("· Grep", session.History[1].Text);
        }

        // An event name this app does not know is ignored rather than mishandled.
        [Fact]
        public void AnUnknownEventNameIsIgnored()
        {
            var session = Session();

            session.OnAgentEvent("something.new", AgentText("text"));

            Assert.Empty(session.History);
        }

        // --- the history cap ---

        // Generous on purpose: at 60 a busy conversation dropped its own
        // beginning while you were reading it, which is what "stuff disappears"
        // looked like. The cap is asserted along with *which end* it trims, since
        // trimming the wrong one would delete the newest messages.
        [Fact]
        public void TheHistoryIsCappedAndTrimsTheOldestFirst()
        {
            var session = Session();

            for (var i = 0; i < 520; i++)
            {
                session.OnAgentEvent("session.tool",
                    Event("{\"data\":{\"phase\":\"start\",\"name\":\"tool" + i + "\"}}"));
            }

            Assert.Equal(500, session.History.Count);
            Assert.Equal("· tool20", session.History[0].Text);
            Assert.Equal("· tool519", session.History[^1].Text);
        }

        // --- history replacement and paging ---

        private static IReadOnlyList<HistoryTurn> Turns(params string[] texts) =>
            texts.Select(t => new HistoryTurn(
                ChatRole.Assistant, t, null, "", DateTimeOffset.UnixEpoch,
                "main", "#7f7")).ToList();

        [Fact]
        public void SettingHistoryReplacesEverythingAndSaysSo()
        {
            var session = Session();
            var replaced = 0;
            session.HistoryReplaced += () => replaced++;

            session.OnAgentEvent("agent", AgentText("live"));
            session.SetHistory(Turns("one", "two"));

            Assert.Equal(new[] { "one", "two" }, session.History.Select(t => t.Text));
            Assert.Equal(1, replaced);
        }

        // The turn in flight is dropped with the rest, so a snapshot arriving
        // afterwards starts a fresh bubble rather than updating one that is no
        // longer in the list.
        [Fact]
        public void ReplacingHistoryClearsTheTurnInFlight()
        {
            var session = Session();

            session.OnAgentEvent("agent", AgentText("live"));
            session.SetHistory(Turns("loaded"));
            session.OnAgentEvent("agent", AgentText("after"));

            Assert.Equal(new[] { "loaded", "after" }, session.History.Select(t => t.Text));
        }

        [Fact]
        public void AnEmptyHistoryIsNotWorthReplacingWith()
        {
            var session = Session();
            var replaced = 0;
            session.HistoryReplaced += () => replaced++;

            session.SetHistory(Turns());

            Assert.Equal(0, replaced);
        }

        // Older turns go on the front and report how many, because the panel has
        // to put the scroll position back afterwards — content appearing above
        // where you are reading would throw you down the page.
        [Fact]
        public void OlderTurnsArePrependedAndCounted()
        {
            var session = Session();
            var prepended = 0;
            session.HistoryPrepended += n => prepended = n;

            session.SetHistory(Turns("newer"));
            session.PrependHistory(Turns("older", "middling"));

            Assert.Equal(new[] { "older", "middling", "newer" }, session.History.Select(t => t.Text));
            Assert.Equal(2, prepended);
        }

        [Fact]
        public void PrependingNothingRaisesNothing()
        {
            var session = Session();
            var raised = false;
            session.HistoryPrepended += _ => raised = true;

            session.PrependHistory(Turns());

            Assert.False(raised);
        }

        // Loaded turns are complete: they already happened, so nothing should
        // draw them as still being typed.
        [Fact]
        public void LoadedTurnsAreAlreadyComplete()
        {
            var session = Session();

            session.SetHistory(Turns("one"));
            session.PrependHistory(Turns("older"));

            Assert.All(session.History, turn => Assert.True(turn.IsComplete));
        }

        // The speaker survives the round trip, which is what draws the right
        // initials and colour on a room's bubbles.
        [Fact]
        public void TheSpeakerAndColourSurvive()
        {
            var session = Session();

            session.SetHistory(Turns("one"));

            Assert.Equal("main", session.History[0].Speaker);
            Assert.Equal("#7f7", session.History[0].SpeakerColor);
        }

        // --- state ---

        [Fact]
        public void AStateChangeIsAnnouncedOnce()
        {
            var session = Session();
            var seen = new List<RemoteChatState>();
            session.StateChanged += seen.Add;

            // Starts Connected, so the first change has to be to something else
            // or the "no event when unchanged" rule would hide a real bug.
            session.SetState(RemoteChatState.Error);
            session.SetState(RemoteChatState.Error);
            session.SetState(RemoteChatState.Connecting);

            Assert.Equal(new[] { RemoteChatState.Error, RemoteChatState.Connecting }, seen);
        }

        [Fact]
        public void TheDisplayNameCanImproveAfterTheSessionExists()
        {
            var session = Session();

            // agents.list arrives moments after the connection, so a panel
            // opened in that window would otherwise keep the raw id forever.
            session.DisplayName = "Lilibeth";

            Assert.Equal("Lilibeth", session.DisplayName);
        }

        // The composer hint is how the panel says replying is switched off,
        // rather than a send that silently does nothing.
        [Fact]
        public void TheComposerHintSaysWhetherReplyingIsOn()
        {
            var session = Session();

            ClaudeBuddySettings.OpenClawReplyEnabled = true;
            Assert.Equal("Message…", session.ComposerHint);

            ClaudeBuddySettings.OpenClawReplyEnabled = false;
            Assert.Equal("Replying is off", session.ComposerHint);
        }

        [Fact]
        public void TheGatewayKeyKeepsItsOwnNamespaceSeparateFromTheSessionId()
        {
            var session = Session();

            Assert.Equal("openclaw:agent:main:main", session.SessionId);
            Assert.Equal("agent:main:main", session.GatewayKey);
        }
    }
}
