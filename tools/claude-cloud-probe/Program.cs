using System.Text;
using System.Text.Json;
using ClaudeBuddy;

// Settles CB-164's last open question from a terminal, with a human present.
//
// ## What the question is
//
// CB-164 settled the load-bearing one — the Claude Code CLI's own stored token
// is accepted at `GET https://api.anthropic.com/v2/ccr-sessions`, returning 200
// with two headers and no cookie. What is left for this tool is everything that
// needs a real credential and a real roster: confirming the store still holds
// what we think it holds on a given machine, and capturing the *shape* of a
// payload without capturing anybody's session titles.
//
// It runs the shipped code — the same credential source the app uses, the same
// request builder, the same status mapping — so its answer is the app's answer
// rather than a second opinion.
//
// **The host matters and is the trap this ticket paid for.** Aimed at claude.ai,
// the identical request is answered by a Cloudflare challenge that looks exactly
// like an auth failure — the same 403 for a real token and a bogus one. If this
// tool ever reports something that reads as "the account is not allowed", check
// the host before checking the account.
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
            "roster" => await RosterAsync(),
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
            "  roster             make the real call; print the environment-kind histogram\n" +
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

        // Printed as a presence, never as a value, and nothing sends it — see
        // ClaudeCliCredentials.OrganizationUuidFrom. It is here because knowing
        // *whether* the config names an organisation is useful when working out
        // whose credential is in the store.
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

        // **No organisation gate any more.** This used to refuse to make the call
        // at all without an `organizationUuid` out of `~/.claude.json`, because
        // `x-organization-uuid` was believed mandatory. It is claude.ai's header;
        // api.anthropic.com ignores it. A probe that refuses to run for want of a
        // value nothing sends is a diagnostic that reports its own assumption as
        // the machine's problem.
        using var api = new HttpCloudApi();
        var result = await api.GetAsync(
            new CloudRequestContext(read.AccessToken,
                CloudRequest.ListPath(CloudRequest.MaxPageSize, null)),
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

    // The whole roster, reduced to counts.
    //
    // **This is the subcommand that answers "is the filter still right".** It
    // walks every page the way the app does, runs the shipped
    // ClaudeCloudRoster.Reduce over the result and prints the environment-kind
    // histogram plus the status sentence the settings window would show. No
    // titles, no ids, no timestamps — a count per kind is a fact about the
    // account's shape and names nobody.
    //
    // A run showing 573 bridge and 5 anthropic_cloud is the measurement CB-164
    // was built on. A run showing a kind this version does not know is the
    // earliest warning that the filter has stopped matching, and it is much
    // cheaper to read here than to diagnose from a screenshot of missing orbs.
    private static async Task<int> RosterAsync()
    {
        var read = Source().Read();
        if (read.Outcome != CredentialOutcome.Found || read.AccessToken is null)
        {
            Console.Error.WriteLine(
                $"no usable credential: {ClaudeCliCredentials.Describe(read.Outcome)}");
            return 1;
        }

        using var api = new HttpCloudApi();

        var pages = new List<ClaudeCloudRoster.Page>();
        string? after = null;

        for (var i = 0; i < CloudRequest.MaxPagesPerWalk; i++)
        {
            var result = await api.GetAsync(
                new CloudRequestContext(read.AccessToken,
                    CloudRequest.ListPath(CloudRequest.MaxPageSize, after)),
                CancellationToken.None);

            if (result.Outcome.Kind != CloudOutcomeKind.Ok)
            {
                Console.Error.WriteLine($"page {i + 1}: {result.Outcome.Kind} {result.Outcome.Status}");
                if (result.Outcome.Detail is { } why) Console.Error.WriteLine($"  {why}");
                return 1;
            }

            var page = ClaudeCloudRoster.ParsePage(result.Body);
            pages.Add(page);

            if (!page.HasMore || page.LastId is null) { after = null; break; }
            after = page.LastId;
        }

        var reduction = ClaudeCloudRoster.Reduce(pages, after is not null);

        Console.WriteLine($"pages     {pages.Count}");
        Console.WriteLine($"inspected {reduction.Inspected}");
        Console.WriteLine($"truncated {reduction.Truncated}");
        Console.WriteLine("kinds:");
        foreach (var kind in reduction.Kinds.OrderByDescending(k => k.Value))
        {
            var known = ClaudeCloudRoster.IsKnownKind(kind.Key) ? "" : "   <- unknown to this version";
            Console.WriteLine($"  {kind.Key,-20} {kind.Value}{known}");
        }

        Console.WriteLine($"orbs      {reduction.Sessions.Count}");
        Console.WriteLine($"status    {ClaudeCloudRoster.Describe(reduction)}");
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
