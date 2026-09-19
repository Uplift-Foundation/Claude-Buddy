using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeBuddy
{
    // Talking to claude.ai's cloud-sessions endpoint.
    //
    // **Everything in this file is about an undocumented private web API.** It is
    // the endpoint claude.ai/code itself calls on page load, observed in a real
    // browser session during CB-164's probe rather than read from documentation,
    // and the product decision on that ticket accepted the maintenance cost of
    // depending on it with eyes open. The consequence for the code is that every
    // shape here is treated as something that can move without notice: a request
    // that comes back wrong produces a legible outcome and no cloud orbs, never a
    // crash and never a retry storm.
    //
    // The transport is a plain HttpClient, as NeuralSpeech uses. Emphatically
    // *not* OpenClawSocket's BouncyCastle TLS stack — that exists because a
    // self-signed LAN gateway speaks TLS 1.3 that .NET on macOS cannot, and
    // claude.ai is an ordinary public HTTPS host with a real certificate. Copying
    // it would mean hand-rolling TLS against a host that does not need it.
    internal enum CloudOutcomeKind
    {
        // 200, with a body to parse.
        Ok,

        // 401, and the endpoint told us it parsed an OAuth bearer token and did
        // not like it. CB-164 measured this as a distinct message; see OutcomeFor.
        TokenRefused,

        // 401 of the generic kind — no credential recognised at all.
        AuthFailed,

        // 400. The endpoint refuses a request missing the header set below, so in
        // practice this means *our* request shape is wrong, which after shipping
        // means the contract moved under us. Named for what it tells the user
        // rather than for the number.
        ShapeChanged,

        // 403. The account may not do this, or something in front of the endpoint
        // declined us. Not retryable by waiting.
        Blocked,

        // 429, with whatever Retry-After came with it.
        RateLimited,

        // 5xx, a timeout, a DNS failure, no network. Retryable, and the only kind
        // that is.
        Unavailable,
    }

    // One attempt's verdict. Status is the HTTP status where there was one and 0
    // where the request never got an answer.
    internal sealed record CloudOutcome(
        CloudOutcomeKind Kind,
        int Status,
        TimeSpan? RetryAfter = null,
        string? Detail = null);

    // A verdict plus the body, for Ok. The body is deliberately a string rather
    // than a parsed roster: parsing belongs to the arm built on top of this, and
    // keeping the two apart is what lets a fixture be captured from the probe
    // without this file knowing anything about session titles.
    internal sealed record CloudApiResult(CloudOutcome Outcome, string? Body);

    // What one call needs to know.
    //
    // **A readonly record struct constructed at the call site and never stored.**
    // It carries the access token, and the custody rules in ClaudeCliCredentials
    // apply to it in full: nothing holds one of these across a request, and
    // nothing puts one in a field.
    internal readonly record struct CloudRequestContext(
        string AccessToken,
        string OrganizationUuid,
        string Path);

    // Where the cloud arm asks, and the exact shape it must ask in.
    internal static class CloudRequest
    {
        internal const string Host = "https://claude.ai";

        // The listing call, as claude.ai/code itself makes it. Both `statuses`
        // values are sent because the roster the web client shows is the union of
        // the two, and a Buddy that showed fewer sessions than the page the click
        // leads to would be quietly wrong.
        internal const string SessionsPath =
            "/v1/code/sessions?statuses=active&statuses=paused&limit=50";

        // **The endpoint 400s without these.** Measured, not guessed: CB-164's
        // probe reproduced the browser's call by hand and a request missing them
        // is refused, which is also the negative control establishing that the 200
        // is a real answer rather than an open endpoint.
        //
        // They are listed as constants so a test can assert each one exactly. A
        // header set copied into prose goes stale; one a test reads off a built
        // request does not.
        internal const string ClientPlatformHeader = "anthropic-client-platform";
        internal const string ClientPlatformValue = "web_claude_ai";
        internal const string VersionHeader = "anthropic-version";
        internal const string VersionValue = "2023-06-01";
        internal const string BetaHeader = "anthropic-beta";
        internal const string BetaValue = "ccr-byoc-2025-07-29";
        internal const string ClientFeatureHeader = "anthropic-client-feature";
        internal const string ClientFeatureValue = "ccr";
        internal const string OrganizationHeader = "x-organization-uuid";

        // How often the cloud arm would poll, if it polls.
        //
        // **This number is not measured and nothing justifies it.** CB-122 is the
        // cautionary tale for exactly this constant: a cadence defended by a
        // comment asserting a fact about an external system that nobody had ever
        // checked, which then ruled out the cheapest explanation for a real bug
        // for hours. So this one says plainly that it is a placeholder. Two things
        // have to happen before it becomes a decision: somebody measures what the
        // endpoint actually does under repeated calls, and somebody weighs
        // /v1/code/sessions/watch — a live update stream CB-164 observed, which
        // would make polling the wrong shape entirely.
        internal static readonly TimeSpan UnmeasuredPollInterval = TimeSpan.FromSeconds(60);

        // Build the request. Pure, so the header set is testable without a socket.
        //
        // content-type goes on an empty body rather than the request's general
        // headers because .NET refuses a content header there, and the browser
        // sends it on this GET. An empty ByteArrayContent gives exactly
        // "application/json" with no charset parameter, where StringContent would
        // append one.
        internal static HttpRequestMessage Build(string token, string orgUuid, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Host + path);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation(ClientPlatformHeader, ClientPlatformValue);
            request.Headers.TryAddWithoutValidation(VersionHeader, VersionValue);
            request.Headers.TryAddWithoutValidation(BetaHeader, BetaValue);
            request.Headers.TryAddWithoutValidation(ClientFeatureHeader, ClientFeatureValue);
            request.Headers.TryAddWithoutValidation(OrganizationHeader, orgUuid);

            // No cookie, ever. The browser's observation of this endpoint rode a
            // session cookie; ours rides the CLI's OAuth token and nothing else.
            // Sending both would make it impossible to tell which one was
            // accepted, which is precisely the question this arm exists to answer.
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            return request;
        }
    }

    // Turning an answer into a verdict, and a verdict into a wait.
    internal static class CloudOutcomes
    {
        // **The 401 asymmetry here is measured, and it is the useful part.**
        // CB-164 sent five variants at this endpoint: no credential at all, an
        // empty Bearer, an invalid x-api-key, and a well-formed but invalid
        // Bearer token. Only the last gets "OAuth access token is invalid." —
        // the other three fall through to a generic "Authentication failed" with
        // a real request_id. That means something upstream parsed the header,
        // recognised an OAuth access token and tried to validate it, which is how
        // we know the endpoint takes Bearer as a first-class scheme rather than
        // being cookie-only.
        //
        // For us it is the difference between "our token was rejected" — stop,
        // re-read the credential, tell the user to sign in — and "we did not
        // present a credential the endpoint understood at all", which is a bug on
        // our side. Both stop; they say different things.
        internal const string TokenRefusedMarker = "OAuth access token is invalid.";

        internal static CloudOutcome OutcomeFor(int status, string? body,
            TimeSpan? retryAfter = null)
        {
            if (status is >= 200 and < 300)
            {
                return new CloudOutcome(CloudOutcomeKind.Ok, status);
            }

            switch (status)
            {
                case 400:
                    return new CloudOutcome(CloudOutcomeKind.ShapeChanged, status, null,
                        "the endpoint refused the request shape");

                case 401:
                    return body is not null
                           && body.Contains(TokenRefusedMarker, StringComparison.Ordinal)
                        ? new CloudOutcome(CloudOutcomeKind.TokenRefused, status, null,
                            "the stored Claude Code login was not accepted")
                        : new CloudOutcome(CloudOutcomeKind.AuthFailed, status, null,
                            "no credential the endpoint recognised");

                case 403:
                    return new CloudOutcome(CloudOutcomeKind.Blocked, status, null,
                        "this account may not list cloud sessions");

                case 429:
                    return new CloudOutcome(CloudOutcomeKind.RateLimited, status, retryAfter,
                        "the endpoint is rate limiting us");

                default:
                    return new CloudOutcome(CloudOutcomeKind.Unavailable, status, retryAfter,
                        $"the endpoint answered {status}");
            }
        }
    }

    // How long to wait before asking again, or whether to stop asking.
    //
    // Null means **stop**, and it is a real answer rather than an absence. CB-164
    // requires that a refused or absent credential produces no cloud orbs, a
    // legible reason and no repeated OS prompt; a backoff that merely got very
    // long would still eventually prompt again, so the stop has to be a distinct
    // value the caller cannot mistake for a long sleep.
    internal static class Backoff
    {
        // What a 429 waits at minimum, whatever Retry-After says. A server that
        // asks us back in one second while rate limiting us is asking for a loop,
        // and this endpoint is one we are a guest on.
        internal static readonly TimeSpan RateLimitFloor = TimeSpan.FromSeconds(60);

        // Where a retryable failure starts, and where it stops growing.
        internal static readonly TimeSpan UnavailableFloor = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(60);

        internal static TimeSpan? Next(CloudOutcome outcome, TimeSpan? previous)
        {
            switch (outcome.Kind)
            {
                // Nothing to wait for.
                case CloudOutcomeKind.Ok:
                    return null;

                // A credential problem, or a decision about this account. Waiting
                // longer changes none of them.
                case CloudOutcomeKind.TokenRefused:
                case CloudOutcomeKind.AuthFailed:
                case CloudOutcomeKind.Blocked:
                    return null;

                case CloudOutcomeKind.RateLimited:
                    var asked = outcome.RetryAfter ?? RateLimitFloor;
                    return asked < RateLimitFloor ? RateLimitFloor : asked;

                // ShapeChanged joins Unavailable rather than stopping: a 400 after
                // shipping most likely means the contract moved, and a deploy in
                // progress looks exactly the same for a few minutes. Backing off
                // to the cap and staying there is quiet enough, and it recovers on
                // its own where a stop would need a restart.
                case CloudOutcomeKind.ShapeChanged:
                case CloudOutcomeKind.Unavailable:
                default:
                    if (previous is not { } last || last < UnavailableFloor)
                    {
                        return UnavailableFloor;
                    }

                    var doubled = last + last;
                    return doubled > Cap ? Cap : doubled;
            }
        }

        // The credential-side half of the same rule, so a caller that never gets
        // as far as an HTTP request still has one place to ask.
        internal static TimeSpan? Next(CredentialOutcome outcome, TimeSpan? previous)
        {
            switch (outcome)
            {
                case CredentialOutcome.Found:
                    return null;

                // Denied is the user saying no, and NotLoggedIn is there being
                // nothing to read. Retrying either means prompting again, which
                // CB-164 rules out in as many words.
                case CredentialOutcome.Denied:
                case CredentialOutcome.NotLoggedIn:
                    return null;

                case CredentialOutcome.Unreadable:
                case CredentialOutcome.Malformed:
                default:
                    if (previous is not { } last || last < UnavailableFloor)
                    {
                        return UnavailableFloor;
                    }

                    var doubled = last + last;
                    return doubled > Cap ? Cap : doubled;
            }
        }
    }

    // The call, as an interface, so everything above it can be driven without a
    // network. Same argument as IUsageSource and ICloudCredentialSource.
    internal interface ICloudApi
    {
        Task<CloudApiResult> ListAsync(CloudRequestContext context, CancellationToken token);
    }

    // The real one.
    //
    // Excluded from coverage: it is an HttpClient talking to claude.ai, and the
    // repository's rule is that tests never touch the real network. Everything it
    // decides — the header set, the status mapping, the wait — is in the pure
    // classes above and is covered there. What is left here is the socket, and a
    // test of that would be a test of HttpClient.
    [ExcludeFromCodeCoverage]
    internal sealed class HttpCloudApi : ICloudApi, IDisposable
    {
        // One client, reused, as .NET wants. 30 seconds because this is a roster
        // fetch behind an ambient overlay: nobody is waiting on it, and a request
        // still outstanding after half a minute is not going to help the user.
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<CloudApiResult> ListAsync(CloudRequestContext context,
            CancellationToken token)
        {
            try
            {
                using var request = CloudRequest.Build(context.AccessToken,
                    context.OrganizationUuid, context.Path);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var retryAfter = response.Headers.RetryAfter?.Delta;
                var outcome = CloudOutcomes.OutcomeFor((int)response.StatusCode, body, retryAfter);

                return new CloudApiResult(outcome,
                    outcome.Kind == CloudOutcomeKind.Ok ? body : null);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new CloudApiResult(
                    new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, "the request timed out"),
                    null);
            }
            catch (HttpRequestException ex)
            {
                return new CloudApiResult(
                    new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, ex.Message),
                    null);
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
