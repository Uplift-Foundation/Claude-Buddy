using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers reading every Claude Code account at once: one source per account, one
// merged and deduplicated list, one owner per session, and the login that owns a
// session being the one its requests carry.
//
// The last is the test that matters. A cloud session belongs to the account that
// created it, and a send with another account's token is refused at best — so
// two accounts get two distinct canary tokens and every request is asserted
// against the token of the account it was made for.
[Collection("Settings")]
public class CloudAccountBoardTests : IDisposable
{
    private const string TokenA = "sk-ant-oat01-CANARY-A-0123456789abcdef";
    private const string TokenB = "sk-ant-oat01-CANARY-B-fedcba9876543210";
    private const string RootA = "/Users/x/.claude";
    private const string RootB = "/Users/x/.claude-board";

    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => CloudAccounts.SetForTests(null);

    // --- fakes ---------------------------------------------------------------

    private sealed class Creds : ICloudCredentialSource
    {
        internal string? StampValue { get; set; } = "s1";
        internal CredentialRead Reading { get; set; }
        internal int Reads;

        internal Creds(string token) =>
            Reading = new(CredentialOutcome.Found, token, null, "a credential is present");

        public string? Stamp() => StampValue;

        public CredentialRead Read()
        {
            Interlocked.Increment(ref Reads);
            return Reading;
        }
    }

    // Records the token each path was requested with.
    private sealed class Api : ICloudApi
    {
        private readonly Func<string, string, CloudApiResult> _answer;
        internal List<(string Token, string Path)> Calls { get; } = new();

        internal Api(Func<string, string, CloudApiResult> answer) => _answer = answer;

        public Task<CloudApiResult> GetAsync(CloudRequestContext context, CancellationToken token)
        {
            lock (Calls) Calls.Add((context.AccessToken, context.Path));
            return Task.FromResult(_answer(context.AccessToken, context.Path));
        }
    }

    private static CloudApiResult Ok(string body) => new(CloudOutcomes.OutcomeFor(200, body), body);

    private static CloudApiResult Fail(int status) => new(CloudOutcomes.OutcomeFor(status, null), null);

    private static string Roster(params string[] ids) =>
        "{\"data\":[" + string.Join(",", ids.Select(id =>
            "{\"id\":\"" + id + "\",\"environment_kind\":\"anthropic_cloud\",\"session_status\":\"running\","
            + "\"status_bucket\":\"running\",\"title\":\"t-" + id + "\",\"updated_at\":\"2026-09-19T10:00:00Z\"}"))
        + "],\"has_more\":false,\"last_id\":null}";

    private static CloudAccount Account(string root, ICloudCredentialSource source) =>
        new(root, root == RootA ? "default" : ClaudeCliCredentials.SafeLabel(root), source, source);

    private static ClaudeCloudSessions.Session S(string id, string? owner, int minute = 0, string title = "t") =>
        new(id, title, "idle", new DateTime(2026, 9, 19, 10, minute, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null, owner);

    private static async Task<ClaudeCloudSessions.StepResult> Step(
        Api api, Creds creds, DateTime? now = null, SemaphoreSlim? gate = null) =>
        await ClaudeCloudSessions.StepAsync(api, creds, ClaudeCloudSessions.ArmState.Initial,
            now ?? Now, CancellationToken.None, readGate: gate);

    // --- dedup ---------------------------------------------------------------

    [Fact]
    public void OneIdSeenByTwoAccountsIsOneSessionOwnedByTheFirstRoot()
    {
        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_1", RootA) }, new[] { S("session_1", RootB) } },
            Array.Empty<ClaudeCloudSessions.Session>());

        var only = Assert.Single(merged);
        Assert.Equal(RootA, only.OwnerRoot);
    }

    [Fact]
    public void OwnershipStaysWithThePreviousOwnerWhileItStillSeesTheSession()
    {
        var previous = new[] { S("session_1", RootB) };

        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_1", RootA) }, new[] { S("session_1", RootB) } }, previous);

        Assert.Equal(RootB, Assert.Single(merged).OwnerRoot);
    }

    [Fact]
    public void OwnershipMovesOnWhenThePreviousOwnerNoLongerSeesIt()
    {
        var previous = new[] { S("session_1", RootB) };

        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_1", RootA) }, Array.Empty<ClaudeCloudSessions.Session>() }, previous);

        Assert.Equal(RootA, Assert.Single(merged).OwnerRoot);
    }

    [Fact]
    public void TheNewestCopyProvidesTheRowDataWhoeverOwnsIt()
    {
        var merged = ClaudeCloudRoster.MergeAccounts(
            new[]
            {
                new[] { S("session_1", RootA, minute: 1, title: "old") },
                new[] { S("session_1", RootB, minute: 9, title: "new") },
            },
            Array.Empty<ClaudeCloudSessions.Session>());

        var only = Assert.Single(merged);
        Assert.Equal("new", only.Title);
        Assert.Equal(RootA, only.OwnerRoot);
    }

    [Fact]
    public void DistinctSessionsFromBothAccountsAreAllKeptNewestFirst()
    {
        var merged = ClaudeCloudRoster.MergeAccounts(
            new[] { new[] { S("session_a", RootA, minute: 1) }, new[] { S("session_b", RootB, minute: 5) } },
            Array.Empty<ClaudeCloudSessions.Session>());

        Assert.Equal(new[] { "session_b", "session_a" }, merged.Select(m => m.Id));
    }

    // --- status text -----------------------------------------------------------

    [Fact]
    public void OneAccountReadsExactlyAsItAlwaysDid()
    {
        Assert.Equal("no cloud sessions (578 sessions inspected)",
            ClaudeCloudRoster.DescribeAccounts(new[] { ("default", "no cloud sessions (578 sessions inspected)") }, 0));
    }

    [Fact]
    public void SeveralAccountsGetAHeaderAndOneLineEach()
    {
        var text = ClaudeCloudRoster.DescribeAccounts(
            new[] { ("default", "1 cloud session (578 sessions inspected)"), ("board", "the Claude Code CLI signed this login out") },
            1);

        Assert.Equal("2 accounts, 1 cloud session\ndefault: 1 cloud session (578 sessions inspected)\n"
                     + "board: the Claude Code CLI signed this login out", text);
        Assert.StartsWith("3 accounts, 2 cloud sessions", ClaudeCloudRoster.DescribeAccounts(
            new[] { ("a", "x"), ("b", "y"), ("c", "z") }, 2));
    }

    [Fact]
    public void ARootNamedLikeAnEmailNeverPutsAnAtSignInTheText()
    {
        var accounts = ClaudeCliCredentials.SourcesFor(isMacOS: false, "/Users/x", "/Users/x/.claude-me@example.com");
        var board = new CloudAccountBoard(accounts);

        Assert.DoesNotContain("@", board.StatusText);
        Assert.Equal("me", accounts[1].Label);
        Assert.Equal("account", ClaudeCliCredentials.SafeLabel("/Users/x/@example.com"));
    }

    // --- the board ---------------------------------------------------------------

    private static (CloudAccountBoard Board, Creds A, Creds B) TwoAccounts()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        return (new CloudAccountBoard(new[] { Account(RootA, a), Account(RootB, b) }), a, b);
    }

    [Fact]
    public async Task AccountAGetting401IsHaltedWhileBsSessionsStillPublish()
    {
        var (board, a, b) = TwoAccounts();
        var api = new Api((token, _) => token == TokenA ? Fail(401) : Ok(Roster("session_b1")));

        var stepA = await Step(api, a);
        var stepB = await Step(api, b);
        board.Apply(RootA, stepA, false, a.Stamp, Now);
        var merged = board.Apply(RootB, stepB, false, b.Stamp, Now);

        Assert.True(stepA.Next.Halted);
        Assert.Equal("session_b1", Assert.Single(merged).Id);
        Assert.Equal(RootB, merged[0].OwnerRoot);
        var lines = board.StatusText.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("default: ", lines[1]);
        Assert.StartsWith("board: 1 cloud session", lines[2]);
    }

    [Fact]
    public async Task ANullSnapshotFromAKeepsAsSlotAndAHaltEmptiesOnlyItsOwn()
    {
        var (board, a, b) = TwoAccounts();
        var ok = new Api((_, _) => Ok(Roster("session_1")));
        board.Apply(RootA, await Step(ok, a), false, a.Stamp, Now);
        board.Apply(RootB, await Step(new Api((t, _) => Ok(Roster("session_2"))), b), false, b.Stamp, Now);

        // A retryable failure: no snapshot, so A's orbs stay.
        var failed = await Step(new Api((_, _) => Fail(503)), a);
        Assert.Null(failed.Snapshot);
        var kept = board.Apply(RootA, failed, false, a.Stamp, Now);
        Assert.Equal(2, kept.Count);

        // A refusal: A's slot empties, B's does not.
        var refused = await Step(new Api((_, _) => Fail(401)), a);
        var after = board.Apply(RootA, refused, false, a.Stamp, Now);
        Assert.Equal("session_2", Assert.Single(after).Id);
    }

    [Fact]
    public async Task BothAccountsSeeingOneIdIsOneOrbAndTheOwnerDoesNotFlap()
    {
        var (board, a, b) = TwoAccounts();
        var api = new Api((_, _) => Ok(Roster("session_shared")));

        board.Apply(RootB, await Step(api, b), false, b.Stamp, Now);
        var first = board.Apply(RootA, await Step(api, a), false, a.Stamp, Now);

        var only = Assert.Single(first);
        // B reported it first; A joining later must not take it over.
        Assert.Equal(RootB, only.OwnerRoot);
    }

    [Fact]
    public async Task ADeclinedPromptLatchesOtherRootsKeychainUntilThatAccountsStampMoves()
    {
        var (board, a, b) = TwoAccounts();
        a.Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "declined");
        var stepA = await Step(new Api((_, _) => Ok(Roster())), a);
        Assert.True(stepA.PromptDeclined);

        board.Apply(RootA, stepA, false, a.Stamp, Now);

        Assert.True(board.KeychainSkippedFor(RootB));
        Assert.False(board.KeychainSkippedFor(RootA));

        a.StampValue = "s2"; // the user signed in again
        Assert.False(board.KeychainSkippedFor(RootB));
    }

    [Fact]
    public async Task ASkippedAccountThatFindsNothingSaysItWasNotAsked()
    {
        var (board, _, b) = TwoAccounts();
        b.Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "no credential stored");
        var stepB = await Step(new Api((_, _) => Ok(Roster())), b);
        Assert.True(stepB.Next.Halted);

        board.Apply(RootB, stepB, keychainSkipped: true, b.Stamp, Now);

        Assert.Contains(CloudAccountBoard.NotAsked, board.StatusText);
    }

    [Fact]
    public async Task NoAnswerAlsoCountsAsDeclined()
    {
        var (_, a, _) = TwoAccounts();
        a.Reading = new CredentialRead(CredentialOutcome.NoAnswer, null, null, "no answer");

        Assert.True((await Step(new Api((_, _) => Ok(Roster())), a)).PromptDeclined);
    }

    [Fact]
    public async Task ARateLimitOnOneAccountHoldsEveryAccountBack()
    {
        var (board, a, _) = TwoAccounts();
        var step = await Step(new Api((_, _) => Fail(429)), a);
        Assert.True(step.RateLimited);

        board.Apply(RootA, step, false, a.Stamp, Now);

        Assert.True(board.HoldRemaining(Now) >= Backoff.RateLimitFloor);
        Assert.Equal(TimeSpan.Zero, board.HoldRemaining(Now + TimeSpan.FromMinutes(10)));
        Assert.False((await Step(new Api((_, _) => Fail(503)), a)).RateLimited);
    }

    [Fact]
    public void AnUnexpectedErrorIsSaidOnThatAccountsLineOnly()
    {
        var (board, _, _) = TwoAccounts();

        board.ApplyError(RootB, "boom");

        Assert.Contains("board: boom", board.StatusText);
        Assert.Contains("default: checking…", board.StatusText);
        Assert.Equal(2, board.Accounts.Count);
        Assert.Empty(board.Merged);
    }

    // --- serialised reads ---------------------------------------------------------

    private sealed class Hanging : ICloudCredentialSource, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);
        internal int Reads;
        public string? Stamp() => "s";

        public CredentialRead Read()
        {
            Interlocked.Increment(ref Reads);
            _gate.Wait();
            return new CredentialRead(CredentialOutcome.Found, "late", null, "late");
        }

        public void Dispose() { _gate.Set(); _gate.Dispose(); }
    }

    [Fact]
    public async Task TheSecondAccountsReadIsNotEnteredUntilTheFirstTimesOut()
    {
        using var first = new Hanging();
        using var second = new Hanging();
        using var gate = new SemaphoreSlim(1, 1);
        var api = new Api((_, _) => Ok(Roster()));
        var budget = TimeSpan.FromMilliseconds(400);

        var t1 = ClaudeCloudSessions.StepAsync(api, first, ClaudeCloudSessions.ArmState.Initial, Now,
            CancellationToken.None, budget, gate);
        var t2 = ClaudeCloudSessions.StepAsync(api, second, ClaudeCloudSessions.ArmState.Initial, Now,
            CancellationToken.None, budget, gate);

        await Task.Delay(150);
        Assert.Equal(1, first.Reads);
        Assert.Equal(0, second.Reads);

        var r1 = await t1;
        var r2 = await t2;
        Assert.Equal(1, second.Reads);
        Assert.Contains("did not answer", r1.Status + r2.Status, StringComparison.OrdinalIgnoreCase);
    }

    // --- the owner's login, end to end -------------------------------------------

    [Fact]
    public async Task EveryRequestForABOwnedSessionCarriesBsTokenAndNeverAs()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        CloudAccounts.SetForTests(new[] { Account(RootA, a), Account(RootB, b) });

        // The poll: each account lists with its own token.
        var pollApi = new Api((token, _) => Ok(Roster(token == TokenA ? "session_a1" : "session_b1")));
        var board = new CloudAccountBoard(CloudAccounts.Current);
        board.Apply(RootA, await Step(pollApi, a), false, a.Stamp, Now);
        var merged = board.Apply(RootB, await Step(pollApi, b), false, b.Stamp, Now);
        var bSession = merged.Single(s => s.Id == "session_b1");
        Assert.Equal(RootB, bSession.OwnerRoot);

        // The chat: resolved through the same registry, by the session's owner.
        var chatApi = new Api((_, _) => Ok("{\"data\":[],\"has_more\":false,\"last_id\":null}"));
        var chat = new ClaudeCloudChatSession(
            bSession, chatApi, CloudAccounts.SourceFor(bSession.OwnerRoot), action => action());
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.All(chatApi.Calls, c => Assert.Equal(TokenB, c.Token));
        Assert.DoesNotContain(chatApi.Calls, c => c.Token == TokenA);
        Assert.All(pollApi.Calls.Where(c => c.Token == TokenA), c => Assert.NotEqual(TokenB, c.Token));
    }

    [Fact]
    public async Task ReopeningAfterOwnershipMovesReadsWithTheNewOwnersLogin()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        CloudAccounts.SetForTests(new[] { Account(RootA, a), Account(RootB, b) });
        var api = new Api((_, _) => Ok("{\"data\":[],\"has_more\":false,\"last_id\":null}"));
        var chat = new ClaudeCloudChatSession(S("session_1", RootA), api, CloudAccounts.SourceFor(RootA), x => x());

        await chat.LoadAsync(CancellationToken.None);
        chat.UseCredentials(CloudAccounts.SourceFor(RootB + "/"));
        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal(TokenA, api.Calls.First().Token);
        Assert.Equal(TokenB, api.Calls.Last().Token);
    }

    [Fact]
    public void AnUnknownOrMissingOwnerFallsBackToTheFirstAccount()
    {
        var a = new Creds(TokenA);
        var b = new Creds(TokenB);
        CloudAccounts.SetForTests(new[] { Account(RootA, a), Account(RootB, b) });

        Assert.Same(a, CloudAccounts.SourceFor(null));
        Assert.Same(a, CloudAccounts.SourceFor("/somewhere/else"));
        Assert.Same(b, CloudAccounts.SourceFor(RootB));
    }
}
