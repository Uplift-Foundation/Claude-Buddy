using System.Text.Json;
using ClaudeBuddy;

// Asks the gateway a question and prints what it says, using the app's own
// transport rather than a second one.
//
// It exists because several attempts at explaining the panel's behaviour were
// reasoning about a payload nobody here had read. Twice that produced a
// plausible fix that was wrong. The gateway is one LAN hop away and already
// paired with this machine; asking it is cheaper than another guess.
//
//   dotnet run --project tools/openclaw-probe -- sessions
//   dotnet run --project tools/openclaw-probe -- history <sessionKey> [limit] [offset]
//   dotnet run --project tools/openclaw-probe -- cron-runs <jobId> [limit] [offset]
//   dotnet run --project tools/openclaw-probe -- raw <method> [jsonParams]
//   dotnet run --project tools/openclaw-probe -- events [seconds] [sessionKey|-] [out.jsonl|-] [messagesKey]
//
// Read-only by construction: it never calls chat.send, and it requests whatever
// scopes the app's settings already granted rather than asking for more.

var host = ClaudeBuddySettings.OpenClawHost;
var port = ClaudeBuddySettings.OpenClawPort;

if (string.IsNullOrWhiteSpace(host))
{
    Console.Error.WriteLine("No gateway address in settings. Turn OpenClaw on in Claude Buddy first.");
    return 1;
}

var command = args.Length > 0 ? args[0] : "sessions";

// The token the app already stores for this gateway, beside the device key.
var token = OpenClawIdentity.GatewayTokenFor(host);
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine($"No gateway token stored for {host}.");
    return 1;
}

using var gateway = new OpenClawGateway(host, port, token!);

var pinned = ClaudeBuddySettings.OpenClawFingerprint;
var result = await gateway.ConnectAsync(string.IsNullOrEmpty(pinned) ? null : pinned, CancellationToken.None);

if (result.Outcome != OpenClawGateway.Outcome.Connected)
{
    Console.Error.WriteLine($"Not connected: {result.Outcome} {result.Detail}");
    return 1;
}

// Whole objects, indented. The point is to see the fields nobody documented,
// so nothing here selects or reshapes what came back.
var pretty = new JsonSerializerOptions { WriteIndented = true };

try
{
    switch (command)
    {
        case "sessions":
        {
            var res = await gateway.RequestAsync("sessions.list", new Dictionary<string, object>(), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        case "history":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("history needs a sessionKey");
                return 1;
            }

            var parameters = new Dictionary<string, object>
            {
                ["sessionKey"] = args[1],
                ["limit"] = args.Length > 2 ? int.Parse(args[2]) : 40,
                ["offset"] = args.Length > 3 ? int.Parse(args[3]) : 0
            };

            var res = await gateway.RequestAsync("chat.history", parameters, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        // CB-115: cron.runs, which recovers a cron-delivered picture's real
        // path once OpenClaw's delivery route has stripped it out of the
        // transcript. `raw` already reaches this method, but a named
        // subcommand is what makes a capture reproducible without retyping
        // the jsonParams shape — the same reasoning `history` above exists
        // for. Limit defaults higher than history's, on purpose: an earlier
        // capture of this ticket's real job at limit:5 silently held only 4
        // of its 35 real MEDIA: paths, and 60 is what actually captured all
        // of them in one page (see OpenClawCronRecovery's header).
        case "cron-runs":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("cron-runs needs a jobId");
                return 1;
            }

            var parameters = new Dictionary<string, object>
            {
                ["jobId"] = args[1],
                ["limit"] = args.Length > 2 ? int.Parse(args[2]) : 60,
                ["offset"] = args.Length > 3 ? int.Parse(args[3]) : 0
            };

            var res = await gateway.RequestAsync("cron.runs", parameters, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        // CB-169: what the event stream actually carries during a turn. The
        // orb's running state is decided entirely by which events arrive and
        // in what order, and the only capture of that sequence so far was a
        // cron run — nobody had watched a DM reply. This subscribes exactly as
        // the app does (sessions.subscribe and nothing more), so what it sees
        // is what the app sees, and prints one line per event with the fields
        // the classifier reads. The raw payloads go to the .jsonl, one per
        // line with the arrival time, so a test can replay the real sequence
        // rather than one written from memory. "-" skips the sessionKey
        // filter; the filter matches the key with any ":run:" suffix trimmed.
        // A fifth argument also calls sessions.messages.subscribe for that
        // key, which the app does not do — it exists to answer whether that
        // subscription carries a run's agent/chat stream when
        // sessions.subscribe alone does not.
        case "events":
        {
            var seconds = args.Length > 1 ? int.Parse(args[1]) : 180;
            var filter = args.Length > 2 && args[2] != "-" ? args[2] : null;
            using var raw = args.Length > 3 && args[3] != "-" ? new StreamWriter(args[3]) { AutoFlush = true } : null;
            var started = DateTime.UtcNow;
            var gate = new object();

            gateway.EventReceived += (name, payload) =>
            {
                var at = DateTime.UtcNow;
                var key = Field(payload, "sessionKey");
                var trimmed = key;
                var run = trimmed?.IndexOf(":run:", StringComparison.Ordinal) ?? -1;
                if (run > 0) trimmed = trimmed![..run];
                if (filter is not null && trimmed != filter) return;

                JsonElement data = default, task = default;
                var hasData = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
                var hasTask = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("task", out task) && task.ValueKind == JsonValueKind.Object;

                var line = string.Join(" ", new[]
                {
                    $"+{(at - started).TotalSeconds,7:F2}s",
                    name,
                    $"key={trimmed ?? "-"}",
                    run > 0 ? $"keyRun={key![(run + 5)..]}" : null,
                    Opt("runId", Field(payload, "runId")),
                    Opt("action", Field(payload, "action")),
                    Opt("stream", Field(payload, "stream")),
                    Opt("phase", hasData ? Field(data, "phase") : null),
                    Opt("state", Field(payload, "state")),
                    Opt("task.status", hasTask ? Field(task, "status") : null),
                    Opt("task.runId", hasTask ? Field(task, "runId") : null),
                    Opt("task.sessionKey", hasTask ? Field(task, "sessionKey") : null),
                }.Where(s => s is not null));

                lock (gate)
                {
                    Console.WriteLine(line);
                    raw?.WriteLine(JsonSerializer.Serialize(new { at = at.ToString("O"), name, payload }));
                }
            };

            await gateway.RequestAsync("sessions.subscribe", new Dictionary<string, object>(), CancellationToken.None);
            if (args.Length > 4)
            {
                await gateway.RequestAsync("sessions.messages.subscribe",
                    new Dictionary<string, object> { ["key"] = args[4] }, CancellationToken.None);
                Console.Error.WriteLine($"also subscribed to messages on {args[4]}");
            }
            Console.Error.WriteLine($"listening for {seconds}s{(filter is null ? "" : $" on {filter}")}");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            break;
        }

        // For the methods this app has never called. The gateway's own error is
        // more informative than a guess about whether something exists.
        case "raw":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("raw needs a method name");
                return 1;
            }

            var parameters = args.Length > 2
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(args[2]) ?? new()
                : new Dictionary<string, object>();

            var res = await gateway.RequestAsync(args[1], parameters, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(res, pretty));
            break;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

return 0;

static string? Field(JsonElement e, string name) =>
    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        : null;

static string? Opt(string label, string? value) => value is null ? null : $"{label}={value}";
