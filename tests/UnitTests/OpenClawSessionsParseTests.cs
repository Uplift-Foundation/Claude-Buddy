using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // Turning the gateway's sessions.list into the orbs on screen.
    //
    // This reads a shape another program defines, which CLAUDE.md says to cover
    // at the parsing level as well as at the seam, and it is the widest such
    // parser in the app: which orbs exist, what each is called, whether it is a
    // room or a DM, who is standing in each room, and where a reply would be
    // delivered all come out of this one walk.
    //
    // The payloads below are shaped like the real ones the docs record — 84
    // sessions across 8 agents on a live gateway, with origin absent on 12 of 70
    // in one measurement — rather than invented. Where a field is optional here,
    // it is because it was observed missing.
    //
    // Parse takes its clock as an argument, which is what makes the recency
    // filter assertable; everything else it needs comes from settings, and this
    // assembly's TestBootstrap already points those at a temp directory.
    [Collection("Settings")]
    public class OpenClawSessionsParseTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

        private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        private static long Ms(DateTime when) => new DateTimeOffset(when).ToUnixTimeMilliseconds();

        // Recent enough to survive any recency filter, so a test about something
        // else is not accidentally a test about the filter.
        private static long JustNow => Ms(Now.AddSeconds(-5));

        // Both settings Parse reads are set on every call rather than left to
        // whatever ran before. ClaudeBuddySettings is a process-wide static and
        // xUnit orders a class's tests as it pleases, so a test that mutated
        // them would silently change the meaning of every test after it — which
        // is exactly what the first draft of this file did: half of these failed
        // because another case had turned heartbeats off, and `agent:<id>:main`
        // is a heartbeat key.
        private static (IReadOnlyList<OpenClawSessions.Session> Sessions, int Total) Parse(
            string json, bool heartbeats = true, int withinMinutes = ClaudeBuddySettings.OpenClawActiveWithinAll)
        {
            ClaudeBuddySettings.OpenClawHeartbeatMode =
                heartbeats ? ClusterMode.WithChats : ClusterMode.Hidden;
            ClaudeBuddySettings.OpenClawActiveWithinMinutes = withinMinutes;

            return OpenClawSessions.Parse(Json(json), Now);
        }

        // --- the envelope ---

        // The list arrives under any of four property names depending on the
        // gateway's mood, and as a bare array in some replies. All five have to
        // work or the app shows no orbs at all with nothing to say about it.
        [Theory]
        [InlineData("sessions")]
        [InlineData("items")]
        [InlineData("rows")]
        [InlineData("list")]
        public void TheSessionListIsFoundUnderAnyOfItsNames(string property)
        {
            var json = $$"""
                {"{{property}}":[{"key":"agent:main:main","lastActivityAt":{{JustNow}}}]}
                """;

            var (sessions, total) = Parse(json);

            Assert.Single(sessions);
            Assert.Equal(1, total);
        }

        [Fact]
        public void ABareArrayIsAlsoAList()
        {
            var (sessions, _) = Parse($$"""[{"key":"agent:main:main","lastActivityAt":{{JustNow}}}]""");

            Assert.Single(sessions);
        }

        // Anything that is not a list is no sessions rather than a throw. A
        // gateway reply this app does not understand must not take the orbs down.
        [Theory]
        [InlineData("{}")]
        [InlineData("""{"sessions":{}}""")]
        [InlineData("7")]
        [InlineData("null")]
        public void SomethingThatIsNotAListIsNoSessions(string json)
        {
            var (sessions, total) = Parse(json);

            Assert.Empty(sessions);
            Assert.Equal(0, total);
        }

        // The total counts every row the gateway sent, not the ones that
        // survived filtering — it is how the status line says "12 of 84".
        [Fact]
        public void TheTotalCountsEveryRowIncludingFilteredOnes()
        {
            var stale = Ms(Now.AddDays(-30));
            var json = $$"""
                {"sessions":[
                  {"key":"agent:main:main","lastActivityAt":{{JustNow}}},
                  {"key":"agent:zara:main","lastActivityAt":{{stale}}}
                ]}
                """;

            var (sessions, total) = Parse(json);

            Assert.Equal(2, total);
            Assert.True(sessions.Count <= 2);
        }

        // A row with no key is not a session — there is nothing to address it by,
        // and nothing downstream could open a chat for it.
        [Theory]
        [InlineData("""{"sessions":[{"lastActivityAt":1}]}""")]
        [InlineData("""{"sessions":[{"key":""}]}""")]
        [InlineData("""{"sessions":[7,"text",null]}""")]
        public void ARowWithNoKeyIsSkipped(string json)
        {
            Assert.Empty(Parse(json).Sessions);
        }

        // Either spelling of the key, because both have been seen.
        [Fact]
        public void EitherSpellingOfTheKeyIsAccepted()
        {
            var (sessions, _) = Parse(
                $$"""{"sessions":[{"sessionKey":"agent:main:main","lastActivityAt":{{JustNow}}}]}""");

            Assert.Single(sessions);
            Assert.Equal("agent:main:main", sessions[0].Key);
        }

        // --- titles ---

        // One agent commonly has a DM with you, a DM with somebody else and two
        // channels at once, so the title carries where as well as who —
        // repeating "Lilibeth — discord" four times identifies nothing.
        [Fact]
        public void ATitleNamesTheAgentAndWhereTheConversationIs()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:lilibeth:discord:channel:1474",
                              "groupChannel":"#general",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal("lilibeth — #general", sessions[0].Title);

        }

        // Session.Channel is NOT the channel name, despite the name — it is the
        // transport a session arrived over, read from origin.provider and falling
        // back to lastChannel. The channel name is in the Title, via groupChannel.
        //
        // Asserted here because the field reads like it holds "#general" and does
        // not, which is the sort of thing that gets grouped on by mistake. Nothing
        // in the app reads it today; this is what it would find if it did.
        [Fact]
        public void SessionChannelIsTheTransportRatherThanTheChannelName()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:lilibeth:discord:channel:1474",
                              "groupChannel":"#general",
                              "origin":{"provider":"discord"},
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal("discord", sessions[0].Channel);
            Assert.Contains("#general", sessions[0].Title);
        }

        // With no provider and no lastChannel it is empty rather than null, so a
        // caller comparing it never has to null-check first.
        [Fact]
        public void AnUnknownTransportIsEmptyRatherThanNull()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:lilibeth:discord:channel:1474",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal("", sessions[0].Channel);
        }

        // groupChannel is asked before origin.label, because the label is written
        // for a log and its shape can change while this field cannot.
        [Fact]
        public void TheChannelNameIsPreferredOverTheLogLabel()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1474",
                              "groupChannel":"#arch",
                              "origin":{"label":"#general channel id:1474"},
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Contains("#arch", sessions[0].Title);
            Assert.DoesNotContain("#general", sessions[0].Title);
        }

        // origin.label is the fallback, and it needs unpicking: it is written as
        // "#general channel id:1474991965354463274" and only the front is useful.
        [Theory]
        [InlineData("#general channel id:1474991965354463274", "#general")]
        [InlineData("wtvamp user id:246722755112861696", "wtvamp")]
        [InlineData("discord:amber", "amber")]
        [InlineData("engineering group id:99", "engineering")]
        public void TheLogLabelIsCutBackToTheUsefulPart(string label, string want)
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1",
                              "origin":{"label":"{{label}}"},
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal($"main — {want}", sessions[0].Title);
        }

        // With neither, the surface segment of the key is all there is.
        [Fact]
        public void WithNothingElseTheSurfaceNamesTheConversation()
        {
            var (sessions, _) = Parse(
                $$"""{"sessions":[{"key":"agent:main:discord","lastActivityAt":{{JustNow}}}]}""");

            Assert.Equal("main — discord", sessions[0].Title);
        }

        // A cron session is identified by its job rather than by where it runs,
        // and the "Cron: " prefix is dropped because the name after it already
        // says that.
        [Fact]
        public void ACronSessionIsNamedForItsJob()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:cron:2f6c",
                              "label":"Cron: nightly sweep",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal("main — nightly sweep", sessions[0].Title);
        }

        // A name that would repeat itself is said once. "Zara — Zara" is worse
        // than "Zara".
        [Fact]
        public void ATitleThatWouldRepeatItselfIsSaidOnce()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:zara:discord",
                              "groupChannel":"zara",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal("zara", sessions[0].Title);
        }

        // A key that is not agent-shaped falls back to its label and then to
        // itself, rather than producing a blank orb.
        [Fact]
        public void ANonAgentKeyFallsBackToItsLabelThenToItself()
        {
            var (withLabel, _) = Parse(
                $$"""{"sessions":[{"key":"something-else","label":"A job","lastActivityAt":{{JustNow}}}]}""");
            Assert.Equal("A job", withLabel[0].Title);

            var (bare, _) = Parse(
                $$"""{"sessions":[{"key":"something-else","lastActivityAt":{{JustNow}}}]}""");
            Assert.Equal("something-else", bare[0].Title);
        }

        // --- kind ---

        // chatType on the session itself wins over the one inside origin,
        // because origin describes where a conversation came from and was absent
        // on 12 of the 70 sessions this was measured against.
        [Fact]
        public void TheSessionsOwnChatTypeBeatsOrigins()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:x:1",
                              "chatType":"channel",
                              "origin":{"chatType":"direct"},
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal(SessionKind.Channel, sessions[0].Kind);
        }

        [Fact]
        public void OriginsChatTypeIsUsedWhenTheSessionHasNone()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:x:1",
                              "origin":{"chatType":"direct"},
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal(SessionKind.Direct, sessions[0].Kind);
        }

        // --- room membership: the bug this ordering exists for ---

        // Membership is recorded before the recency filter and deliberately
        // ignores it, because "which orbs are worth showing" and "who is in this
        // room" are different questions. With it after the filter, an agent who
        // spoke an hour ago was dropped, her transcript never loaded, and the
        // message she had posted survived only as input to the others —
        // anonymous, unmatchable, and drawn as though you had said it.
        [Fact]
        public void ARoomKeepsAMemberTheRecencyFilterDropped()
        {
            var stale = Ms(Now.AddHours(-5));
            var (sessions, _) = Parse($$"""
                {"sessions":[
                  {"key":"agent:main:discord:channel:1474","lastActivityAt":{{JustNow}}},
                  {"key":"agent:amber:discord:channel:1474","lastActivityAt":{{stale}}}
                ]}
                """, withinMinutes: 60);

            // Amber has no orb...
            Assert.DoesNotContain(sessions, s => s.Key.Contains("amber"));

            // ...and is still in the room.
            var members = OpenClawSessions.MembersOfRoom("discord:1474");
            Assert.Contains("agent:amber:discord:channel:1474", members);
            Assert.Contains("agent:main:discord:channel:1474", members);
        }

        // Most recently active first, which is what decides who gets a wedge
        // on the room's orb when more agents are in the channel than an orb can
        // hold — see OpenClawSessions.RoomAvatar. The gateway's own order is
        // whatever it likes and does move between polls, so this is the one
        // place the answer is made stable.
        [Fact]
        public void ARoomsMembersComeBackMostRecentlyActiveFirst()
        {
            Parse($$"""
                {"sessions":[
                  {"key":"agent:amber:discord:channel:1474","lastActivityAt":{{Ms(Now.AddMinutes(-30))}}},
                  {"key":"agent:zara:discord:channel:1474","lastActivityAt":{{JustNow}}},
                  {"key":"agent:main:discord:channel:1474","lastActivityAt":{{Ms(Now.AddMinutes(-5))}}}
                ]}
                """);

            Assert.Equal(
                new[]
                {
                    "agent:zara:discord:channel:1474",
                    "agent:main:discord:channel:1474",
                    "agent:amber:discord:channel:1474",
                },
                OpenClawSessions.MembersOfRoom("discord:1474"));
        }

        // Two members whose last activity is the same instant — which a gateway
        // reporting whole seconds produces all the time. Broken on the key, so
        // the order is the same twice running rather than however the list
        // happened to arrive; an unstable answer here reshuffles a room orb's
        // wedges under a conversation that has not changed.
        [Fact]
        public void MembersTiedOnActivityAreOrderedByKey()
        {
            Parse($$"""
                {"sessions":[
                  {"key":"agent:zara:discord:channel:1474","lastActivityAt":{{JustNow}}},
                  {"key":"agent:amber:discord:channel:1474","lastActivityAt":{{JustNow}}}
                ]}
                """);

            Assert.Equal(
                new[]
                {
                    "agent:amber:discord:channel:1474",
                    "agent:zara:discord:channel:1474",
                },
                OpenClawSessions.MembersOfRoom("discord:1474"));
        }

        // --- where a session delivers ---

        // The same rule as the two above, applied to the address: a member the
        // recency filter dropped has no orb and still has somewhere its messages
        // go. This is CB-27 in one assertion — the delivery was being read off
        // the snapshot, so a channel whose members had all gone quiet had no
        // address at all, and a message typed into it went privately to one
        // agent with nothing in the channel to show for it.
        [Fact]
        public void AMemberOutsideTheWindowHasNoOrbAndStillHasAnAddress()
        {
            var stale = Ms(Now.AddHours(-5));
            var (sessions, _) = Parse($$"""
                {"sessions":[
                  {"key":"agent:quill:discord:channel:900",
                   "lastActivityAt":{{stale}},
                   "deliveryContext":{"channel":"discord","to":"channel:900","accountId":"quillbot"}
                  }
                ]}
                """, withinMinutes: 60);

            Assert.Empty(sessions);

            ClaudeBuddySettings.OpenClawEnabled = true;
            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor(
                "openclaw:agent:quill:discord:channel:900", "Quill")!;

            Assert.NotNull(chat.Delivery);
            Assert.Equal("discord", chat.Delivery!.Channel);
            Assert.Equal("channel:900", chat.Delivery.To);
            Assert.Equal("quillbot", chat.Delivery.AccountId);
        }

        // The accountId in particular, which is the one part of the address that
        // cannot be reconstructed from the room key. It is what makes the
        // gateway suppress a bot's own channel post from that bot's own
        // sessions, so it is what stops a room send reaching the carrier twice.
        [Fact]
        public void EveryMemberOfAChannelCarriesItsOwnAccount()
        {
            Parse($$"""
                {"sessions":[
                  {"key":"agent:quill:discord:channel:901","lastActivityAt":{{JustNow}},
                   "deliveryContext":{"channel":"discord","to":"channel:901","accountId":"quillbot"}
                  },
                  {"key":"agent:thorn:discord:channel:901","lastActivityAt":{{JustNow}},
                   "deliveryContext":{"channel":"discord","to":"channel:901","accountId":"thornbot"}
                  }
                ]}
                """);

            ClaudeBuddySettings.OpenClawEnabled = true;

            var quill = (OpenClawChatSession)OpenClawSessions.ChatFor(
                "openclaw:agent:quill:discord:channel:901", "Quill")!;
            var thorn = (OpenClawChatSession)OpenClawSessions.ChatFor(
                "openclaw:agent:thorn:discord:channel:901", "Thorn")!;

            Assert.Equal("quillbot", quill.Delivery!.AccountId);
            Assert.Equal("thornbot", thorn.Delivery!.AccountId);
        }

        // A known address is never replaced by not knowing one.
        //
        // The same rule, for the same reason, as ChatSpeaker.Resolve: a poll
        // that lost a race, or a gateway that stopped listing a session for a
        // moment, is a gap in what we were told rather than news that the
        // conversation stopped living anywhere. Without this, a panel reopened
        // in that window had its mirror switched off for the rest of the run —
        // and a mirror that silently does not happen is precisely the failure
        // this ticket is about.
        [Fact]
        public void AKnownAddressSurvivesAPollThatNoLongerCarriesOne()
        {
            Parse($$"""
                {"sessions":[
                  {"key":"agent:quill:discord:channel:902","lastActivityAt":{{JustNow}},
                   "deliveryContext":{"channel":"discord","to":"channel:902","accountId":"quillbot"}
                  }
                ]}
                """);

            ClaudeBuddySettings.OpenClawEnabled = true;
            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor(
                "openclaw:agent:quill:discord:channel:902", "Quill")!;
            Assert.NotNull(chat.Delivery);

            // The same session, now with nothing to say about where it delivers.
            Parse($$"""
                {"sessions":[
                  {"key":"agent:quill:discord:channel:902","lastActivityAt":{{JustNow}}}
                ]}
                """);

            var again = (OpenClawChatSession)OpenClawSessions.ChatFor(
                "openclaw:agent:quill:discord:channel:902", "Quill")!;

            Assert.Same(chat, again);
            Assert.NotNull(again.Delivery);
            Assert.Equal("channel:902", again.Delivery!.To);
        }

        // The other arm of the same rule: a session the delivery map has never
        // heard of falls back to the published snapshot.
        //
        // Worth a case because the fallback is not dead weight. SetSnapshotForTests
        // is the seam every other test publishes sessions through, and a lookup
        // that reached only the map would make all of them pass for the wrong
        // reason — they would be asserting against a chat whose Delivery had
        // silently become null. The comment on the line says so; this is the half
        // of it that was unverified.
        [Fact]
        public void ASessionTheMapHasNotSeenFallsBackToTheSnapshot()
        {
            // A poll that knows about somebody else entirely, so the map is
            // populated and simply does not contain the key asked for next.
            Parse($$"""
                {"sessions":[
                  {"key":"agent:aster:discord:channel:903","lastActivityAt":{{JustNow}},
                   "deliveryContext":{"channel":"discord","to":"channel:903","accountId":"asterbot"}
                  }
                ]}
                """);

            OpenClawSessions.SetSnapshotForTests(new[]
            {
                new OpenClawSessions.Session(
                    "agent:quill:discord:channel:904", "Quill — #lobby", "discord", "idle",
                    Now, new OpenClawSessions.Delivery("discord", "channel:904", "quillbot"),
                    SessionKind.Channel, false),
            });

            ClaudeBuddySettings.OpenClawEnabled = true;
            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor(
                "openclaw:agent:quill:discord:channel:904", "Quill")!;

            Assert.NotNull(chat.Delivery);
            Assert.Equal("channel:904", chat.Delivery!.To);
            Assert.Equal("quillbot", chat.Delivery.AccountId);

            OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        }

        // Every agent gets a colour reserved whether its orb is drawn or not, for
        // the same reason: its messages still appear in a room, and an uncoloured
        // bubble in a coloured conversation reads as a failure rather than an
        // absence.
        [Fact]
        public void AFilteredAgentStillGetsAColour()
        {
            var stale = Ms(Now.AddHours(-5));
            Parse($$"""
                {"sessions":[
                  {"key":"agent:main:discord:channel:1","lastActivityAt":{{JustNow}}},
                  {"key":"agent:amber:discord:channel:1","lastActivityAt":{{stale}}}
                ]}
                """, withinMinutes: 60);

            Assert.False(string.IsNullOrEmpty(OpenClawSessions.ColourForAgent("amber")));
        }

        [Fact]
        public void ARoomHasItsOwnColour()
        {
            Parse($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1474","lastActivityAt":{{JustNow}}}]}
                """);

            Assert.False(string.IsNullOrEmpty(OpenClawSessions.ColourForRoom("discord:1474")));
        }

        [Fact]
        public void ADirectMessageIsNotARoom()
        {
            Parse($$"""
                {"sessions":[{"key":"agent:main:discord:direct:2467","lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Empty(OpenClawSessions.MembersOfRoom("discord:2467"));
        }

        // --- the recency filter ---

        [Fact]
        public void AStaleSessionIsFilteredOut()
        {
            var (sessions, _) = Parse(
                $$"""{"sessions":[{"key":"agent:main:main","lastActivityAt":{{Ms(Now.AddHours(-5))}}}]}""",
                withinMinutes: 60);

            Assert.Empty(sessions);
        }

        [Fact]
        public void ARecentSessionSurvives()
        {
            var (sessions, _) = Parse(
                $$"""{"sessions":[{"key":"agent:main:main","lastActivityAt":{{Ms(Now.AddMinutes(-5))}}}]}""",
                withinMinutes: 60);

            Assert.Single(sessions);
        }

        // With the filter off, age stops mattering at all.
        [Fact]
        public void WithNoRecencyLimitEverythingSurvives()
        {
            var (sessions, _) = Parse(
                $$"""{"sessions":[{"key":"agent:main:main","lastActivityAt":{{Ms(Now.AddDays(-400))}}}]}""");

            Assert.Single(sessions);
        }

        // updatedAt stands in for lastActivityAt, and the later of the two wins —
        // a session is as recent as the most recent thing said about it.
        [Fact]
        public void TheLaterOfTheTwoTimestampsCounts()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:main",
                              "lastActivityAt":{{Ms(Now.AddHours(-9))}},
                              "updatedAt":{{Ms(Now.AddMinutes(-1))}}}]}
                """, withinMinutes: 60);

            Assert.Single(sessions);
        }

        // No timestamp at all is treated as now rather than as the epoch, which
        // would filter out every session that did not report one.
        [Fact]
        public void ASessionWithNoTimestampIsTreatedAsCurrent()
        {
            var (sessions, _) = Parse(
                """{"sessions":[{"key":"agent:main:main"}]}""", withinMinutes: 60);

            Assert.Single(sessions);
        }

        // --- heartbeats ---

        [Fact]
        public void AHeartbeatSessionIsMarkedAsOne()
        {
            var (sessions, _) = Parse(
                $$"""{"sessions":[{"key":"agent:main:main","lastActivityAt":{{JustNow}}}]}""",
                heartbeats: true);

            Assert.True(sessions[0].Heartbeat);
        }

        [Fact]
        public void HeartbeatSessionsCanBeHidden()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[
                  {"key":"agent:main:main","lastActivityAt":{{JustNow}}},
                  {"key":"agent:main:discord:channel:1","lastActivityAt":{{JustNow}}}
                ]}
                """, heartbeats: false);

            Assert.Single(sessions);
            Assert.DoesNotContain(sessions, s => s.Heartbeat);
        }

        // Hidden and still in the room, for the same reason a filtered agent
        // keeps its colour.
        [Fact]
        public void AHiddenHeartbeatIsStillARoomMember()
        {
            Parse($$"""
                {"sessions":[{"key":"agent:main:main","lastActivityAt":{{JustNow}}},
                             {"key":"agent:main:discord:channel:77","lastActivityAt":{{JustNow}}}]}
                """, heartbeats: false);

            Assert.Contains(
                "agent:main:discord:channel:77", OpenClawSessions.MembersOfRoom("discord:77"));
        }

        // --- delivery ---

        // deliveryContext is authoritative; lastChannel/lastTo are what the
        // gateway itself falls back to, so this falls back the same way rather
        // than inventing a rule.
        [Fact]
        public void DeliveryComesFromTheContextWhenThereIsOne()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1",
                              "deliveryContext":{"channel":"discord","to":"1474","accountId":"acc1"},
                              "lastChannel":"slack","lastTo":"C999",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            var delivery = sessions[0].Delivery;
            Assert.NotNull(delivery);
            Assert.Equal("discord", delivery!.Channel);
            Assert.Equal("1474", delivery.To);
            Assert.Equal("acc1", delivery.AccountId);
        }

        [Fact]
        public void DeliveryFallsBackToTheLastChannelAndRecipient()
        {
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1",
                              "lastChannel":"discord","lastTo":"1474",
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Equal("discord", sessions[0].Delivery!.Channel);
            Assert.Equal("1474", sessions[0].Delivery!.To);
        }

        // Half an address is no address. A reply needs both a channel and a
        // recipient, and sending to one without the other would go nowhere while
        // looking like it had gone somewhere.
        [Theory]
        [InlineData("""{"lastChannel":"discord"}""")]
        [InlineData("""{"lastTo":"1474"}""")]
        [InlineData("""{"lastChannel":"","lastTo":"1474"}""")]
        [InlineData("{}")]
        public void HalfAnAddressIsNoAddress(string fields)
        {
            var trimmed = fields == "{}" ? "" : "," + fields.Trim('{', '}');
            var (sessions, _) = Parse($$"""
                {"sessions":[{"key":"agent:main:discord:channel:1"{{trimmed}},
                              "lastActivityAt":{{JustNow}}}]}
                """);

            Assert.Null(sessions[0].Delivery);
        }

        // --- DecodeDataUri: agent avatars arrive inline ---

        [Fact]
        public void ADataUriDecodesToItsBytes()
        {
            var bytes = OpenClawSessions.DecodeDataUri(
                "data:image/png;base64,iVBORw0KGgo=");

            Assert.NotNull(bytes);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a data uri")]
        [InlineData("data:image/png;base64,!!!not base64!!!")]
        public void AnythingThatIsNotADataUriIsNull(string? uri)
        {
            Assert.Null(OpenClawSessions.DecodeDataUri(uri));
        }

        // A well-formed uri carrying nothing decodes to zero bytes rather than to
        // null, which is worth stating because the two are not the same answer:
        // null means "there is no avatar here", and an empty array means "the
        // gateway sent an avatar and it is empty". Asserted as it behaves; a
        // caller that treats an empty image as a real one is the caller's bug to
        // fix, and nothing downstream currently does.
        [Fact]
        public void AnEmptyPayloadDecodesToNoBytesRatherThanToNull()
        {
            Assert.Equal(Array.Empty<byte>(), OpenClawSessions.DecodeDataUri("data:image/png;base64,"));
        }
    }
}
