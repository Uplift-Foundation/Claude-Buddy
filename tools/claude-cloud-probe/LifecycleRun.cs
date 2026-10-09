using System.Diagnostics;

namespace Orbweaver;

// CB-225: run the shipped ClaudeCloudLifecycle.RunAsync through the shipped
// HttpCloudApi — the app's exact path, not the probe's own requests — and log
// each call it makes with its status, kind and timing. This is what answers
// "does the delete's confirming read, made the moment the DELETE returns, see
// the session gone?", which the scripted runs could not: they slept between
// the two. Never prints a token, a body or a title.
internal static class LifecycleRun
{
    private sealed class Logged : ICloudApi
    {
        private readonly ICloudApi _inner = new HttpCloudApi();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public async Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            var started = _clock.ElapsedMilliseconds;
            var result = await _inner.SendAsync(context, token);
            Console.WriteLine($"call     +{started}ms {context.Method?.Method ?? "GET"} {context.Path} -> " +
                              $"{result.Outcome.Status} {result.Outcome.Kind} body:{(result.Body is null ? "null" : "present")}" +
                              $" ({_clock.ElapsedMilliseconds - started}ms)");
            return result;
        }
    }

    private sealed class Fixed : ICloudCredentialSource
    {
        private readonly CredentialRead _read;
        internal Fixed(CredentialRead read) => _read = read;
        public string? Stamp() => null;
        public CredentialRead Read() => _read;
    }

    internal static async Task<int> RunAsync(string[] args, Func<Task<CredentialRead>> readCredential)
    {
        if (args.Length < 2 || !ClaudeCloudRoster.IsWellFormedId(args[0])
            || args[1] is not ("archive" or "delete") || !args.Contains("--throwaway"))
        {
            Console.Error.WriteLine("usage: lifecycle-run <session id> archive|delete --throwaway");
            return 2;
        }

        var read = await readCredential();

        // The same guard every write verb has: only a session /v2 calls
        // `anthropic_cloud`. This verb deletes, so it is the one that most
        // needs it — the first version of it shipped without, and was caught
        // in review before it had been aimed at anything but a throwaway.
        if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
        {
            Console.Error.WriteLine($"no usable credential: {ClaudeCliCredentials.Describe(read.Outcome)}");
            return 1;
        }
        var kind = await WriteProbe.KindOfAsync(args[0], token);
        Console.WriteLine($"guard    environment_kind {kind ?? "(unreadable)"}");
        if (kind != "anthropic_cloud")
        {
            Console.Error.WriteLine("refusing: not an anthropic_cloud session.");
            return 2;
        }

        var action = args[1] == "archive" ? CloudLifecycleAction.Archive : CloudLifecycleAction.Delete;

        var result = await ClaudeCloudLifecycle.RunAsync(new Logged(), new Fixed(read), action, args[0],
            CancellationToken.None);

        Console.WriteLine($"verdict  {result.Verdict}");
        Console.WriteLine($"row      {CloudActionText.For(action, result)}");
        return result.Succeeded ? 0 : 1;
    }
}
