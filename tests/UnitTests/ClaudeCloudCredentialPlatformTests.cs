using System.IO;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers which credential store each platform uses.
//
// The platform is an argument rather than something SourceFor asks the runtime,
// for the same reason OrbGlyph takes the two-letter setting instead of reading
// it: a decision that reads its own environment can only be tested on the
// machine that happens to agree with it, and CI runs both legs. This way the
// Windows arm is verified on a Mac and the macOS arm on Windows.
//
// Nothing here touches the real Keychain or a real credential file. Constructing
// KeychainCredentialSource does not query anything — the query happens in
// Stamp() and Read(), which these tests do not call.
[Collection("Settings")]
public class ClaudeCloudCredentialPlatformTests
{
    private static string Home => Path.Combine("/Users", "someone");

    [Fact]
    public void MacOsUsesTheKeychain()
    {
        var multi = ClaudeCliCredentials.SourceFor(isMacOS: true, Home);

        Assert.Equal(new[]
        {
            "Claude Code-credentials",
            ClaudeCliCredentials.KeychainServiceFor(Path.Combine(Home, ".claude")),
            // The CLI's plaintext fallback, walked after the Keychain candidates.
            Path.Combine(Home, ".claude", ".credentials.json"),
        }, multi.Names);
    }

    [Fact]
    public void EverywhereElseUsesTheCredentialsFile()
    {
        var source = ClaudeCliCredentials.SourceFor(isMacOS: false, Home);

        Assert.Equal(new[] { Path.Combine(Home, ".claude", ".credentials.json") }, source.Names);
    }

    // **Inside the config root, unlike UsageAccounts' `.claude.json`, which is a
    // sibling of it.** The two files are neighbours with different rules, and
    // reasoning from one to the other is a trap that has already cost this
    // repository once — see UsageAccountsTests for the version of it that
    // produces no error at all.
    [Fact]
    public void TheCredentialsFileLivesInsideTheConfigRoot()
    {
        var root = Path.Combine(Home, ".claude-work");

        Assert.Equal(Path.Combine(root, ".credentials.json"),
            ClaudeCliCredentials.CredentialsFilePath(root));
    }

    // ## Service-name derivation (CB-221)

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoConfigDirIsTheUnsuffixedName(string? dir)
    {
        Assert.Equal("Claude Code-credentials", ClaudeCliCredentials.KeychainServiceFor(dir));
    }

    // Literal computed with: printf %s '/Users/x/.claude-board' | shasum -a 256
    [Fact]
    public void ACustomConfigDirIsSuffixedWithTheFirstEightHexOfItsSha256()
    {
        Assert.Equal("Claude Code-credentials-301e83ae",
            ClaudeCliCredentials.KeychainServiceFor("/Users/x/.claude-board"));
    }

    // The CLI suffixes even when CLAUDE_CONFIG_DIR names ~/.claude.
    [Fact]
    public void TheDefaultPathIsStillSuffixedWhenPassedExplicitly()
    {
        Assert.Equal("Claude Code-credentials-c72cc1ce",
            ClaudeCliCredentials.KeychainServiceFor("/Users/x/.claude"));
    }

    [Fact]
    public void TheDirIsNormalisedToNfcBeforeHashing()
    {
        var decomposed = "/Users/jose\u0301/.cl";
        var composed = "/Users/jos\u00e9/.cl";

        Assert.Equal("Claude Code-credentials-aa4cb181", ClaudeCliCredentials.KeychainServiceFor(decomposed));
        Assert.Equal(ClaudeCliCredentials.KeychainServiceFor(composed),
            ClaudeCliCredentials.KeychainServiceFor(decomposed));
    }

    [Fact]
    public void SecureStorageDirOverridesTheConfigDir()
    {
        Assert.Equal(ClaudeCliCredentials.KeychainServiceFor("/Users/x/.claude-board"),
            ClaudeCliCredentials.KeychainServiceFor("/elsewhere", "/Users/x/.claude-board"));
        Assert.Equal(ClaudeCliCredentials.KeychainServiceFor("/Users/x/.claude-board"),
            ClaudeCliCredentials.KeychainServiceFor(null, "/Users/x/.claude-board"));
    }

    [Fact]
    public void AnEmptySecureStorageDirForcesTheUnsuffixedNameEvenWithAConfigDir()
    {
        Assert.Equal("Claude Code-credentials",
            ClaudeCliCredentials.KeychainServiceFor("/Users/x/.claude-board", ""));
    }

    // ## Candidates

    [Fact]
    public void TheDefaultRootIsAskedUnsuffixedThenSuffixed()
    {
        var roots = ClaudeCliCredentials.CandidateRoots(Home, null);
        var services = ClaudeCliCredentials.CandidateServices(Home, roots);

        Assert.Equal(new[]
        {
            "Claude Code-credentials",
            ClaudeCliCredentials.KeychainServiceFor(Path.Combine(Home, ".claude")),
        }, services);
    }

    [Fact]
    public void AConfiguredExtraRootIsSuffixedOnlyAndComesAfterTheDefault()
    {
        ClaudeBuddySettings.AddClaudeCodeProfileDir(".claude-board");
        try
        {
            var roots = ClaudeCliCredentials.CandidateRoots(Home, null);
            var services = ClaudeCliCredentials.CandidateServices(Home, roots);

            Assert.Equal(3, services.Count);
            Assert.Equal(ClaudeCliCredentials.KeychainServiceFor(Path.Combine(Home, ".claude-board")),
                services[2]);
        }
        finally
        {
            ClaudeBuddySettings.RemoveClaudeCodeProfileDir(".claude-board");
        }
    }

    [Fact]
    public void TheProcessConfigDirIsAddedOnceAndNotDuplicated()
    {
        var custom = Path.Combine(Home, ".elsewhere");

        Assert.Equal(custom, ClaudeCliCredentials.CandidateRoots(Home, custom).Last());
        Assert.Single(ClaudeCliCredentials.CandidateRoots(Home, custom), r => r == custom);

        var def = Path.Combine(Home, ".claude");
        Assert.Single(ClaudeCliCredentials.CandidateRoots(Home, def));
    }

    [Fact]
    public void DuplicateServicesAreDropped()
    {
        var root = Path.Combine(Home, ".x");
        Assert.Single(ClaudeCliCredentials.CandidateServices(Home, new[] { root, root }));
    }

    [Fact]
    public void EveryRootGetsItsOwnCredentialsFileOffMacOS()
    {
        var custom = Path.Combine(Home, ".elsewhere");
        var source = ClaudeCliCredentials.SourceFor(isMacOS: false, Home, custom);

        Assert.Equal(new[]
        {
            Path.Combine(Home, ".claude", ".credentials.json"),
            Path.Combine(custom, ".credentials.json"),
        }, source.Names);
    }

    // ## Walking the candidates

    private sealed class Fake : ICloudCredentialSource
    {
        private readonly string? _stamp;
        private readonly CredentialRead _read;
        internal int Reads;

        internal Fake(string? stamp, CredentialOutcome outcome, string? detail = null)
        {
            _stamp = stamp;
            _read = new CredentialRead(outcome, outcome == CredentialOutcome.Found ? "tok" : null, null,
                detail ?? outcome.ToString());
        }

        public string? Stamp() => _stamp;
        public CredentialRead Read() { Reads++; return _read; }
    }

    private static MultiCredentialSource Multi(params (string, Fake)[] children) =>
        new(children.Select(c => (c.Item1, (ICloudCredentialSource)c.Item2)).ToList());

    [Fact]
    public void ABlankedFirstEntryFallsThroughToTheLiveOne()
    {
        var blank = new Fake("1", CredentialOutcome.NotLoggedIn);
        var live = new Fake("2", CredentialOutcome.Found);
        var multi = Multi(("a", blank), ("b", live));

        var read = multi.Read();

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Equal("b", multi.AnsweredBy);
        Assert.Equal(new[] { ("a", CredentialOutcome.NotLoggedIn, "NotLoggedIn"), ("b", CredentialOutcome.Found, "Found") },
            multi.Attempts);
    }

    [Fact]
    public void TheFirstFoundWinsAndLaterStoresAreNotRead()
    {
        var first = new Fake("1", CredentialOutcome.Found);
        var second = new Fake("2", CredentialOutcome.Found);

        Multi(("a", first), ("b", second)).Read();

        Assert.Equal(0, second.Reads);
    }

    [Fact]
    public void AStoreWithNoStampIsSkippedWithoutBeingRead()
    {
        var absent = new Fake(null, CredentialOutcome.Found);
        var live = new Fake("2", CredentialOutcome.Found);
        var multi = Multi(("a", absent), ("b", live));

        multi.Read();

        Assert.Equal(0, absent.Reads);
        Assert.Equal("b", multi.AnsweredBy);
        Assert.Equal(("a", CredentialOutcome.NotLoggedIn, "no such item or file"), multi.Attempts[0]);
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("NoAnswer")]
    public void DeniedAndNoAnswerStopTheWalkSoNoSecondPromptIsRaised(string name)
    {
        var outcome = System.Enum.Parse<CredentialOutcome>(name);
        var stopped = new Fake("1", outcome);
        var next = new Fake("2", CredentialOutcome.Found);
        var multi = Multi(("a", stopped), ("b", next));

        var read = multi.Read();

        Assert.Equal(outcome, read.Outcome);
        Assert.Equal(0, next.Reads);
        Assert.Null(multi.AnsweredBy);
    }

    [Fact]
    public void WhenNothingIsFoundTheMostInformativeFailureWins()
    {
        var multi = Multi(
            ("a", new Fake("1", CredentialOutcome.NotLoggedIn)),
            ("b", new Fake("2", CredentialOutcome.Malformed)),
            ("c", new Fake("3", CredentialOutcome.Unreadable)),
            ("d", new Fake("4", CredentialOutcome.NotLoggedIn)));

        Assert.Equal(CredentialOutcome.Malformed, multi.Read().Outcome);
    }

    [Fact]
    public void NothingStoredAnywhereIsNotLoggedIn()
    {
        var multi = Multi(("a", new Fake(null, CredentialOutcome.Found)));

        var read = multi.Read();

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
        Assert.Single(multi.Attempts);
        Assert.Null(multi.Stamp());
    }

    [Fact]
    public void AnExpiredStoreReportsItsExpiryTimeInTheReason()
    {
        var expiry = new System.DateTimeOffset(2026, 9, 19, 12, 0, 0, System.TimeSpan.Zero);
        var expired = new ExpiredFake(expiry);
        var multi = new MultiCredentialSource(new (string, ICloudCredentialSource)[] { ("a", expired) });

        multi.Read();

        Assert.Contains("expiresAt 2026-09-19 12:00:00Z", multi.Attempts[0].Reason);
    }

    private sealed class ExpiredFake : ICloudCredentialSource
    {
        private readonly System.DateTimeOffset _expiry;
        internal ExpiredFake(System.DateTimeOffset expiry) => _expiry = expiry;
        public string? Stamp() => "1";
        public CredentialRead Read() =>
            new(CredentialOutcome.NotLoggedIn, null, _expiry, "the stored credential expired");
    }

    [Fact]
    public void TheStampNamesWhichStoreHoldsTheLoginAndOmitsAbsentOnes()
    {
        var multi = Multi(("a", new Fake(null, CredentialOutcome.Found)), ("b", new Fake("9", CredentialOutcome.Found)));

        Assert.Equal("b=9", multi.Stamp());
    }
}
