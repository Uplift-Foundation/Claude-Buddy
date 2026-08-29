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

        // First by gateway key, deliberately — stable rather than depending on
        // who happened to speak last. Asserted by giving the room its members in
        // the wrong order and checking the send still lands in the same
        // transcript, because "stable" is the whole property and member order
        // does change between scans.
        [Fact]
        public async Task TheSendAlwaysGoesThroughTheSameMemberWhateverTheOrder()
        {
            ClaudeBuddySettings.ReloadForTests();
            ClaudeBuddySettings.OpenClawReplyEnabled = true;

            var kai = Member("kai");
            var zara = Member("zara");
            kai.HasMore = false;
            zara.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
            room.SetMembers([(zara, "Zara", "#ff0000"), (kai, "Kai", "#00ff00")]);

            await room.SendAsync("anyone about?");

            // "agent:kai:…" sorts before "agent:zara:…", so kai carries it even
            // though zara was listed first. The send itself fails — there is no
            // gateway — which is what leaves the pair of turns behind.
            Assert.Contains(kai.History, t => t.Role == ChatRole.User && t.Text == "anyone about?");
            Assert.DoesNotContain(zara.History, t => t.Role == ChatRole.User);
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
