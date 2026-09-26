using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-170: what an OpenClaw orb's Interrupt and End rows actually put on the
// wire, and what comes back to the row, over an in-memory socket.
//
// The ticket's acceptance criteria are mostly about refusals — shown, never
// swallowed — and a refusal is an exchange rather than a value, so it is
// asserted here against a gateway that really refuses: an error frame with the
// gateway's own code and sentence, through the real OpenClawGateway. The
// socket and handshake are OpenClawRoomSendTests', which borrowed them from
// OpenClawGatewayTests.
//
// The frames are the shapes measured against OpenClaw 2026.9.2 and recorded on
// CB-170: chat.abort {sessionKey} answering {ok, aborted, runIds}, and
// sessions.patch {key, archived, expectedSessionId} answering {ok, key, entry}.
[Collection("Settings")]
public class OpenClawOrbActionRequestTests : IDisposable
{
    private const string Key = "agent:main:dashboard:abc";
    private const string Orb = "openclaw:" + Key;

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
    }

    private static OpenClawSessions.Session Session(string? sessionId = "sid-1", bool isMain = false) =>
        new(Key, "Dashboard", "", "idle", DateTime.UtcNow, null, SessionKind.Direct, false, sessionId, isMain);

    // A gateway that completes the handshake — advertising the two methods and
    // granting read+write, the shape a device with replying on gets — and then
    // hands every other request to `answer`. A null answer is an empty ok.
    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object?>? answer = null,
        OpenClawSessions.Session? session = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "2026.9.2" },
                    auth = new { scopes = new[] { "operator.read", "operator.write" } },
                    features = new { methods = new[] { "chat.abort", "sessions.patch", "sessions.reset" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : answer?.Invoke(request) ?? FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);
        OpenClawSessions.SetSnapshotForTests(new[] { session ?? Session() });
        return (socket, gateway);
    }

    private static List<FakeGatewaySocket.Request> Sent(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method != "connect").ToList();

    private static object Aborted(FakeGatewaySocket.Request r, bool aborted) =>
        FakeGatewaySocket.Ok(r.Id, new { ok = true, aborted, runIds = aborted ? new[] { "run-1" } : Array.Empty<string>() });

    private static object Archived(FakeGatewaySocket.Request r) =>
        FakeGatewaySocket.Ok(r.Id, new { ok = true, key = Key, entry = new { archivedAt = 1 } });

    private static object Refuse(FakeGatewaySocket.Request r, string code, string message) =>
        FakeGatewaySocket.Error(r.Id, code, message, null);

    // --- what the gateway told us on connect ---

    [Fact]
    public async Task TheHandshakeKeepsTheMethodListAndTheContextCarriesIt()
    {
        var (_, gateway) = await ConnectedAsync(session: Session("sid-9", isMain: true));
        using (gateway)
        {
            Assert.Contains("chat.abort", gateway.Methods);

            var context = OpenClawSessions.CapabilitiesFor(Orb);

            Assert.True(context.Connected);
            Assert.Contains("sessions.patch", context.Methods);
            Assert.Contains("operator.write", context.Scopes);
            Assert.True(context.IsMain);
            Assert.Equal("sid-9", context.SessionId);
            Assert.Equal(Key, context.Key);
        }
    }

    // Connected, but the orb's conversation is not in the last list: no id,
    // so End has nothing to send.
    [Fact]
    public async Task AnOrbMissingFromTheListHasNoSessionId()
    {
        var (_, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var context = OpenClawSessions.CapabilitiesFor("openclaw:agent:main:dashboard:other");

            Assert.True(context.Connected);
            Assert.Null(context.SessionId);
            Assert.False(context.IsMain);
        }
    }

    [Theory]
    [InlineData("openclaw:")]
    [InlineData("not-an-openclaw-orb")]
    public async Task AnIdWithNoGatewayKeyIsTheEmptyContext(string orb)
    {
        var (_, gateway) = await ConnectedAsync();
        using (gateway)
        {
            Assert.Same(OpenClawActionContext.None, OpenClawSessions.CapabilitiesFor(orb));
        }
    }

    [Fact]
    public void DisconnectedIsTheEmptyContext()
    {
        OpenClawSessions.SetGatewayForTests(null);

        Assert.Same(OpenClawActionContext.None, OpenClawSessions.CapabilitiesFor(Orb));
    }

    // --- Interrupt ---

    [Fact]
    public async Task InterruptSendsChatAbortWithTheKeyAlone()
    {
        var (socket, gateway) = await ConnectedAsync(r => Aborted(r, true));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.InterruptAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Done, outcome);

            var sent = Assert.Single(Sent(socket));
            Assert.Equal("chat.abort", sent.Method);
            Assert.Equal(Key, sent.Params.GetProperty("sessionKey").GetString());
            Assert.Single(sent.Params.EnumerateObject());
        }
    }

    [Fact]
    public async Task InterruptWithNothingRunningSaysSo()
    {
        var (_, gateway) = await ConnectedAsync(r => Aborted(r, false));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.InterruptAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.NothingRunning, outcome);
        }
    }

    [Fact]
    public async Task InterruptOfAnotherDevicesRunIsOtherDevice()
    {
        var (_, gateway) = await ConnectedAsync(r => Refuse(r, "INVALID_REQUEST", "unauthorized"));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.InterruptAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.OtherDevice, outcome);
        }
    }

    // The acceptance criteria's refusal path: the gateway's own sentence
    // reaches the row, verbatim.
    [Fact]
    public async Task AScopeRefusalOnInterruptIsReturnedVerbatim()
    {
        var (_, gateway) = await ConnectedAsync(r => Refuse(r, "INVALID_REQUEST", "missing scope: operator.write"));
        using (gateway)
        {
            var (outcome, detail) = await OpenClawSessions.InterruptAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Refused, outcome);
            Assert.Equal("missing scope: operator.write", detail);
        }
    }

    [Fact]
    public async Task InterruptWhileDisconnectedSendsNothing()
    {
        OpenClawSessions.SetGatewayForTests(null);

        var (outcome, _) = await OpenClawSessions.InterruptAsync(Orb, CancellationToken.None);

        Assert.Equal(OpenClawActionOutcome.NotConnected, outcome);
    }

    [Fact]
    public async Task InterruptOfAnIdWithNoKeyIsNotConnected()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.InterruptAsync("openclaw:", CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.NotConnected, outcome);
            Assert.Empty(Sent(socket));
        }
    }

    // --- End ---

    // Abort first, then archive — the gateway refuses to archive mid-run — and
    // the archive carries the id it will not act without. The orb's session
    // leaves the snapshot at once rather than a poll later.
    [Fact]
    public async Task EndAbortsThenArchivesWithTheSessionId()
    {
        var (socket, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort" ? Aborted(r, true) : Archived(r));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Done, outcome);

            var sent = Sent(socket);
            Assert.Equal(new[] { "chat.abort", "sessions.patch" }, sent.Select(r => r.Method).ToArray());

            var patch = sent[1].Params;
            Assert.Equal(Key, patch.GetProperty("key").GetString());
            Assert.True(patch.GetProperty("archived").GetBoolean());
            Assert.Equal("sid-1", patch.GetProperty("expectedSessionId").GetString());

            // Read through CapabilitiesFor rather than Snapshot(), which is empty
            // whenever OpenClaw is off in settings and would pass for nothing.
            Assert.Null(OpenClawSessions.CapabilitiesFor(Orb).SessionId);
        }
    }

    // Nothing running is the ordinary case, and still ends.
    [Fact]
    public async Task EndWithNothingRunningStillArchives()
    {
        var (socket, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort" ? Aborted(r, false) : Archived(r));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Done, outcome);
            Assert.Equal(2, Sent(socket).Count);
        }
    }

    // A run another device started cannot be stopped from here, so the
    // archive would be refused as still active. Not attempted.
    [Fact]
    public async Task EndStopsAtAnAbortItWasNotAllowed()
    {
        var (socket, gateway) = await ConnectedAsync(r => Refuse(r, "INVALID_REQUEST", "unauthorized"));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.OtherDevice, outcome);
            Assert.Equal(new[] { "chat.abort" }, Sent(socket).Select(r => r.Method).ToArray());
        }
    }

    [Fact]
    public async Task EndStopsAtARefusedAbort()
    {
        var (socket, gateway) = await ConnectedAsync(r => Refuse(r, "INVALID_REQUEST", "missing scope: operator.write"));
        using (gateway)
        {
            var (outcome, detail) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Refused, outcome);
            Assert.Equal("missing scope: operator.write", detail);
            Assert.Single(Sent(socket));
        }
    }

    // The gateway's refusal of the archive itself, shown as it said it — and
    // the orb stays, because nothing was archived.
    [Fact]
    public async Task ARefusedArchiveIsReturnedVerbatimAndTheOrbStays()
    {
        var (_, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort"
                ? Aborted(r, false)
                : Refuse(r, "INVALID_REQUEST", "Cannot archive an agent's main session."));
        using (gateway)
        {
            var (outcome, detail) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Refused, outcome);
            Assert.Equal("Cannot archive an agent's main session.", detail);
            Assert.Equal("sid-1", OpenClawSessions.CapabilitiesFor(Orb).SessionId);
        }
    }

    [Fact]
    public async Task AnUnconfirmedArchiveIsNotAnEndAndTheOrbStays()
    {
        var (_, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort" ? Aborted(r, false) : FakeGatewaySocket.Ok(r.Id, new { }));
        using (gateway)
        {
            var (outcome, detail) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Refused, outcome);
            Assert.Equal("the gateway didn't confirm the archive", detail);
            Assert.Equal("sid-1", OpenClawSessions.CapabilitiesFor(Orb).SessionId);
        }
    }

    // An archive refused as retryable — the run it follows still settling —
    // is asked again, and a later yes is an end.
    [Fact]
    public async Task AStillActiveArchiveIsAskedAgain()
    {
        var patches = 0;
        var (socket, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort"
                ? Aborted(r, true)
                : ++patches < 3
                    ? Refuse(r, "UNAVAILABLE", $"Session {Key} is still active; retry the archive.")
                    : Archived(r));
        using (gateway)
        {
            var (outcome, _) = await OpenClawSessions.EndConversationAsync(
                Orb, CancellationToken.None, TimeSpan.Zero);

            Assert.Equal(OpenClawActionOutcome.Done, outcome);
            Assert.Equal(3, Sent(socket).Count(r => r.Method == "sessions.patch"));
        }
    }

    // ...but not for ever: after the last attempt the refusal is the answer.
    [Fact]
    public async Task AnArchiveStillActiveAfterEveryAttemptIsShown()
    {
        var (socket, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort"
                ? Aborted(r, true)
                : Refuse(r, "UNAVAILABLE", $"Session {Key} is still active; retry the archive."));
        using (gateway)
        {
            var (outcome, detail) = await OpenClawSessions.EndConversationAsync(
                Orb, CancellationToken.None, TimeSpan.Zero);

            Assert.Equal(OpenClawActionOutcome.Refused, outcome);
            Assert.Equal($"Session {Key} is still active; retry the archive.", detail);
            Assert.Equal(OpenClawSessions.ArchiveAttempts,
                Sent(socket).Count(r => r.Method == "sessions.patch"));
        }
    }

    // The real half-second wait between attempts, once, so the default delay
    // is exercised and not only the zero a test passes.
    [Fact]
    public async Task TheDefaultRetryWaitsBeforeAskingAgain()
    {
        var patches = 0;
        var (_, gateway) = await ConnectedAsync(r =>
            r.Method == "chat.abort"
                ? Aborted(r, true)
                : ++patches < 2
                    ? Refuse(r, "UNAVAILABLE", "still active")
                    : Archived(r));
        using (gateway)
        {
            var started = DateTime.UtcNow;
            var (outcome, _) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Done, outcome);
            Assert.True(DateTime.UtcNow - started >= OpenClawSessions.ArchiveRetryDelay - TimeSpan.FromMilliseconds(50));
        }
    }

    // No id in the list, nothing sent: the archive would be refused anyway.
    [Fact]
    public async Task EndWithoutASessionIdSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync(session: Session(sessionId: null));
        using (gateway)
        {
            var (outcome, detail) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Refused, outcome);
            Assert.Equal("the gateway hasn't listed this conversation", detail);
            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task EndWhileDisconnectedSendsNothing()
    {
        OpenClawSessions.SetGatewayForTests(null);

        var (outcome, _) = await OpenClawSessions.EndConversationAsync(Orb, CancellationToken.None);

        Assert.Equal(OpenClawActionOutcome.NotConnected, outcome);
    }
}
