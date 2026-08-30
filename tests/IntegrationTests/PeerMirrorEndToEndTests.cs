using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// The whole mirror, over a real TLS socket, with nothing faked between the two
// halves but the machines themselves.
//
// **This is the test the plan turns on.** Every other assertion about the peer
// link is about a part of it — framing, trust, the read loop. This one takes the
// unchanged RemoteMirrorClient and RemoteMirrorServer, puts a PeerLink between
// them instead of a hidden Claude Code session, and asks whether a transcript
// still arrives. If it does, the transport swap altered no behaviour, which is
// the only claim worth making at this step.
//
// What it replaces is worth restating: the same exchange over the relay cost a
// model turn per frame and was measured at 222 to 247 seconds for a single 6KB
// chunk, with at least one chunk arriving corrupted.
public class PeerMirrorEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "peer-mirror-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<PeerMirrorHost> _hosts = new();

    public PeerMirrorEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var host in _hosts) host.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PeerMirrorHost NewHost()
    {
        var host = new PeerMirrorHost();
        _hosts.Add(host);
        return host;
    }

    // The far machine: a Buddy with one Claude Code session whose transcript is
    // on its disk, serving whatever is asked of it.
    private (PeerMirrorHost Host, RemoteMirrorServer Server) Serving(params string[] lines)
    {
        var path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        var sessionId = Guid.NewGuid().ToString();

        var sessions = new List<(string, SessionStatus)>
        {
            (sessionId, new SessionStatus
            {
                Title = "job-hunter",
                Cwd = _dir,
                Source = SessionSource.ClaudeCode,
                TranscriptPath = path,
                TmuxPane = "%1",
                SessionPid = 4242
            })
        };

        var agents = new List<AgentRoster.Entry> { new("job-hunter", sessionId, 4242) };

        var host = NewHost();

        var server = new RemoteMirrorServer("acct", new RemoteMirrorServer.Seams(
            SendFrame: host.SendFrameAsync,
            LocalSessions: () => sessions,
            Agents: () => agents,
            ReplyEnabled: _ => true,
            CanType: _ => true,
            TypeInto: (_, _) => Task.FromResult(true),
            PeerAllowed: host.MayAsk));

        host.Bind(new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(host.SendFrameAsync)), server);

        return (host, server);
    }

    // This machine: a Buddy with a panel open on the far session.
    private (PeerMirrorHost Host, RemoteMirrorClient Client) Watching()
    {
        var host = NewHost();
        var client = new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(host.SendFrameAsync));

        var server = new RemoteMirrorServer("acct", new RemoteMirrorServer.Seams(
            SendFrame: host.SendFrameAsync,
            LocalSessions: () => new List<(string, SessionStatus)>(),
            Agents: () => new List<AgentRoster.Entry>(),
            ReplyEnabled: _ => true,
            CanType: _ => false,
            TypeInto: (_, _) => Task.FromResult(false),
            PeerAllowed: host.MayAsk));

        host.Bind(client, server);
        return (host, client);
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";

    private static string AssistantRow(string uuid, string text) =>
        $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";


    [Fact]
    public async Task ATranscriptCrossesTheLinkAndArrivesAsTurns()
    {
        var far = Serving(
            UserRow("u1", "what is it working on?"),
            AssistantRow("a1", "Batch 33 launched (RUN_ID 410)."),
            UserRow("u2", "thanks"));

        var near = Watching();

        // What a person does once, in Settings: agree that this machine and that
        // one may talk. Both ends run in this process and so share one identity
        // file, which means one certificate and therefore one pin — the same
        // situation as two machines that have paired.
        //
        // **Both directions, because pairing is mutual and the greeting made
        // that visible.** The dialling side has to trust the certificate it is
        // offered; the accepting side has to trust the machine that greets it,
        // and a greeting names the *real* machine — Environment.MachineName —
        // rather than the label the dial used. Remembering only "far" left the
        // far end refusing a connection it had already completed a TLS
        // handshake on, which arrived here as a session that was never
        // Available and no explanation anywhere.
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "far"));
        PeerIdentity.Remember(
            new PeerIdentity.Peer(PeerIdentity.OwnPin(), Environment.MachineName));

        far.Host.Link.Listen(0);
        var port = far.Host.Link.BoundPort;

        Assert.True(
            await near.Host.Link.ConnectAsync("far", "127.0.0.1", port,
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token),
            "the two Buddies could not connect");

        var painted = new TaskCompletionSource<RemoteMirrorClient.MirrorRows>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        near.Client.Delivered += rows => painted.TrySetResult(rows);

        // Told who to ask directly, which is what a direct link knows — the
        // relay-shaped peer list this used to require is the coupling that came
        // out for this to be possible.
        await near.Client.DiscoverAsync(new[] { "far" }, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            near.Client.StateFor("job-hunter").Availability);

        Assert.True(await near.Client.OpenAsync("job-hunter"));

        var window = await painted.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The far machine's actual conversation, read off its disk and parsed by
        // the same ChatTranscript a local panel uses.
        Assert.Equal(3, window.Turns.Count);
        Assert.Contains(window.Turns, t => t.Text.Contains("Batch 33 launched"));
        Assert.Contains(window.Turns, t => t.Text.Contains("what is it working on?"));
    }

    // A peer that never connected is refused by the server's own gate, which on
    // this transport asks "is there a connection?" rather than "does the name
    // look right?" — the difference between a boundary and a guard.
    [Fact]
    public void AMachineWithNoConnectionMayNotAsk()
    {
        var far = Serving(UserRow("u1", "hello"));

        Assert.False(far.Host.MayAsk("someone-else"));
    }
}
