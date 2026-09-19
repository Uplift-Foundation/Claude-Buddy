using System.Text;
using System.Text.Json;
using ClaudeBuddy;

// Settles CB-164's last open question from a terminal, with a human present.
//
// ## What the question is
//
// CB-164 measured that claude.ai's `/v1/code/sessions` accepts an OAuth Bearer
// token as a first-class scheme — a well-formed-but-invalid one is refused with
// "OAuth access token is invalid.", where no credential, an empty Bearer and a
// bogus x-api-key all fall through to a generic "Authentication failed". That
// asymmetry is the measurement, and the negative controls are what make it one.
//
// **What it does not establish is whether the Claude Code CLI's own stored token
// is accepted here.** A token minted for one audience can be perfectly
// well-formed and still refused by another, and that refusal reads *identically*
// to the placeholder's. Telling the two apart needs the real token, and nothing
// short of sending it will do it.
//
// This tool is the way to send it. It runs the shipped code — the same
// credential source the app would use, the same six headers, the same status
// mapping — so its answer is the app's answer rather than a second opinion.
//
// ## Why it is a separate binary rather than something the app does
//
// Two reasons, and both are about consent.
//
// On macOS, reading the credential raises a Keychain prompt naming the calling
// binary. Running this is a deliberate act by a person at a keyboard who can see
// that prompt and answer it. The app must not take that step on anybody's behalf
// before the feature is opted into, and nothing built on top of this file may
// ship reading a credential by default.
//
// The prompt this raises names *this binary*, not Claude Buddy — it is a
// separate unsigned executable. That is expected and is not the rule the app
// follows being bent: the P/Invoke-rather-than-`security` argument in
// MacOSKeychain is about what the shipped, signed app puts on that screen. A
// grant given to the probe is a grant to the probe, and it does not carry over.
//
// And an agent must not run this. CB-164's own investigation had two attempts to
// reach the credential refused by a safety classifier, and neither was worked
// around; that refusal stands. The code below reads a credential when a *human*
// runs it.
//
// ## What it never prints
//
// No subcommand prints a token. `read` prints its length and first four
// characters at most, which is enough to tell "we read something plausible" from
// "we read an empty string" and not enough to be a credential. `list --shape`
// prints field names and JSON types with every value stripped, so a fixture for
// the roster parser can be designed without a single session title — several of
// which, per this ticket, are personal.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var flags = args.Skip(1).ToArray();

        return args[0] switch
        {
            "stamp" => Stamp(),
            "read" => Read(flags),
            "list" => await ListAsync(flags),
            _ => UnknownCommand(args[0]),
        };
    }

    private static void Usage() =>
        Console.Error.WriteLine(
            "usage: claude-cloud-probe <command>\n" +
            "\n" +
            "  stamp              is a credential present, and what is its change stamp\n" +
            "  read --keys-only   which fields were found, and the outcome (no token value)\n" +
            "  list --raw         make the real call; print status and body\n" +
            "  list --shape       make the real call; print field names and types only\n" +
            "\n" +
            "On macOS, `read` and `list` raise a Keychain consent prompt naming this\n" +
            "binary. That prompt is the point: answer it yourself.");

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        Usage();
        return 2;
    }

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static ICloudCredentialSource Source() =>
        ClaudeCliCredentials.SourceFor(OperatingSystem.IsMacOS(), Home);

    // Attributes only on macOS, an mtime elsewhere. Neither returns secret
    // material and neither prompts, so this is the subcommand to run first: it
    // says whether there is anything to read at all before anyone is asked to
    // approve reading it.
    private static int Stamp()
    {
        var stamp = Source().Stamp();
        if (stamp is null)
        {
            Console.WriteLine("no credential found (nothing stored, or not readable without prompting)");
            return 1;
        }

        Console.WriteLine($"credential present; stamp {stamp}");
        return 0;
    }

    private static int Read(string[] flags)
    {
        if (!flags.Contains("--keys-only"))
        {
            Console.Error.WriteLine(
                "read requires --keys-only. There is no mode that prints the token; the flag is\n" +
                "mandatory so that is obvious from the command line rather than from the output.");
            return 2;
        }

        var read = Source().Read();

        Console.WriteLine($"outcome  {read.Outcome}");
        Console.WriteLine($"meaning  {ClaudeCliCredentials.Describe(read.Outcome)}");
        if (read.Detail is { } detail) Console.WriteLine($"detail   {detail}");
        Console.WriteLine($"expires  {read.ExpiresAt?.ToString("u") ?? "(not stated)"}");

        if (read.AccessToken is { } token)
        {
            // Length and a four-character prefix. The prefix is the CLI's token
            // scheme marker rather than anything secret, and it is what tells a
            // reader the parse found a token rather than an empty string.
            var prefix = token.Length >= 4 ? token[..4] : token;
            Console.WriteLine($"token    present, {token.Length} chars, starts \"{prefix}…\"");
        }
        else
        {
            Console.WriteLine("token    none");
        }

        var orgUuid = OrganizationUuid();
        Console.WriteLine($"org      {(orgUuid is null ? "(not found in ~/.claude.json)" : "found")}");

        return read.Outcome == CredentialOutcome.Found ? 0 : 1;
    }

    private static string? OrganizationUuid()
    {
        var path = UsageAccounts.AccountFilePath(Home, null);
        try
        {
            return ClaudeCliCredentials.OrganizationUuidFrom(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // **This is the measurement the ticket is waiting on.**
    //
    // A 200 says the CLI's token is accepted at this audience and the feature is
    // buildable. A 401 carrying "OAuth access token is invalid." says it is a
    // structurally valid token for a different audience, and the feature is not
    // buildable on this footing — which is a real answer and closes the ticket as
    // firmly as a 200 opens it.
    private static async Task<int> ListAsync(string[] flags)
    {
        var raw = flags.Contains("--raw");
        var shape = flags.Contains("--shape");
        if (raw == shape)
        {
            Console.Error.WriteLine("list needs exactly one of --raw or --shape.");
            return 2;
        }

        var read = Source().Read();
        if (read.Outcome != CredentialOutcome.Found || read.AccessToken is null)
        {
            Console.Error.WriteLine(
                $"no usable credential: {ClaudeCliCredentials.Describe(read.Outcome)}");
            if (read.Detail is { } detail) Console.Error.WriteLine($"  {detail}");
            return 1;
        }

        var orgUuid = OrganizationUuid();
        if (orgUuid is null)
        {
            Console.Error.WriteLine(
                "no organizationUuid in ~/.claude.json. The endpoint refuses a request without\n" +
                "the x-organization-uuid header, so there is nothing to send.");
            return 1;
        }

        using var api = new HttpCloudApi();
        var result = await api.ListAsync(
            new CloudRequestContext(read.AccessToken, orgUuid, CloudRequest.SessionsPath),
            CancellationToken.None);

        Console.WriteLine($"outcome  {result.Outcome.Kind}");
        Console.WriteLine($"status   {result.Outcome.Status}");
        if (result.Outcome.Detail is { } why) Console.WriteLine($"detail   {why}");

        if (result.Body is null)
        {
            Console.WriteLine("body     (none)");
            return result.Outcome.Kind == CloudOutcomeKind.Ok ? 0 : 1;
        }

        if (raw)
        {
            Console.WriteLine("body:");
            Console.WriteLine(result.Body);
            return 0;
        }

        Console.WriteLine("shape:");
        Console.WriteLine(Shape(result.Body));
        return 0;
    }

    // Field names and JSON types, no values anywhere.
    //
    // An array collapses to its first element's shape rather than being printed
    // per row: the roster's rows are homogeneous, and printing fifty of them
    // would say nothing extra while making it likelier that something slipped
    // through. A scalar prints only its type — this is the whole privacy
    // property, so it is the one thing in this file worth reading twice.
    private static string Shape(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            Describe(doc.RootElement, "", builder);
            return builder.ToString().TrimEnd();
        }
        catch (JsonException ex)
        {
            return $"(not JSON: {ex.Message})";
        }
    }

    private static void Describe(JsonElement element, string indent, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    builder.Append(indent).Append(property.Name).Append(": ")
                        .Append(TypeName(property.Value)).AppendLine();
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Describe(property.Value, indent + "  ", builder);
                    }
                }
                break;

            case JsonValueKind.Array:
                var first = element.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined)
                {
                    builder.Append(indent).Append("[0]: ").Append(TypeName(first)).AppendLine();
                    if (first.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Describe(first, indent + "  ", builder);
                    }
                }
                break;
        }
    }

    private static string TypeName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => $"array[{element.GetArrayLength()}]",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Null => "null",
        _ => "?",
    };
}
