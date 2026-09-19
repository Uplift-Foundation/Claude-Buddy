using System;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers turning the Claude Code CLI's stored login into a reading, and the
// custody rules that surround it.
//
// The parse itself is ordinary. What is not ordinary is the last test in this
// file, and it is the reason the others exist at all: this is the first
// credential Claude Buddy has ever held, and the rule the code is written to is
// that nothing token-derived reaches a string a user or a log can see. A rule
// like that is easy to state and easy to break by accident six months later in a
// well-meaning "add the token to the error message so we can debug it" — so the
// negative control feeds distinctive fake token values through every outcome and
// asserts they appear nowhere in the wording.
//
// The fixtures are hand-written rather than captured, deliberately: capturing a
// real one would mean putting a real access token in the repository, which is
// the exact thing this file is about not doing. The shape is read off the
// documented-by-observation structure in ClaudeCliCredentials.
public class ClaudeCliCredentialsParseTests
{
    // Distinctive enough to find anywhere they leak, and obviously not real.
    private const string FakeAccess = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string FakeRefresh = "sk-ant-ort01-CANARY-REFRESH-9876543210fedcba";

    private static readonly DateTimeOffset Now =
        new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Blob(string accessToken, long expiresAtMillis) =>
        $$$"""
        {
          "claudeAiOauth": {
            "accessToken": "{{{accessToken}}}",
            "refreshToken": "{{{FakeRefresh}}}",
            "expiresAt": {{{expiresAtMillis}}},
            "scopes": ["user:inference", "user:profile"],
            "subscriptionType": "max"
          }
        }
        """;

    private static long MillisAt(DateTimeOffset when) => when.ToUnixTimeMilliseconds();

    [Fact]
    public void AWellFormedUnexpiredBlobYieldsTheToken()
    {
        var read = ClaudeCliCredentials.ParseCredentials(
            Blob(FakeAccess, MillisAt(Now.AddHours(1))), Now);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Equal(FakeAccess, read.AccessToken);
        Assert.Equal(Now.AddHours(1), read.ExpiresAt);
    }

    // Expired is NotLoggedIn, not an error. The CLI owns refresh and will do it
    // on its next use; we re-read rather than renewing, because renewing needs
    // the refresh token and a client id, which is the option CB-164 rejected.
    [Fact]
    public void AnExpiredBlobIsNotLoggedInAndHandsBackNoToken()
    {
        var read = ClaudeCliCredentials.ParseCredentials(
            Blob(FakeAccess, MillisAt(Now.AddMinutes(-1))), Now);

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
        Assert.Null(read.AccessToken);
        Assert.Equal(Now.AddMinutes(-1), read.ExpiresAt);
    }

    // The boundary belongs on the expired side: a token expiring this instant is
    // not one to send.
    [Fact]
    public void ATokenExpiringExactlyNowIsExpired()
    {
        var read = ClaudeCliCredentials.ParseCredentials(Blob(FakeAccess, MillisAt(Now)), Now);

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
    }

    [Fact]
    public void AnIsoExpiryIsAcceptedToo()
    {
        var json = $$$"""
            {"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}","expiresAt":"2026-09-19T13:00:00+00:00"}}
            """;

        var read = ClaudeCliCredentials.ParseCredentials(json, Now);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Equal(Now.AddHours(1), read.ExpiresAt);
    }

    // No expiry stated is not a reason to refuse: the endpoint is the authority
    // on whether a token works, and an absent field is a shape we do not own
    // moving, not a credential problem.
    [Fact]
    public void AMissingExpiryIsStillUsable()
    {
        var json = $$$"""{"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}"}}""";

        var read = ClaudeCliCredentials.ParseCredentials(json, Now);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Null(read.ExpiresAt);
    }

    [Fact]
    public void AnUnparseableExpiryIsTreatedAsAbsentRatherThanFatal()
    {
        var json = $$$"""{"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}","expiresAt":"soon"}}""";

        var read = ClaudeCliCredentials.ParseCredentials(json, Now);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Null(read.ExpiresAt);
    }

    // Neither a number nor a string — the arm reached when the field is present
    // but of a type nobody has seen. Worth a case of its own rather than assumed
    // away, because this is an undocumented format and "present but a different
    // type" is exactly how one of those moves.
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AnExpiryOfAnUnexpectedTypeIsTreatedAsAbsent(string expiresAt)
    {
        var json = $$$"""
            {"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}","expiresAt":{{{expiresAt}}}}}
            """;

        var read = ClaudeCliCredentials.ParseCredentials(json, Now);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Null(read.ExpiresAt);
    }

    [Fact]
    public void AnOutOfRangeNumericExpiryIsTreatedAsAbsent()
    {
        var json = $$$"""
            {"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}","expiresAt":99999999999999999}}
            """;

        var read = ClaudeCliCredentials.ParseCredentials(json, Now);

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Null(read.ExpiresAt);
    }

    [Fact]
    public void AMissingClaudeAiOauthObjectIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials("""{"somethingElse":{}}""", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    [Fact]
    public void AClaudeAiOauthThatIsNotAnObjectIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials("""{"claudeAiOauth":"nope"}""", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
    }

    [Fact]
    public void ARootThatIsNotAnObjectIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials("[1,2,3]", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
    }

    [Fact]
    public void AMissingAccessTokenIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials(
            $$$"""{"claudeAiOauth":{"refreshToken":"{{{FakeRefresh}}}"}}""", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
    }

    [Fact]
    public void AnEmptyAccessTokenIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials(
            """{"claudeAiOauth":{"accessToken":""}}""", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
    }

    [Fact]
    public void AnAccessTokenThatIsNotAStringIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials(
            """{"claudeAiOauth":{"accessToken":12345}}""", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
    }

    [Fact]
    public void SomethingThatIsNotJsonIsMalformed()
    {
        var read = ClaudeCliCredentials.ParseCredentials("not json at all {", Now);

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingStoredIsNotLoggedInRatherThanAnError(string? json)
    {
        var read = ClaudeCliCredentials.ParseCredentials(json, Now);

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    // ## The negative control
    //
    // Every outcome's Detail and every Describe string, checked against two
    // canary values chosen so a leak is unmistakable. The refresh token is in the
    // fixture on purpose — the record has no field for it, and this asserts that
    // the absence is real rather than merely intended.
    [Fact]
    public void NoWordingAnywhereCarriesEitherTokenValue()
    {
        var fixtures = new[]
        {
            Blob(FakeAccess, MillisAt(Now.AddHours(1))),   // Found
            Blob(FakeAccess, MillisAt(Now.AddHours(-1))),  // expired
            $$$"""{"claudeAiOauth":{"refreshToken":"{{{FakeRefresh}}}"}}""", // Malformed
            $$$"""{"accessToken":"{{{FakeAccess}}}"}""",       // Malformed, token at the root
            $$$"""not json {{{FakeAccess}}}""",                // Malformed, token in the noise
            "",                                             // NotLoggedIn
        };

        foreach (var fixture in fixtures)
        {
            var read = ClaudeCliCredentials.ParseCredentials(fixture, Now);

            Assert.DoesNotContain(FakeAccess, read.Detail ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(FakeRefresh, read.Detail ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(FakeAccess, ClaudeCliCredentials.Describe(read.Outcome),
                StringComparison.Ordinal);
            Assert.DoesNotContain(FakeRefresh, ClaudeCliCredentials.Describe(read.Outcome),
                StringComparison.Ordinal);

            // And the refresh token never reaches the record at all — there is no
            // field it could land in, and this is what says so out loud.
            Assert.NotEqual(FakeRefresh, read.AccessToken);
        }
    }

    [Fact]
    public void EveryOutcomeHasWordingAndNoneOfItIsEmpty()
    {
        foreach (var outcome in Enum.GetValues<CredentialOutcome>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ClaudeCliCredentials.Describe(outcome)));
        }

        // Including a value outside the enum, which is reachable via a cast and is
        // the arm a `_ =>` exists for.
        Assert.False(string.IsNullOrWhiteSpace(
            ClaudeCliCredentials.Describe((CredentialOutcome)999)));
    }

    [Fact]
    public void TheOrganisationUuidComesOutOfTheOauthAccount()
    {
        var json = """
            {"oauthAccount":{"accountUuid":"a","organizationUuid":"org-1234","emailAddress":"x@y.z"}}
            """;

        Assert.Equal("org-1234", ClaudeCliCredentials.OrganizationUuidFrom(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"oauthAccount":"nope"}""")]
    [InlineData("""{"oauthAccount":{}}""")]
    [InlineData("""{"oauthAccount":{"organizationUuid":null}}""")]
    [InlineData("""{"oauthAccount":{"organizationUuid":""}}""")]
    [InlineData("""{"somethingElse":1}""")]
    public void AnAbsentOrUnreadableOrganisationUuidIsNull(string? json)
    {
        Assert.Null(ClaudeCliCredentials.OrganizationUuidFrom(json));
    }
}
