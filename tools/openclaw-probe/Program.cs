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
//   dotnet run --project tools/openclaw-probe -- raw <method> [jsonParams]
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
