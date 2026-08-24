using Xunit;

namespace ClaudeBuddy.Tests
{
    // Merging several agents' transcripts into one room.
    //
    // At 0% across 213 instrumented lines, and reachable with nothing but
    // in-memory sessions: Rebuild touches lists and raises events, and only the
    // coalescing wrapper around it needs a dispatcher.
    //
    // Worth the effort out of proportion to its size, because three separate
    // shipped bugs were fixed in here and all three had the same shape — the room
    // asserting who said something when it did not know:
    //
    //   * Every agent's message drawn twice, once attributed in its own colour
    //     and once as a blue bubble from you. Both arrive as user-role turns in
    //     the transcripts of the agents that *received* them, and nothing in the
    //     payload says which is which.
    //   * The relayed copy sometimes arriving cut short, so equality missed it
    //     and the duplicate came back — one full sentence attributed, one blue
    //     bubble ending at its first colon.
    //   * Historical messages wrong far more often than recent ones, because in
    //     the stretch only some members had paged back to, the missing members'
    //     messages existed solely as echoes with nothing to attribute them
    //     against.
    public class OpenClawRoomChatTests
    {
        private static OpenClawChatSession Member(string agent) =>
            new($"openclaw:agent:{agent}:discord:channel:1", $"agent:{agent}:discord:channel:1", agent);

        private static readonly DateTimeOffset T0 =
            new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        private static void Give(
            OpenClawChatSession session,
            params (ChatRole Role, string Text, int Minute)[] turns)
        {
            session.SetHistory(turns
                .Select(t => (t.Role, t.Text, (string?)null, "", T0.AddMinutes(t.Minute),
                              (string?)null, (string?)null))
                .ToList());
        }

        // Every member has everything there is, so nothing constrains the
        // window — which keeps a test about attribution from also being a test
        // about the trust cutoff.
        private static OpenClawRoomChatSession Room(
            params (OpenClawChatSession Chat, string Agent, string Colour)[] members)
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            foreach (var (chat, _, _) in members) chat.HasMore = false;

            room.SetMembers(members.ToList());
            room.Rebuild();

            return room;
        }

        // --- attribution ---

        // A message found in Zara's transcript in the assistant role is Zara.
        // That is the whole reason to merge rather than show one transcript.
        [Fact]
        public void AnAgentsOwnWordsAreAttributedToIt()
        {
            var zara = Member("zara");
            Give(zara, (ChatRole.Assistant, "Nodes loaded", 1));

            var room = Room((zara, "Zara", "#7f7"));

            Assert.Single(room.History);
            Assert.Equal("Zara", room.History[0].Speaker);
            Assert.Equal("#7f7", room.History[0].SpeakerColor);
        }

        // The first bug. Zara's message is an assistant turn in her own
        // transcript and a *user* turn in Lilibeth's, because Lilibeth received
        // it — so a naive merge shows it twice, once as Zara and once as a blue
        // bubble from the user.
        [Fact]
        public void AnAgentsMessageEchoedIntoAnotherTranscriptIsNotDrawnTwice()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            Give(lilibeth, (ChatRole.User, "Nodes loaded and ready", 1));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Single(room.History);
            Assert.Equal("Zara", room.History[0].Speaker);
        }

        // The second bug. The relayed copy is sometimes cut short, so equality
        // misses it — a long enough prefix has to count, in either direction,
        // because nothing says the stored original is the longer of the two.
        [Fact]
        public void ATruncatedEchoIsStillRecognisedAsTheSameMessage()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "Nodes loaded and ready: 41 of them", 1));
            Give(lilibeth, (ChatRole.User, "Nodes loaded and ready", 1));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Single(room.History);
            Assert.Equal("Zara", room.History[0].Speaker);
        }

        // ...and the other way round, since the truncation can be on either side.
        [Fact]
        public void TheEchoMayBeTheLongerOfTheTwo()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            Give(lilibeth, (ChatRole.User, "Nodes loaded and ready: 41 of them", 1));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Single(room.History);
        }

        // The floor on prefix matching, and the reason for it: below sixteen
        // characters a match means very little, and dropping a person's short
        // reply because an agent happened to open a paragraph the same way is a
        // worse failure than showing one duplicate.
        [Fact]
        public void AShortReplyIsNotSwallowedByAnAgentThatStartedTheSameWay()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "Yes, and here is the long explanation", 1));
            Give(lilibeth, (ChatRole.User, "Yes", 2));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Equal(2, room.History.Count);
        }

        // Matching is on the words alone, because a relayed copy can differ in
        // surrounding whitespace from the one the agent wrote.
        [Fact]
        public void SurroundingWhitespaceDoesNotDefeatTheMatch()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            Give(lilibeth, (ChatRole.User, "  Nodes   loaded\n and ready  ", 1));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Single(room.History);
        }

        // The same message reaches every agent in the room, so it is taken once —
        // keyed on the text rather than the time, because the same message is
        // timestamped per delivery and two agents can record it either side of a
        // minute boundary.
        [Fact]
        public void APersonsMessageIsTakenOnceHoweverManyAgentsReceivedIt()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.User, "what is the status", 1));
            Give(lilibeth, (ChatRole.User, "what is the status", 2));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Single(room.History);
        }

        // The third rule, and the most carefully argued: a message that is not
        // any agent's own is drawn as the room's own voice — left, neutral, no
        // name — rather than in the user's blue. Three things arrive looking
        // identical, and only one of them is the user, so "somebody said this"
        // is true where "you said this" is not.
        [Fact]
        public void AMessageNobodyCanBeCreditedWithIsNotDrawnAsYours()
        {
            var zara = Member("zara");
            Give(zara, (ChatRole.User, "who is handling the deploy", 1));

            var room = Room((zara, "Zara", "#7f7"));

            Assert.Single(room.History);
            Assert.Equal(ChatRole.Assistant, room.History[0].Role);
            Assert.Null(room.History[0].Speaker);
        }

        // Blank turns contribute nothing rather than an unattributed empty
        // bubble.
        [Fact]
        public void BlankTurnsAreDropped()
        {
            var zara = Member("zara");
            Give(zara, (ChatRole.User, "   ", 1), (ChatRole.User, "real", 2));

            var room = Room((zara, "Zara", "#7f7"));

            Assert.Single(room.History);
            Assert.Equal("real", room.History[0].Text);
        }

        // --- order ---

        // Merged by time, not by member, because that is the conversation. A
        // merge has no append: a message from an agent whose backlog arrives late
        // belongs in the middle.
        [Fact]
        public void TheRoomIsInTimeOrderAcrossMembers()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "first", 1), (ChatRole.Assistant, "third", 3));
            Give(lilibeth, (ChatRole.Assistant, "second", 2), (ChatRole.Assistant, "fourth", 4));

            var room = Room((zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f"));

            Assert.Equal(
                new[] { "first", "second", "third", "fourth" },
                room.History.Select(t => t.Text));
        }

        // --- the trust window ---

        // The third bug. A member still holding history back draws a line at the
        // oldest message it has: before that point the room does not know who was
        // talking, and showing less is the honest answer.
        [Fact]
        public void HistoryOlderThanEveryMemberHasLoadedIsNotShown()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "old", 1), (ChatRole.Assistant, "recent", 50));
            Give(lilibeth, (ChatRole.Assistant, "mine", 40));

            // Lilibeth has more to fetch, so nothing before her oldest message
            // can be trusted.
            zara.HasMore = false;
            lilibeth.HasMore = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f") }.ToList());
            room.Rebuild();

            Assert.DoesNotContain(room.History, t => t.Text == "old");
            Assert.Contains(room.History, t => t.Text == "mine");
            Assert.Contains(room.History, t => t.Text == "recent");
        }

        // A member that has reached the beginning of its transcript constrains
        // nothing: it has everything there is, so its silence before some point
        // is real rather than unloaded.
        [Fact]
        public void AMemberWithNothingLeftToFetchConstrainsNothing()
        {
            var zara = Member("zara");
            var lilibeth = Member("lilibeth");

            Give(zara, (ChatRole.Assistant, "old", 1));
            Give(lilibeth, (ChatRole.Assistant, "newer", 40));

            zara.HasMore = false;
            lilibeth.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (zara, "Zara", "#7f7"), (lilibeth, "Lilibeth", "#77f") }.ToList());
            room.Rebuild();

            Assert.Contains(room.History, t => t.Text == "old");
        }

        // --- what the panel is told ---

        // A replaced transcript scrolls the panel to the bottom, which is right
        // when a room opens and exactly wrong when you have just scrolled to the
        // top and asked for more. So older turns on the front are reported as a
        // prepend instead.
        [Fact]
        public void OlderTurnsArrivingOnTheFrontAreReportedAsAPrepend()
        {
            var zara = Member("zara");
            Give(zara, (ChatRole.Assistant, "recent", 50));
            zara.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (zara, "Zara", "#7f7") }.ToList());
            room.Rebuild();

            var prepended = 0;
            var replaced = 0;
            room.HistoryPrepended += n => prepended = n;
            room.HistoryReplaced += () => replaced++;

            // A page of older history arrives.
            Give(zara,
                (ChatRole.Assistant, "older", 10),
                (ChatRole.Assistant, "middling", 20),
                (ChatRole.Assistant, "recent", 50));
            zara.HasMore = false;
            room.Rebuild();

            Assert.Equal(2, prepended);
            Assert.Equal(0, replaced);
        }

        // Anything else is a replacement, which is what a room opening looks
        // like.
        [Fact]
        public void ADifferentConversationIsReportedAsAReplacement()
        {
            var zara = Member("zara");
            Give(zara, (ChatRole.Assistant, "one", 1));
            zara.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers(new[] { (zara, "Zara", "#7f7") }.ToList());
            room.Rebuild();

            var prepended = 0;
            var replaced = 0;
            room.HistoryPrepended += n => prepended = n;
            room.HistoryReplaced += () => replaced++;

            Give(zara, (ChatRole.Assistant, "something else entirely", 2));
            zara.HasMore = false;
            room.Rebuild();

            Assert.Equal(0, prepended);
            Assert.Equal(1, replaced);
        }

        // The merge is deliberately uncapped, and this is why: a cap here trimmed
        // the *front*, which is the end paging adds to — so scrolling up fetched
        // older messages and then dropped them again, and the window could never
        // open past the cap however far you scrolled.
        [Fact]
        public void TheMergeIsNotCappedAtTheFront()
        {
            var zara = Member("zara");
            Give(zara, Enumerable.Range(0, 700)
                .Select(i => (ChatRole.Assistant, $"turn {i}", i))
                .ToArray());
            zara.HasMore = false;

            var room = Room((zara, "Zara", "#7f7"));

            Assert.Equal(700, room.History.Count);
            Assert.Equal("turn 0", room.History[0].Text);
        }

        [Fact]
        public void ARoomWithNoMembersIsEmptyRatherThanAThrow()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            room.SetMembers(Array.Empty<(OpenClawChatSession, string, string)>());
            room.Rebuild();

            Assert.Empty(room.History);
        }

        // "Message the channel…", not the plain "Message…" a single agent's
        // composer shows. Worth pinning rather than loosening to a Contains:
        // sending here posts to the channel itself rather than to one agent, and
        // the hint is the only thing on screen that says so.
        [Fact]
        public void TheComposerHintSaysWhetherReplyingIsOn()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            ClaudeBuddySettings.OpenClawReplyEnabled = true;
            Assert.Equal("Message the channel…", room.ComposerHint);

            ClaudeBuddySettings.OpenClawReplyEnabled = false;
            Assert.Equal("Replying is off", room.ComposerHint);
        }
    }
}
