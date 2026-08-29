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
    [Collection("Settings")]
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
                .Select(t => new HistoryTurn(t.Role, t.Text, null, "", T0.AddMinutes(t.Minute),
                              null, null))
                .ToList());
        }

        // The same, for the cases that turn on who the gateway said sent a turn.
        // Kept separate rather than widening Give: every case above is about the
        // rules that hold when nothing said, and adding two more slots to their
        // tuples would bury that.
        private static void GiveAttributed(
            OpenClawChatSession session,
            params (ChatRole Role, string Text, int Minute, bool Mine, string? Speaker)[] turns)
        {
            session.SetHistory(turns
                .Select(t => new HistoryTurn(t.Role, t.Text, null, "", T0.AddMinutes(t.Minute),
                              t.Speaker, null, t.Mine))
                .ToList());
        }

        private static OpenClawSessions.Delivery Address(string account) =>
            new("discord", "channel:900", account);

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

        // --- attribution the gateway supplied ---

        // Your own message, drawn as yours. Everything above this line is about
        // the rules that hold when nothing said who — and they still hold, which
        // is why every one of those cases passes unchanged. This is what happens
        // when something does.
        [Fact]
        public void YourOwnMessageIsDrawnAsYours()
        {
            var quill = Member("quill");
            GiveAttributed(quill, (ChatRole.User, "what is the status", 1, true, null));

            var room = Room((quill, "Quill", "#7f7"));

            var turn = Assert.Single(room.History);
            Assert.True(turn.Mine);
            Assert.Equal(ChatRole.User, turn.Role);
            Assert.Null(turn.Speaker);
        }

        // The three copies of one message you sent, which is the shape a real
        // room send leaves behind: the carrier's transcript holds what you
        // typed, every other member's holds the mirror with its prefix already
        // taken off by the parser, and this window's own optimistic copy is the
        // same text again. All three normalise to one string, so one bubble.
        //
        // Before the prefix came off, the last two matched nothing and a
        // successful send drew twice — once plain, once prefixed.
        [Fact]
        public void TheCarrierAndMirrorCopiesOfYourMessageAreOneBubble()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            GiveAttributed(quill, (ChatRole.User, "anyone free to look at the build?", 1, true, null));
            GiveAttributed(aster, (ChatRole.User, "anyone free to look at the build?", 2, true, null));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            var turn = Assert.Single(room.History);
            Assert.True(turn.Mine);
        }

        // Your message is not an agent's, however closely it happens to match
        // one. The echo test runs after the Mine test on purpose: a message
        // swallowed for coincidentally opening the way an agent opened a
        // paragraph would be your message, gone, with the app having decided
        // somebody else said it.
        [Fact]
        public void YourMessageIsNotSwallowedByAnAgentThatSaidTheSameThing()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            Give(quill, (ChatRole.Assistant, "Rebuilding the index now", 1));
            GiveAttributed(aster, (ChatRole.User, "Rebuilding the index now", 2, true, null));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            Assert.Equal(2, room.History.Count);
            Assert.Contains(room.History, t => t.Mine);
        }

        // Somebody the gateway named: an agent relayed through the channel whose
        // own session is not in this room, or another person in it. Both are
        // "somebody who is not you", and both are better than the anonymous turn
        // this used to draw.
        [Fact]
        public void ANamedSenderIsAttributedRatherThanDrawnAnonymously()
        {
            var quill = Member("quill");
            GiveAttributed(quill, (ChatRole.User, "Nodes are loaded.", 1, false, "Thistle"));

            var room = Room((quill, "Quill", "#7f7"));

            var turn = Assert.Single(room.History);
            Assert.Equal("Thistle", turn.Speaker);
            Assert.False(turn.Mine);
            Assert.Equal(ChatRole.Assistant, turn.Role);
        }

        // ...with no colour. It is a Discord display name and the ring colours
        // are keyed by agent id, so borrowing one would say two different
        // speakers were the same agent. An initials chip is the honest answer.
        [Fact]
        public void ANamedSenderGetsNoBorrowedColour()
        {
            var quill = Member("quill");
            GiveAttributed(quill, (ChatRole.User, "Nodes are loaded.", 1, false, "Thistle"));

            var room = Room((quill, "Quill", "#7f7"));

            Assert.Null(Assert.Single(room.History).SpeakerColor);
        }

        // A named echo of an agent that *is* in this room is still an echo. The
        // first pass has already attributed that message in the agent's own
        // colour, from its own transcript, which is a better answer than the
        // display name on the copy — so the copy is dropped exactly as it was
        // before names existed.
        [Fact]
        public void ANamedEchoOfAnAgentInThisRoomIsStillDropped()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            Give(quill, (ChatRole.Assistant, "Nodes loaded and ready", 1));
            GiveAttributed(aster, (ChatRole.User, "Nodes loaded and ready", 1, false, "Quillbot"));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

            var turn = Assert.Single(room.History);
            Assert.Equal("Quill", turn.Speaker);
            Assert.Equal("#7f7", turn.SpeakerColor);
        }

        // One named person's message reaching every agent in the room is still
        // one message. The dedupe set is shared with everything else here, which
        // is what keeps that true without a second rule.
        [Fact]
        public void ANamedSendersMessageIsTakenOnceHoweverManyAgentsReceivedIt()
        {
            var quill = Member("quill");
            var aster = Member("aster");

            GiveAttributed(quill, (ChatRole.User, "Morning all.", 1, false, "Thistle"));
            GiveAttributed(aster, (ChatRole.User, "Morning all.", 2, false, "Thistle"));

            var room = Room((quill, "Quill", "#7f7"), (aster, "Aster", "#77f"));

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

        // --- who is in the room ---

        // SetMembers runs on every scan, because membership genuinely changes: an
        // agent that has not spoken lately drops out of the session list, and one
        // that joins has to start being listened to.

        // The same set of people, with better names and colours. agents.list
        // lands after the first connection, so the first SetMembers often has
        // placeholders — and refreshing those must not tear the subscriptions
        // down, or a streaming reply in flight stops redrawing.
        [Fact]
        public void TheSameMembersWithBetterNamesAreRefreshedInPlace()
        {
            var zara = Member("zara");
            zara.HasMore = false;
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            room.SetMembers([(zara, "agent-1", "#111111")]);
            room.SetMembers([(zara, "Zara", "#ff0000")]);

            // Still subscribed: a turn arriving still reaches the room. Rebuild
            // is called directly because the coalescing wrapper needs a
            // dispatcher this suite does not run.
            Give(zara, (ChatRole.Assistant, "still here", 1));
            room.Rebuild();

            var turn = Assert.Single(room.History);
            Assert.Equal("still here", turn.Text);
            Assert.Equal("Zara", turn.Speaker);
            Assert.Equal("#ff0000", turn.SpeakerColor);
        }

        // A member leaving is a different set, so the room is rebuilt from
        // scratch and their messages go with them.
        [Fact]
        public void AMemberThatLeavesStopsBeingShown()
        {
            var zara = Member("zara");
            var kai = Member("kai");
            zara.HasMore = false;
            kai.HasMore = false;
            Give(zara, (ChatRole.Assistant, "from zara", 1));
            Give(kai, (ChatRole.Assistant, "from kai", 2));

            var room = Room((zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00"));
            Assert.Equal(2, room.History.Count);

            room.SetMembers([(zara, "Zara", "#ff0000")]);
            room.Rebuild();

            var turn = Assert.Single(room.History);
            Assert.Equal("from zara", turn.Text);
        }

        // And a member that leaves is unsubscribed, or its transcript keeps
        // driving rebuilds of a room it is no longer part of.
        [Fact]
        public void AMemberThatLeavesStopsBeingListenedTo()
        {
            var zara = Member("zara");
            var kai = Member("kai");
            zara.HasMore = false;
            kai.HasMore = false;

            var room = Room((zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00"));
            room.SetMembers([(zara, "Zara", "#ff0000")]);

            // Kai speaks after leaving. Nothing it says belongs in the room.
            Give(kai, (ChatRole.Assistant, "shouting from outside", 5));
            room.Rebuild();

            Assert.DoesNotContain(room.History, t => t.Text == "shouting from outside");
        }

        // A member that joins and whose loaded history reaches back at least as
        // far as everyone else's contributes its messages, and both are shown.
        [Fact]
        public void AMemberThatJoinsBringsItsBacklogWithIt()
        {
            var zara = Member("zara");
            var kai = Member("kai");
            zara.HasMore = false;
            kai.HasMore = false;
            Give(zara, (ChatRole.Assistant, "from zara", 1));
            Give(kai, (ChatRole.Assistant, "kai was here first", 0),
                      (ChatRole.Assistant, "from kai", 2));

            var room = Room((zara, "Zara", "#ff0000"));
            Assert.Single(room.History);

            room.SetMembers([(zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00")]);
            room.Rebuild();

            Assert.Contains(room.History, t => t.Text == "from zara");
            Assert.Contains(room.History, t => t.Text == "from kai");
        }

        // The other way round, and the case that caught me writing the test
        // above: a member joining with a SHORTER loaded history narrows the
        // window rather than widening it, so a message that was on screen a
        // moment ago is no longer trustworthy and goes away.
        //
        // That is correct — the room must not claim to know who said what in a
        // stretch the new member has not loaded, which is the third of the three
        // shipped bugs named at the top of this file — but it is surprising
        // enough to be worth an assertion of its own rather than being left as
        // an emergent property of two other tests.
        [Fact]
        public void AMemberJoiningWithLessHistoryNarrowsTheWindow()
        {
            var zara = Member("zara");
            var kai = Member("kai");
            zara.HasMore = false;
            kai.HasMore = false;
            Give(zara, (ChatRole.Assistant, "from zara", 1));
            Give(kai, (ChatRole.Assistant, "from kai", 2));

            var room = Room((zara, "Zara", "#ff0000"));
            Assert.Single(room.History);

            room.SetMembers([(zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00")]);
            room.Rebuild();

            // Zara's earlier message is now below the line every member reaches.
            var turn = Assert.Single(room.History);
            Assert.Equal("from kai", turn.Text);
        }

        // An empty room is a real state — every agent in a channel can go quiet
        // at once — and it must not throw on the way there.
        [Fact]
        public void ARoomEmptiedOfEveryoneIsEmptyRatherThanBroken()
        {
            var zara = Member("zara");
            zara.HasMore = false;
            Give(zara, (ChatRole.Assistant, "from zara", 1));

            var room = Room((zara, "Zara", "#ff0000"));

            room.SetMembers([]);
            room.Rebuild();

            Assert.Empty(room.History);
            Assert.False(room.HasMore);
        }

        // --- HasMore ---

        // Any one member with more to fetch means the room has more, because
        // paging that member is what moves the trustworthy line back.
        [Fact]
        public void TheRoomHasMoreIfAnySingleMemberDoes()
        {
            var zara = Member("zara");
            var kai = Member("kai");
            zara.HasMore = false;
            kai.HasMore = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00")]);

            Assert.True(room.HasMore);
        }

        [Fact]
        public void TheRoomHasNoMoreWhenEveryMemberIsExhausted()
        {
            var zara = Member("zara");
            var kai = Member("kai");
            zara.HasMore = false;
            kai.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00")]);

            Assert.False(room.HasMore);
        }

        [Fact]
        public void ARoomWithNoMembersHasNoMore()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            Assert.False(room.HasMore);
        }

        // --- paging ---

        // Nothing to page and nothing to talk to: LoadOlderAsync must report that
        // the window did not move rather than throwing, because the panel reads
        // the answer to decide whether it has hit the top.
        [Fact]
        public async Task PagingARoomWithNothingToFetchReportsNoMovement()
        {
            var zara = Member("zara");
            zara.HasMore = false;
            Give(zara, (ChatRole.Assistant, "from zara", 1));
            var room = Room((zara, "Zara", "#ff0000"));

            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        [Fact]
        public async Task PagingAnEmptyRoomReportsNoMovement()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        // --- sending to the room ---

        // Sending here is not addressed to a member. It goes out through one
        // member's transcript but with delivery on, which posts it to the channel
        // itself, so every agent there receives it the way they receive anything
        // else said in the room. Which member carries it is therefore not a
        // routing decision at all — only a question of whose transcript the send
        // sits in.
        [Fact]
        public async Task WithReplyingOffTheMessageIsRefusedInTheRoom()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = false;

            var zara = Member("zara");
            zara.HasMore = false;
            var room = Room((zara, "Zara", "#ff0000"));
            var before = room.History.Count;

            await room.SendAsync("anyone about?");

            var note = Assert.Single(room.History.Skip(before));
            Assert.Equal(ChatRole.System, note.Role);
            Assert.Contains("Replying is off", note.Text);
        }

        // A channel every agent has gone quiet in has nobody to send through.
        // Said plainly rather than failing silently: from the user's side an
        // empty channel and a broken one look identical otherwise.
        [Fact]
        public async Task ARoomWithNobodyInItSaysSoRatherThanFailingQuietly()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");

            await room.SendAsync("anyone about?");

            var note = Assert.Single(room.History);
            Assert.Equal(ChatRole.System, note.Role);
            Assert.Contains("Nobody is in this channel", note.Text);
        }

        // --- picking the carrier ---

        // Which member carries a room send, as a rule with no gateway, no
        // transcript and no window behind it. It used to be "first by gateway
        // key", chosen for stability on the grounds that the choice is not a
        // routing decision. Half of that survives — the channel post reaches
        // everyone whoever carries it — and half of it did not: the chat.send
        // half wakes exactly one agent, and the member that has been talking to
        // you is a better one to wake than whichever sorts first.

        // Having somewhere to deliver is the only hard requirement, and it is
        // the one the old rule did not have. A member with no address cannot
        // post to the channel at all, so the first-by-key member being the one
        // without an address is precisely how a room send ended up private.
        [Fact]
        public void ThePickSkipsAMemberWithNowhereToDeliver()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (false, (DateTimeOffset?)T0.AddMinutes(9), "agent:aster:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // ...including when it is the one that spoke most recently. Recency
        // orders the members that can post; it does not qualify one that can't.
        [Fact]
        public void TheLatestSpeakerIsSkippedIfItCannotPost()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:aster:discord:channel:900"),
                (false, (DateTimeOffset?)T0.AddMinutes(30), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(0, pick);
        }

        // Among members that can post, the one that spoke last.
        [Fact]
        public void TheMostRecentSpeakerCarriesIt()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:aster:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(30), "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(12), "agent:thorn:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // A member that has never spoken loses to one that has, whatever the
        // order it was listed in — member order changes between scans and must
        // not change the answer.
        [Fact]
        public void AMemberThatHasNeverSpokenLosesToOneThatHas()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)null, "agent:aster:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // With nobody having spoken, the old rule intact: first by gateway key,
        // so a freshly opened room picks the same member every time rather than
        // whichever the list happened to arrive in.
        [Fact]
        public void WithNobodyHavingSpokenTheKeyDecides()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)null, "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)null, "agent:aster:discord:channel:900"),
            });

            // "agent:aster:…" sorts before "agent:quill:…" whichever order they
            // were listed in.
            Assert.Equal(1, pick);
        }

        // ...and the same when two members last spoke at the same moment, which
        // is what the key is a tiebreak for.
        [Fact]
        public void TwoMembersThatSpokeAtOnceAreSeparatedByTheKey()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(5), "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)T0.AddMinutes(5), "agent:aster:discord:channel:900"),
            });

            Assert.Equal(1, pick);
        }

        // Nobody can post. A refusal rather than a fallback: sending through a
        // member with no address is the silent private delivery this ticket is
        // about, and -1 is what makes the caller say so out loud.
        [Fact]
        public void ARoomWhereNobodyCanPostRefuses()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (false, (DateTimeOffset?)T0.AddMinutes(1), "agent:aster:discord:channel:900"),
                (false, (DateTimeOffset?)T0.AddMinutes(2), "agent:quill:discord:channel:900"),
            });

            Assert.Equal(-1, pick);
        }

        // A member that has never spoken, listed *after* one that has. The same
        // answer as the case above and reached down the other side of the
        // comparison, which is the whole reason both are here: member order
        // changes between scans, and a rule that only holds in one order is a
        // rule that holds half the time.
        [Fact]
        public void AMemberThatHasNeverSpokenLosesFromEitherPosition()
        {
            var pick = OpenClawRoomChatSession.PickCarrier(new[]
            {
                (true, (DateTimeOffset?)T0.AddMinutes(1), "agent:quill:discord:channel:900"),
                (true, (DateTimeOffset?)null, "agent:aster:discord:channel:900"),
            });

            Assert.Equal(0, pick);
        }

        [Fact]
        public void AnEmptyRoomRefusesToo()
        {
            Assert.Equal(-1, OpenClawRoomChatSession.PickCarrier(
                Array.Empty<(bool, DateTimeOffset?, string)>()));
        }

        // --- who spoke last, from a real transcript ---

        // The function that *produces* the timestamps PickCarrier orders by.
        //
        // Covered separately because covering PickCarrier is not covering this:
        // every carrier case above hands timestamps straight in, and every
        // SendAsync case builds members with no history at all — so the loop in
        // here only ever ran over an empty list and "newest" was asserted
        // nowhere. This is the half of the fix that decides which agent actually
        // receives the message.

        // Several turns, and the newest wins — not the first walked past, and
        // not the last. Deliberately out of order in the transcript, because a
        // loop that returned either end would pass with them sorted.
        [Fact]
        public void TheNewestThingAMemberSaidIsWhatCounts()
        {
            var quill = Member("quill");
            Give(quill,
                (ChatRole.Assistant, "starting", 3),
                (ChatRole.Assistant, "still going", 40),
                (ChatRole.Assistant, "nearly there", 12));

            Assert.Equal(T0.AddMinutes(40), OpenClawRoomChatSession.LastSpoke(quill));
        }

        // ...and with the newest first in the list, so neither end of the walk
        // is the answer by accident.
        [Fact]
        public void TheOrderTheTurnsAreStoredInDoesNotDecideIt()
        {
            var quill = Member("quill");
            Give(quill,
                (ChatRole.Assistant, "nearly there", 40),
                (ChatRole.Assistant, "starting", 3),
                (ChatRole.Assistant, "still going", 12));

            Assert.Equal(T0.AddMinutes(40), OpenClawRoomChatSession.LastSpoke(quill));
        }

        // A user turn in a member's transcript is somebody else's message
        // arriving, not that member speaking. Counting those would make "who
        // spoke last" mean "who was spoken to last", which is the same answer
        // for every member in the room and so no answer at all.
        [Fact]
        public void BeingSpokenToIsNotSpeaking()
        {
            var quill = Member("quill");
            Give(quill,
                (ChatRole.Assistant, "on it", 5),
                (ChatRole.User, "any update?", 50));

            Assert.Equal(T0.AddMinutes(5), OpenClawRoomChatSession.LastSpoke(quill));
        }

        // A member that has only ever been spoken to has not spoken, which is
        // null rather than "a long time ago" — the distinction PickCarrier's
        // Compare exists for.
        [Fact]
        public void AMemberThatHasOnlyListenedHasNotSpoken()
        {
            var quill = Member("quill");
            Give(quill, (ChatRole.User, "anyone about?", 1));

            Assert.Null(OpenClawRoomChatSession.LastSpoke(quill));
        }

        [Fact]
        public void AMemberWithNoTranscriptAtAllHasNotSpoken()
        {
            Assert.Null(OpenClawRoomChatSession.LastSpoke(Member("quill")));
        }

        // --- what a failure says ---

        // Three sentences, because there are three different truths and the
        // difference is what the person needs. One "couldn't send" covering all
        // three would have somebody re-type a message that is already in the
        // channel.
        [Fact]
        public void NoAddressSaysNothingWasSentAndWhy()
        {
            Assert.Equal(
                "Couldn't post to #general: no member of this channel carries a delivery address.",
                OpenClawSessions.NoAddressInRoom("#general"));
        }

        [Fact]
        public void AFailedPostSaysNothingWasSent()
        {
            Assert.Equal(
                "Couldn't post to #general: the gateway said no. Nothing was sent.",
                OpenClawSessions.PostFailed("#general", "the gateway said no"));
        }

        // The one that must not say "nothing was sent", because something was:
        // the channel has the message and only the handoff to an agent failed.
        [Fact]
        public void AFailedHandoffSaysTheChannelAlreadyHasIt()
        {
            var note = OpenClawSessions.HandoffFailed("#general", "Quill", "timed out");

            Assert.Equal("Posted to #general, but couldn't hand it to Quill: timed out.", note);
            Assert.DoesNotContain("Nothing was sent", note);
        }

        // --- sending, end to end, with no gateway behind it ---

        // Your own message goes into the *room's* transcript, not into whichever
        // member's session carried it. It used to go into the carrier's, which
        // read as the right thing while the room was a view over its members —
        // but a note about a failure written there is invisible in the merge,
        // and the message and its explanation belong together.
        [Fact]
        public async Task WhatYouTypeAppearsInTheRoomRatherThanInAMembersTranscript()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            Assert.Contains(room.History, t => t.Role == ChatRole.User && t.Text == "anyone about?");
            Assert.Empty(quill.History);
        }

        // ...and it is yours, so the panel draws it in your colour and on your
        // side. ChatRole.User with no Speaker is exactly what does that.
        [Fact]
        public async Task WhatYouTypeIsMarkedAsYours()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            var mine = Assert.Single(room.History, t => t.Text == "anyone about?");
            Assert.True(mine.Mine);
            Assert.Equal(ChatRole.User, mine.Role);
            Assert.Null(mine.Speaker);
        }

        // The refusal this ticket exists for. Every member is in the channel and
        // none of them has an address — which is what a room whose members have
        // all been quiet longer than the recency window looked like — so nothing
        // is sent, and it says so instead of delivering privately to one agent.
        [Fact]
        public async Task ARoomWithNoAddressAnywhereRefusesAndSendsNothing()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            var aster = Member("aster");
            quill.HasMore = false;
            aster.HasMore = false;

            var room = Room((quill, "Quill", "#ff0000"), (aster, "Aster", "#00ff00"));

            await room.SendAsync("anyone about?");

            Assert.Contains(room.History, t =>
                t.Role == ChatRole.System
                && t.Text == OpenClawSessions.NoAddressInRoom("#general"));

            // Nothing reached either member: no transcript, no send.
            Assert.Empty(quill.History);
            Assert.Empty(aster.History);
        }

        // A member that *can* post, with no gateway to post through. A different
        // sentence from the one above, and both of them end up in the room.
        [Fact]
        public async Task WithNoGatewayTheFailureIsSaidInTheRoom()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            var note = Assert.Single(room.History, t => t.Role == ChatRole.System);
            Assert.StartsWith("Couldn't post to #general:", note.Text);
            Assert.Contains("Nothing was sent", note.Text);
        }

        // A note has to survive the next rebuild, and until now none did.
        // Rebuild throws the whole transcript away and re-merges from the
        // members, so the "Replying is off" note this class has always written
        // lasted only until any member event landed — which is any moment at all
        // in a busy channel.
        [Fact]
        public async Task ANoteSurvivesTheNextRebuild()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = false;

            var quill = Member("quill");
            quill.HasMore = false;
            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");
            Assert.Contains(room.History, t => t.Text.Contains("Replying is off"));

            room.Rebuild();

            Assert.Contains(room.History, t => t.Text.Contains("Replying is off"));
        }

        // ...and stops surviving the moment the gateway's own copy of it turns
        // up, which is what keeps a sent message from being drawn twice — once
        // optimistically and once for real — for as long as the panel is open.
        [Fact]
        public async Task YourMessageGivesWayToTheGatewaysOwnCopyOfIt()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");

            // The gateway records it and the member's transcript reloads with it
            // in, which is what happens a second or two after any real send.
            GiveAttributed(quill, (ChatRole.User, "anyone about?", 3, true, null));
            room.Rebuild();

            var mine = Assert.Single(room.History, t => t.Text == "anyone about?");
            Assert.True(mine.Mine);

            // ...and it is the gateway's copy that survived, timestamped by the
            // gateway rather than by this window.
            Assert.Equal(T0.AddMinutes(3), mine.At);
        }

        // The dedupe predicate walks past turns that are not yours without
        // matching them. Obvious, and uncovered until now because every case
        // that reached it had nothing else in the room to walk past — so the
        // arm that says "this merged turn is somebody else's, keep looking" had
        // never been taken.
        [Fact]
        public async Task SomebodyElsesIdenticalWordsDoNotRetireYourOwnMessage()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            // An agent that happens to have said the same words, attributed to
            // it in pass one — which is not your message coming back.
            Give(quill, (ChatRole.Assistant, "anyone about?", 1));

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");
            room.Rebuild();

            // Both survive: Quill's, attributed, and yours.
            Assert.Contains(room.History, t => t.Speaker == "Quill" && !t.Mine);
            Assert.Contains(room.History, t => t.Mine);
        }

        // The room's own list is bounded like every other transcript here. Small
        // on purpose — these are only ever the last few things this window did —
        // and a room left open for a long afternoon of failed sends must not
        // accumulate them without limit.
        [Fact]
        public async Task TheRoomsOwnTurnsAreBounded()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = false;

            var quill = Member("quill");
            quill.HasMore = false;
            var room = Room((quill, "Quill", "#ff0000"));

            for (var i = 0; i < 40; i++) await room.SendAsync("attempt " + i);

            room.Rebuild();

            Assert.True(room.History.Count <= 32, $"kept {room.History.Count}");
        }

        // ...and so does the message you typed, until the gateway's own copy of
        // it turns up.
        [Fact]
        public async Task YourMessageSurvivesTheNextRebuild()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var quill = Member("quill");
            quill.HasMore = false;
            quill.Delivery = Address("quillbot");

            var room = Room((quill, "Quill", "#ff0000"));

            await room.SendAsync("anyone about?");
            room.Rebuild();

            Assert.Single(room.History, t => t.Text == "anyone about?");
        }

        // --- state and identity ---

        // Raised directly rather than posted, unlike the per-agent session's: a
        // room's state changes only when something already on the UI thread
        // rebuilt it, so there is no thread to hop off.
        [Fact]
        public void SettingTheStateRaisesStateChanged()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            RemoteChatState? seen = null;
            room.StateChanged += s => seen = s;

            room.SetState(RemoteChatState.Error);

            Assert.Equal(RemoteChatState.Error, seen);
            Assert.Equal(RemoteChatState.Error, room.State);
        }

        // The same state twice raises nothing. Every scan calls this, so a room
        // sitting connected would otherwise raise several times a second for the
        // panel to react to.
        [Fact]
        public void SettingTheSameStateAgainRaisesNothing()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetState(RemoteChatState.Error);

            var raised = 0;
            room.StateChanged += _ => raised++;
            room.SetState(RemoteChatState.Error);

            Assert.Equal(0, raised);
        }

        // Deliberately nothing: a room is a view, and an agent's own run is
        // stopped from its own orb. Asserted so that "does nothing" stays a
        // decision on record rather than an empty method somebody fills in.
        [Fact]
        public void CancelDoesNothing()
        {
            var zara = Member("zara");
            zara.HasMore = false;
            Give(zara, (ChatRole.Assistant, "hello", 1));
            var room = Room((zara, "Zara", "#ff0000"));
            var before = room.History.Count;

            room.Cancel();

            Assert.Equal(before, room.History.Count);
        }

        [Fact]
        public void TheRoomKeepsTheSessionIdItWasGiven()
        {
            var room = new OpenClawRoomChatSession("openclaw:room:discord:7", "#general");

            Assert.Equal("openclaw:room:discord:7", room.SessionId);
        }

        // --- paging the member that constrains the view ---

        // The room can only show the stretch every member has loaded, so paging
        // means paging whichever member starts LATEST — that is the one holding
        // the line up. Picking any other fetches history nobody can see yet.
        //
        // With no gateway the fetch itself fails, which is what makes this safe to
        // run: what is exercised is choosing the member and asking, not the answer.
        [Fact]
        public async Task PagingAsksTheMemberThatIsHoldingTheLineUp()
        {
            var early = Member("early");
            var late = Member("late");
            early.HasMore = true;
            late.HasMore = true;
            Give(early, (ChatRole.Assistant, "from ages ago", 1));
            Give(late, (ChatRole.Assistant, "from just now", 100));

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(early, "Early", "#ff0000"), (late, "Late", "#00ff00")]);
            room.Rebuild();

            // Nothing moves, because there is no gateway to fetch from — the point
            // is that it chose a member and asked without throwing, and reported
            // the window as unmoved.
            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        // A member with more to fetch but nothing loaded yet cannot be the
        // constraint — there is no "earliest" to compare — so it is skipped rather
        // than chosen and endlessly re-asked.
        [Fact]
        public async Task AMemberWithNothingLoadedIsNotChosenAsTheConstraint()
        {
            var empty = Member("empty");
            empty.HasMore = true;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(empty, "Empty", "#ff0000")]);
            room.Rebuild();

            Assert.False(await room.LoadOlderAsync(CancellationToken.None));
        }

        // Deepening runs the same paging in the background when a room is first
        // opened, because the members' first pages rarely cover the same stretch
        // and the room can only show where they overlap. With nothing to fetch it
        // stops on the first round rather than spinning through all eight.
        [Fact]
        public async Task DeepeningARoomWithNothingToFetchStopsImmediately()
        {
            var zara = Member("zara");
            zara.HasMore = false;
            Give(zara, (ChatRole.Assistant, "hello", 1));

            var room = Room((zara, "Zara", "#ff0000"));

            await room.DeepenAsync();
        }
    }
}
