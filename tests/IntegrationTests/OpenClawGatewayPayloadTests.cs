using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// The gateway's two payloads, end to end, against fixtures derived from what a
// real gateway actually returned.
//
// Here rather than only in tests/UnitTests because this is the seam CLAUDE.md
// says to cover twice: the unit suites check the parsing rules a case at a time,
// and these check the whole exchange — a real sessions.list walked into rooms,
// addresses and orbs, and a real chat.history walked into attributed turns. The
// two fail differently. A unit test catches a field read wrong; only this
// catches a payload whose shape is not what the rules assume.
//
// **Every value in these fixtures is invented.** The structure is real —
// derived field by field from captured payloads, including the fields that were
// observed absent — and the ids, names, channel and sentences are not. This
// repository is public and the gateway is a private Discord server with real
// people in it, so what these are for is the shape, and the shape is all they
// carry.
//
// Serialised on the settings collection because Parse reads the recency window
// and the cluster modes out of the process-wide settings, and ChatFor reads
// whether OpenClaw is on at all.
[Collection("Settings")]
public class OpenClawGatewayPayloadTests
{
    // Nine agents standing in one channel, which is what the room this was
    // diagnosed against actually looked like. Their activity times span five
    // months, so the recency window has something real to cut.
    //
    // The *shape* of that spread is the room's — two members inside the hour,
    // three about a day back, then a week, three months and five — and the
    // instants are nobody's. The captured epochs were shifted onto an unrelated
    // base rather than kept: a millisecond timestamp names no person, channel or
    // credential, but it is still a record of when a private gateway was doing
    // something, and this repository is public. Nothing in the test depends on
    // which day they fall on, only on the distances between them.
    //
    // Two details worth keeping rather than tidying: every member's
    // deliveryContext names the same channel and the same recipient and differs
    // only in accountId, which is the fact the whole fix rests on; and
    // lastActivityAt is genuinely absent on four of the nine, with updatedAt
    // left to answer for them.
    private const string SessionsList = """
        {
          "ts": 1763640000000,
          "count": 9,
          "totalCount": 9,
          "hasMore": false,
          "sessions": [
            {
              "key": "agent:quill:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "webchat",
                "chatType": "direct",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "quillbot"
              },
              "updatedAt": 1763639640000,
              "archived": false,
              "lastActivityAt": 1763575200000,
              "sessionId": "00000000-0000-4000-8000-000000000000",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "quillbot"
              }
            },
            {
              "key": "agent:aster:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "default"
              },
              "updatedAt": 1763639580000,
              "archived": false,
              "lastActivityAt": 1762615200000,
              "sessionId": "00000000-0000-4000-8000-000000000001",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "default"
              }
            },
            {
              "key": "agent:thorn:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "thornbot"
              },
              "updatedAt": 1763517600000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000002",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "thornbot"
              }
            },
            {
              "key": "agent:bram:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "brambot"
              },
              "updatedAt": 1763517540000,
              "archived": false,
              "lastActivityAt": 1763517300000,
              "sessionId": "00000000-0000-4000-8000-000000000003",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "brambot"
              }
            },
            {
              "key": "agent:nib:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "nibbot"
              },
              "updatedAt": 1763517480000,
              "archived": false,
              "lastActivityAt": 1763517240000,
              "sessionId": "00000000-0000-4000-8000-000000000004",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "nibbot"
              }
            },
            {
              "key": "agent:vale:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              },
              "updatedAt": 1763517420000,
              "archived": false,
              "lastActivityAt": 1763517180000,
              "sessionId": "00000000-0000-4000-8000-000000000005",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              }
            },
            {
              "key": "agent:wren:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "wrenbot"
              },
              "updatedAt": 1763121600000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000006",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "wrenbot"
              }
            },
            {
              "key": "agent:fen:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "fenbot"
              },
              "updatedAt": 1754989200000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000007",
              "status": "done",
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "fenbot"
              }
            },
            {
              "key": "agent:moss:discord:channel:900000000000000001",
              "kind": "group",
              "displayName": "discord:900000000000000000#lobby",
              "channel": "discord",
              "groupChannel": "#lobby",
              "space": "900000000000000000",
              "chatType": "channel",
              "origin": {
                "label": "#lobby channel id:900000000000000001",
                "provider": "discord",
                "surface": "discord",
                "chatType": "channel",
                "from": "discord:channel:900000000000000001",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              },
              "updatedAt": 1750420800000,
              "archived": false,
              "lastActivityAt": null,
              "sessionId": "00000000-0000-4000-8000-000000000008",
              "status": null,
              "deliveryContext": {
                "channel": "discord",
                "to": "channel:900000000000000001",
                "accountId": "valebot"
              }
            }
          ]
        }
        """;

    private const string HistoryPage = """
        {
          "sessionKey": "agent:quill:discord:channel:900000000000000001",
          "sessionId": "00000000-0000-4000-8000-000000000000",
          "messages": [
            {
              "role": "user",
              "content": "what did the overnight run say?",
              "timestamp": 1787880000000,
              "__openclaw": {
                "senderIsOwner": true,
                "senderId": "900000000000000101",
                "senderName": "quillfeather",
                "senderUsername": "quillfeather",
                "id": "00000000-0000-4000-8000-0000000000a1",
                "recordTimestampMs": 1787880000200,
                "seq": 1
              }
            },
            {
              "role": "assistant",
              "content": [
                {
                  "type": "text",
                  "text": "Green on both legs."
                }
              ],
              "stopReason": "stop",
              "api": "cli",
              "provider": "claude-cli",
              "model": "claude-sonnet-4-6",
              "timestamp": 1787880001000,
              "idempotencyKey": "cli-assistant:00000000-0000-4000-8000-0000000000a2",
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000a3",
                "idempotencyKey": "cli-assistant:00000000-0000-4000-8000-0000000000a2",
                "recordTimestampMs": 1787880001100,
                "seq": 2
              }
            },
            {
              "role": "user",
              "content": "Nodes are loaded.",
              "timestamp": 1787880002000,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000102",
                "senderName": "Thistle",
                "senderUsername": "Thistle",
                "id": "00000000-0000-4000-8000-0000000000a4",
                "recordTimestampMs": 1787880002100,
                "seq": 3
              }
            },
            {
              "role": "user",
              "content": "anyone free to look at the build?",
              "timestamp": 1787880003000,
              "idempotencyKey": "00000000-0000-4000-8000-0000000000a5:user",
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000a6",
                "idempotencyKey": "00000000-0000-4000-8000-0000000000a5:user",
                "recordTimestampMs": 1787880003100,
                "seq": 4
              }
            },
            {
              "role": "user",
              "content": "**(via Claude Buddy)** anyone free to look at the build?",
              "timestamp": 1787880003050,
              "__openclaw": {
                "senderIsOwner": false,
                "senderId": "900000000000000103",
                "senderName": "Quillbot",
                "senderUsername": "Quillbot",
                "id": "00000000-0000-4000-8000-0000000000a7",
                "recordTimestampMs": 1787880003150,
                "seq": 5
              }
            },
            {
              "role": "user",
              "content": "[Inter-session message] sourceSession=agent:thorn:discord:direct:900000000000000104 sourceChannel=discord sourceTool=sessions_send isUser=false can you take the release notes?",
              "timestamp": 1787880004000,
              "__openclaw": {
                "id": "00000000-0000-4000-8000-0000000000a8",
                "recordTimestampMs": 1787880004100,
                "seq": 6
              }
            }
          ],
          "offset": 0,
          "hasMore": false,
          "totalMessages": 6
        }
        """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // A moment just after the newest session in the fixture, so "the last hour"
    // means something fixed rather than something that depends on when the suite
    // is run.
    private static readonly DateTime Now = new(2025, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private const string Room = "discord:900000000000000001";

    private static (IReadOnlyList<OpenClawSessions.Session> Sessions, int Total) Parse(
        int withinMinutes)
    {
        ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        ClaudeBuddySettings.OpenClawCronMode = ClusterMode.WithChats;
        ClaudeBuddySettings.OpenClawActiveWithinMinutes = withinMinutes;

        return OpenClawSessions.Parse(Json(SessionsList), Now);
    }

    // --- sessions.list, all the way to an address --------------------------

    // Everyone in the channel is a member of the room, however long ago they
    // last said anything. Membership is recorded before the recency filter and
    // deliberately ignores it — an agent that spoke last week is still one of
    // the people standing in the channel.
    [Fact]
    public void EveryAgentInTheChannelIsAMemberOfTheRoom()
    {
        Parse(withinMinutes: 60);

        var members = OpenClawSessions.MembersOfRoom(Room);

        Assert.Equal(9, members.Count);
        Assert.Contains("agent:quill:discord:channel:900000000000000001", members);
        Assert.Contains("agent:moss:discord:channel:900000000000000001", members);
    }

    // The window still cuts the orbs, which is what it is for: seven of the nine
    // have been quiet for longer than an hour and none of them gets one.
    [Fact]
    public void TheRecencyWindowStillDecidesWhichOrbsAreDrawn()
    {
        var (sessions, total) = Parse(withinMinutes: 60);

        Assert.Equal(9, total);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Contains("channel:900000000000000001", s.Key));
    }

    // ...and every one of the nine still has somewhere to deliver, orb or no
    // orb. This is CB-27 end to end: before the fix, a member outside the window
    // had no address, so a room whose members had all gone quiet could not post
    // to its channel at all.
    [Fact]
    public void AMemberOutsideTheWindowStillHasAnAddress()
    {
        Parse(withinMinutes: 60);
        ClaudeBuddySettings.OpenClawEnabled = true;

        foreach (var key in OpenClawSessions.MembersOfRoom(Room))
        {
            var chat = (OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + key, "#lobby")!;

            Assert.NotNull(chat.Delivery);
            Assert.Equal("discord", chat.Delivery!.Channel);
            Assert.Equal("channel:900000000000000001", chat.Delivery.To);
            Assert.False(string.IsNullOrWhiteSpace(chat.Delivery.AccountId));
        }
    }

    // The accountId is the one part of the address that differs per member, and
    // the one part the room key cannot be made to yield. It is what makes the
    // gateway suppress a bot's own channel post from that bot's own sessions,
    // which is what stops a room send reaching its carrier twice.
    [Fact]
    public void EachMemberCarriesItsOwnAccount()
    {
        Parse(withinMinutes: 60);
        ClaudeBuddySettings.OpenClawEnabled = true;

        var accounts = OpenClawSessions.MembersOfRoom(Room)
            .Select(k => ((OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + k, "#lobby")!)
                .Delivery!.AccountId)
            .ToList();

        // Eight distinct accounts across nine members: two of these agents share
        // one, which is real and is why this asserts "several" rather than "all
        // different".
        Assert.True(accounts.Distinct().Count() >= 8, string.Join(",", accounts));
    }

    // --- chat.history, all the way to who said it --------------------------

    // One page carrying all five message shapes the gateway was observed to
    // produce, read in one pass.
    [Fact]
    public void APageOfHistoryIsClassifiedShapeByShape()
    {
        var page = Json(HistoryPage).GetProperty("messages");
        var turns = OpenClawSessions.TurnsFromHistory(page);

        Assert.Equal(6, turns.Count);

        // The operator, typing in Discord.
        Assert.True(turns[0].Mine);
        Assert.Null(turns[0].Speaker);

        // The agent whose transcript this is. Never classified, never yours,
        // whatever its idempotency key looks like.
        Assert.Equal(ChatRole.Assistant, turns[1].Role);
        Assert.False(turns[1].Mine);

        // Another agent, relayed through the channel — named, and uncoloured
        // because a Discord display name is not an agent id.
        Assert.Equal("Thistle", turns[2].Speaker);
        Assert.False(turns[2].Mine);
        Assert.Null(turns[2].SpeakerColor);

        // The operator, typing here: no sender fields, and an idempotency key
        // ending ":user".
        Assert.True(turns[3].Mine);

        // The same message as the one before it, as the other agents in the room
        // received it — prefixed, attributed to the bot that carried it, and
        // still yours. Shown without the prefix, which is what lets the two
        // dedupe into one bubble.
        Assert.True(turns[4].Mine);
        Assert.Equal(turns[3].Text, turns[4].Text);

        // An inter-session message, whose machine header Readable strips and
        // whose speaker it identifies — so the classification leaves it alone.
        Assert.False(turns[5].Mine);
        Assert.Equal("can you take the release notes?", turns[5].Text);
        Assert.NotNull(turns[5].Speaker);
    }

    // --- the room-send failure path ----------------------------------------

    // With no gateway there is nothing to post through, and the room says so in
    // its own transcript rather than going quiet. Silence is the failure this
    // ticket is about: the message looked sent and nobody in the channel had it.
    //
    // Through the real SendToRoomAsync rather than a stand-in, because the thing
    // being checked is that the refusal survives the whole path — carrier pick,
    // send attempt, sentence, note — and lands somewhere a person will read it.
    [Fact]
    public async Task ARoomSendThatCannotHappenSaysSoInTheRoom()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawReplyEnabled = true;

        Parse(withinMinutes: 60);
        ClaudeBuddySettings.OpenClawEnabled = true;

        var members = OpenClawSessions.MembersOfRoom(Room)
            .Select(k => ((OpenClawChatSession)OpenClawSessions.ChatFor("openclaw:" + k, "#lobby")!,
                          k.Split(':')[1], "#7f7"))
            .ToList();

        foreach (var (chat, _, _) in members) chat.HasMore = false;

        var room = new OpenClawRoomChatSession("openclaw:room:" + Room, "#lobby");
        room.SetMembers(members);

        await room.SendAsync("anyone free to look at the build?");

        // Your message, and under it the reason it went nowhere.
        Assert.Contains(room.History, t => t.Mine && t.Text == "anyone free to look at the build?");

        var note = Assert.Single(room.History, t => t.Role == ChatRole.System);
        Assert.StartsWith("Couldn't post to #lobby:", note.Text);
        Assert.Contains("Nothing was sent", note.Text);
    }
}
