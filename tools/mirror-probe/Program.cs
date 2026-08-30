using System.Diagnostics;
using System.Text.Json;
using ClaudeBuddy;

// Fetches another machine's transcript the way the chat panel does, and prints
// what came back.
//
// It exists because the mirror's one visible symptom — a panel that says it
// could not get a live view — is produced by at least six different faults, and
// telling them apart by looking at the panel has cost this project several
// wrong diagnoses. CB-54 is the sharpest case: the transfer worked perfectly,
// the window arrived complete and verified, and the client discarded it because
// the request had already expired. From outside, that is indistinguishable from
// the far machine never answering.
//
// The other reason is that the fetch is only reachable by opening a panel, so
// the last leg of any end-to-end check needed a human with a mouse. That made
// the fix unverifiable by whoever wrote it, which is how a promising build gets
// reported as broken the next morning.
//
//   dotnet run --project tools/mirror-probe -- <session-name> [far-relay] [near-relay]
//
// Read-only: it sends HELLO and FETCH, never INPUT, so it cannot type into
// anyone's session.
//
// **Run it with Claude Buddy quit.** The probe drives the relay pane by pasting
// into it, and so does Buddy; both doing it at once interleaves two prompts in
// one composer and corrupts both. The relay itself is a tmux session that
// outlives the app, which is what makes this possible at all.

var wanted = args.Length > 0 ? args[0] : "job-hunter-mac-mini";
var farRelay = args.Length > 1 ? args[1] : "claude-buddy-rc--claude-board-avatar";
var nearRelay = args.Length > 2 ? args[2] : "claude-buddy-rc--claude-board-warrens-mbp";

var tmux = new[] { "/opt/homebrew/bin/tmux", "/usr/local/bin/tmux", "/usr/bin/tmux" }
    .FirstOrDefault(File.Exists);

if (tmux is null)
{
    Console.Error.WriteLine("no tmux found");
    return 1;
}

var pane = nearRelay + ":";

if (!Tmux(tmux, out _, "has-session", "-t", "=" + nearRelay))
{
    Console.Error.WriteLine($"no relay session named {nearRelay}. Start Claude Buddy once to "
                          + "launch it, then quit Buddy and run this again.");
    return 1;
}

// Where the relay writes what it saw. Frames arrive as cross-session messages in
// this file, which is exactly where Buddy reads them from — the app does not
// scrape the pane for them either.
var dir = Directory.EnumerateDirectories(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".claude-board", "projects"))
    .Where(d => Path.GetFileName(d).Contains(nearRelay, StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(File.GetLastWriteTimeUtc)
    .FirstOrDefault();

if (dir is null)
{
    Console.Error.WriteLine($"no transcript directory for {nearRelay}");
    return 1;
}

Console.WriteLine($"near relay : {nearRelay}");
Console.WriteLine($"far relay  : {farRelay}");
Console.WriteLine($"session    : {wanted}");
Console.WriteLine();

// Only rows written from here on count. A relay's transcript holds every frame
// it has ever carried, and replaying those would settle requests this run never
// made.
var seen = new HashSet<string>(StringComparer.Ordinal);
var start = DateTime.UtcNow;

var client = new RemoteMirrorClient("claude-board", new RemoteMirrorClient.Seams(
    async (peer, frame) =>
    {
        var prompt = BridgeProtocol.SendFramePrompt(peer, frame);

        // The same three calls RemoteControlBridge.Paste makes: buffer it, paste
        // it as one bracketed paste so a multi-line prompt does not arrive as a
        // series of newlines, then send the Return separately.
        if (!Tmux(tmux, out _, "set-buffer", "-b", "mirror-probe", "--", prompt)) return false;
        if (!Tmux(tmux, out _, "paste-buffer", "-b", "mirror-probe", "-t", pane, "-p", "-d")) return false;
        if (!Tmux(tmux, out _, "send-keys", "-t", pane, "Enter")) return false;

        Console.WriteLine($"  -> {Describe(frame)}");
        await Task.CompletedTask;
        return true;
    }));

client.Failed += (name, why) => Console.WriteLine($"  !! {name}: {why}");

var painted = new TaskCompletionSource<RemoteMirrorClient.MirrorRows>(
    TaskCreationOptions.RunContinuationsAsynchronously);

client.Delivered += rows =>
{
    Console.WriteLine($"  <- {rows.Mode} for {rows.Name}: {rows.Turns.Count} turns ({rows.Cli})");
    painted.TrySetResult(rows);
};

// Reads the relay's transcript for frames addressed back to us and hands them to
// the client, which is the half that verifies hashes and reassembles chunks.
using var stop = new CancellationTokenSource();
var pump = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        foreach (var (type, text) in NewRows(dir, seen, start))
        {
            foreach (var inbound in BridgeProtocol.ParseInboundMessagesFrom(type, text))
            {
                var frame = MirrorProtocol.TryParseFrame(inbound.Body);
                if (frame is null) continue;

                Console.WriteLine($"  <- {Describe(inbound.Body)}");
                await client.OnFrameAsync(farRelay, frame).ConfigureAwait(false);
            }
        }

        await Task.Delay(1000, stop.Token).ContinueWith(_ => { }).ConfigureAwait(false);
    }
});

var peers = new List<BridgeProtocol.RemoteAgent>
{
    new(farRelay, "", "Remote Control", "online")
};

Console.WriteLine("asking who is over there...");
var began = DateTime.UtcNow;

await client.DiscoverAsync(peers, new[] { wanted });

var state = client.StateFor(wanted);
Console.WriteLine($"  roster says: {state.Availability} after {(DateTime.UtcNow - began).TotalSeconds:F0}s");

if (state.Availability != RemoteMirrorClient.MirrorAvailability.Available)
{
    Console.WriteLine();
    Console.WriteLine("No live view offered, so there is nothing to fetch. That is the honest");
    Console.WriteLine("answer rather than a failure of this probe.");
    stop.Cancel();
    return 2;
}

Console.WriteLine();
Console.WriteLine($"fetching {wanted} (this takes minutes: the far relay retypes it by hand)...");
began = DateTime.UtcNow;

var ok = await client.OpenAsync(wanted);
var took = (DateTime.UtcNow - began).TotalSeconds;

Console.WriteLine();
Console.WriteLine($"OpenAsync returned {ok} after {took:F0}s "
                + $"(deadline is {MirrorProtocol.FetchTimeoutSeconds}s)");

if (painted.Task.IsCompleted)
{
    var rows = painted.Task.Result;
    Console.WriteLine();
    Console.WriteLine($"=== {rows.Turns.Count} turns of {rows.Name} ===");

    foreach (var turn in rows.Turns.TakeLast(6))
    {
        var text = turn.Text.Replace("\n", " ");
        if (text.Length > 150) text = text[..150] + "...";
        Console.WriteLine($"  [{turn.Role}] {text}");
    }
}

stop.Cancel();
return ok ? 0 : 3;

// --- plumbing ----------------------------------------------------------------

static string Describe(string frame)
{
    var parsed = MirrorProtocol.TryParseFrame(frame);
    if (parsed is null) return "(unparseable)";

    var extra = parsed.Fields.TryGetValue("seq", out var seq)
        ? $" seq {seq} of {parsed.Fields.GetValueOrDefault("of", "?")}"
        : "";

    return $"{parsed.Type} {parsed.Id}{extra} ({frame.Length} chars)";
}

// Rows the relay has written since this run began, each as (type, raw json).
static IEnumerable<(string Type, string Text)> NewRows(
    string dir, HashSet<string> seen, DateTime since)
{
    var file = Directory.EnumerateFiles(dir, "*.jsonl")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();

    if (file is null) yield break;

    string[] lines;

    // Copied first: the relay appends while this reads, and a half-written last
    // line is not worth an exception.
    try { lines = File.ReadAllLines(file); }
    catch { yield break; }

    foreach (var line in lines)
    {
        if (line.Length == 0) continue;

        string? type = null, uuid = null, stamp = null, body = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString();
            if (doc.RootElement.TryGetProperty("uuid", out var u)) uuid = u.GetString();
            if (doc.RootElement.TryGetProperty("timestamp", out var s)) stamp = s.GetString();

            // The *content*, not the row. BridgeProtocol's parser matches an
            // XML-ish `<cross-session-message from="…">` tag, and in a JSONL row
            // every quote in that tag is backslash-escaped — so handing it the
            // raw line matches nothing and the frame is silently never seen.
            // Cost an hour here before the pane showed the reply arriving that
            // the probe swore had not. The app reads the extracted string too;
            // see RemoteControlBridge's two call sites.
            if (doc.RootElement.TryGetProperty("message", out var m)
                && m.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String)
                {
                    body = c.GetString();
                }
                else if (c.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();

                    foreach (var block in c.EnumerateArray())
                        if (block.TryGetProperty("text", out var bt)
                            && bt.ValueKind == JsonValueKind.String)
                            parts.Add(bt.GetString() ?? "");

                    body = string.Join("\n", parts);
                }
            }
        }
        catch { continue; }

        if (type is null || uuid is null || body is null) continue;
        if (!seen.Add(uuid)) continue;

        // Rows already in the file when this started are noted as seen above and
        // then skipped, so an old frame never settles a new request.
        if (DateTime.TryParse(stamp, out var written)
            && written.ToUniversalTime() < since) continue;

        yield return (type, body);
    }
}

static bool Tmux(string tmux, out string output, params string[] args)
{
    var psi = new ProcessStartInfo(tmux)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    foreach (var a in args) psi.ArgumentList.Add(a);

    output = "";

    try
    {
        using var p = Process.Start(psi);
        if (p is null) return false;

        output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode == 0;
    }
    catch { return false; }
}
