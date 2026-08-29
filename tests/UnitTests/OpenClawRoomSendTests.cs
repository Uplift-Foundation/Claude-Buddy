using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// What a room send actually puts on the wire, over an in-memory socket.
//
// Not covered by asserting the sentences alone, which is where this started.
// The whole of CB-27's fix is a claim about two requests — that there are two of
// them, that the channel post goes first, and that it goes out under the
// carrier's *own* accountId — and none of those is visible from the return
// value. An exclusion over the method would have left the load-bearing part of
// the fix as something nothing checks.
//
// The gateway takes its connector as a constructor argument for exactly this
// reason; see OpenClawGateway's own comment, and OpenClawGatewayTests, whose
// FakeGatewaySocket and handshake this borrows.
[Collection("Settings")]
public class OpenClawRoomSendTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    // A socket that completes the handshake and then answers whatever it is
    // asked, unless `refuse` names a method it should reject instead.
    private static FakeGatewaySocket Socket(string? refuse = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.write" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : request.Method == refuse
                    ? FakeGatewaySocket.Error(request.Id, "refused", "the gateway said no", null)
                    : FakeGatewaySocket.Ok(request.Id, new { });

        return socket;
    }

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        string? refuse = null)
    {
        var socket = Socket(refuse);
        var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private static OpenClawChatSession Carrier(string? account = "quillbot")
    {
        var chat = new OpenClawChatSession(
            "openclaw:agent:quill:discord:channel:900",
            "agent:quill:discord:channel:900",
            "Quill");

        chat.Delivery = new OpenClawSessions.Delivery("discord", "channel:900", account);
        return chat;
    }

    // Everything the send sent, in order, ignoring the handshake.
    private static List<FakeGatewaySocket.Request> Sent(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method != "connect").ToList();

    // --- the two requests, and their order ---

    // Both halves, and the channel post first. Ordering is not cosmetic: the
    // reply can come back fast, and a post that lands after it puts the question
    // below its own answer in Discord.
    [Fact]
    public async Task ARoomSendPostsToTheChannelAndThenHandsItToTheAgent()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var failure = await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            Assert.Null(failure);

            var sent = Sent(socket);
            Assert.Equal(2, sent.Count);
            Assert.Equal("send", sent[0].Method);
            Assert.Equal("chat.send", sent[1].Method);
        }
    }

    // The channel post itself: the address off the carrier's own delivery
    // context, and the fixed prefix in front of the message.
    //
    // The prefix is fixed rather than composed because it is read back as well
    // as written — it is the only trace that a message in somebody else's
    // transcript came from here — and it comes from OpenClawSender so the two
    // halves cannot drift.
    [Fact]
    public async Task TheChannelPostCarriesTheAddressAndThePrefix()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            var post = Sent(socket)[0].Params;

            Assert.Equal("discord", post.GetProperty("channel").GetString());
            Assert.Equal("channel:900", post.GetProperty("to").GetString());
            Assert.Equal(OpenClawSender.MirrorPrefix + "anyone about?",
                post.GetProperty("message").GetString());
        }
    }

    // The detail the whole fix rests on. Under any other account the carrier
    // would receive the post as an ordinary channel message *and* the chat.send,
    // and answer twice; under its own, the gateway's suppression of a bot's own
    // channel post is what makes the pair arrive exactly once.
    [Fact]
    public async Task TheChannelPostGoesOutUnderTheCarriersOwnAccount()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendToRoomAsync(
                Carrier("quillbot"), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            Assert.Equal("quillbot", Sent(socket)[0].Params.GetProperty("accountId").GetString());
        }
    }

    // A delivery context with no account is a real shape — the gateway falls
    // back to lastChannel/lastTo, which carry no account — and the field is left
    // off rather than sent empty. An empty accountId is not the same request as
    // no accountId, and inventing one would be guessing at an identity.
    [Fact]
    public async Task AnAddressWithNoAccountSendsNoAccountField()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendToRoomAsync(
                Carrier(account: null), "#lobby", "Quill", "anyone about?",
                CancellationToken.None);

            Assert.False(Sent(socket)[0].Params.TryGetProperty("accountId", out _));
        }
    }

    // The handoff: this session, and deliver on. Without deliver the gateway
    // routes the reply to its internal channel — the agent answers, the
    // transcript records it, and nothing reaches Discord.
    [Fact]
    public async Task TheHandoffNamesTheCarrierAndAsksForDelivery()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            var send = Sent(socket)[1].Params;

            Assert.Equal("agent:quill:discord:channel:900",
                send.GetProperty("sessionKey").GetString());
            Assert.Equal("anyone about?", send.GetProperty("message").GetString());
            Assert.True(send.GetProperty("deliver").GetBoolean());
        }
    }

    // The message the agent is handed is what you typed, without the prefix. The
    // prefix is addressing on the channel copy; putting it here would put it in
    // the agent's own transcript, where it is noise.
    [Fact]
    public async Task TheAgentIsHandedTheMessageWithoutThePrefix()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            Assert.DoesNotContain("via Claude Buddy",
                Sent(socket)[1].Params.GetProperty("message").GetString()!);
        }
    }

    // Every side-effecting request carries a fresh idempotency key, so a retry
    // after a timeout cannot post the message twice — and the two halves carry
    // different ones, since they are two different side effects.
    [Fact]
    public async Task BothRequestsCarryTheirOwnIdempotencyKey()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            var sent = Sent(socket);
            var post = sent[0].Params.GetProperty("idempotencyKey").GetString();
            var send = sent[1].Params.GetProperty("idempotencyKey").GetString();

            Assert.False(string.IsNullOrWhiteSpace(post));
            Assert.False(string.IsNullOrWhiteSpace(send));
            Assert.NotEqual(post, send);
        }
    }

    // --- what happens when one half fails ---

    // A failed post aborts the send, which is the one place this deliberately
    // differs from the best-effort mirror on an ordinary single-session send.
    // There the conversation already lives in the DM and the agent's reply is
    // delivered to it either way; here a chat.send without the post is exactly
    // the silent private delivery this ticket is about.
    [Fact]
    public async Task AFailedPostStopsTheSendRatherThanGoingAheadWithoutIt()
    {
        var (socket, gateway) = await ConnectedAsync(refuse: "send");
        using (gateway)
        {
            var failure = await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            Assert.NotNull(failure);
            Assert.Contains("Nothing was sent", failure!);
            Assert.DoesNotContain(Sent(socket), r => r.Method == "chat.send");
        }
    }

    // A failed handoff must not say "nothing was sent", because something was:
    // the channel has the message and only the agent's copy failed. The wrong
    // wording here would have someone post the same message a second time.
    [Fact]
    public async Task AFailedHandoffSaysTheChannelAlreadyHasIt()
    {
        var (socket, gateway) = await ConnectedAsync(refuse: "chat.send");
        using (gateway)
        {
            var failure = await OpenClawSessions.SendToRoomAsync(
                Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

            Assert.NotNull(failure);
            Assert.StartsWith("Posted to #lobby, but couldn't hand it to Quill:", failure!);
            Assert.DoesNotContain("Nothing was sent", failure!);

            // ...and it really did post.
            Assert.Contains(Sent(socket), r => r.Method == "send");
        }
    }

    // A carrier with no address cannot post at all, and nothing is attempted.
    [Fact]
    public async Task ACarrierWithNoAddressSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var carrier = Carrier();
            carrier.Delivery = null;

            var failure = await OpenClawSessions.SendToRoomAsync(
                carrier, "#lobby", "Quill", "anyone about?", CancellationToken.None);

            Assert.Equal(OpenClawSessions.NoAddressInRoom("#lobby"), failure);
            Assert.Empty(Sent(socket));
        }
    }

    // --- the room's own path, end to end ---

    // A send that actually works, through OpenClawRoomChatSession rather than
    // straight into SendToRoomAsync. Everything else about the room is asserted
    // with no gateway behind it, which reaches every failure and none of the
    // success — so the one thing left unchecked was that a room that *can* send
    // leaves no explanation behind. A note under a message that went through
    // would be worse than no note at all.
    [Fact]
    public async Task ARoomThatCanSendSendsAndSaysNothing()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawReplyEnabled = true;

        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var carrier = Carrier();
            carrier.HasMore = false;

            var room = new OpenClawRoomChatSession("openclaw:room:discord:900", "#lobby");
            room.SetMembers(new[] { (carrier, "Quill", "#7f7") });

            await room.SendAsync("anyone about?");

            // Your message, and nothing explaining itself under it.
            Assert.Contains(room.History, t => t.Mine && t.Text == "anyone about?");
            Assert.DoesNotContain(room.History, t => t.Role == ChatRole.System);

            // ...and it really went out, both halves of it.
            Assert.Equal(new[] { "send", "chat.send" }, Sent(socket).Select(r => r.Method));
        }
    }

    // No connection at all, which is what the app looks like between reconnects.
    [Fact]
    public async Task WithNoGatewayNothingIsSentAndItSaysSo()
    {
        OpenClawSessions.SetGatewayForTests(null);

        var failure = await OpenClawSessions.SendToRoomAsync(
            Carrier(), "#lobby", "Quill", "anyone about?", CancellationToken.None);

        Assert.Equal(
            OpenClawSessions.PostFailed("#lobby", "not connected to the gateway"), failure);
    }
}
