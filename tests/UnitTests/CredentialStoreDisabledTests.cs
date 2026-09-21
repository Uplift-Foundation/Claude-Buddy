using System;
using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.Tests;

// The guard that stops a test process asking the OS for a credential.
//
// This exists because the absence of it was expensive in a way nothing caught:
// on macOS the cloud arm's credential is a login-Keychain item, reading it from
// another application raises a consent dialog, and a headless suite has nobody
// to answer one. The read then waits out ClaudeCliCredentials.UnmeasuredReadBudget
// — forty-five seconds — and leaks the pool thread parked inside
// Security.framework, per ReadWithinAsync's own comment on why that thread never
// returns. Two abandoned UiTests hosts were found on a developer's machine at
// forty-three and eighty minutes, re-parented to launchd and still burning CPU.
//
// CI never saw it. A runner has no "Claude Code-credentials" item, so the query
// fails fast and every leg is green; the dialog only exists on a machine where
// somebody has logged in, which is to say on the machine of whoever is trying to
// work. That asymmetry is the whole reason this needs a test rather than a note:
// a regression here would be green everywhere it is measured and broken only
// where it is used.
//
// Deliberately asserts the *decision*, not the Keychain call. KeychainCredentialSource
// is [ExcludeFromCodeCoverage] and its body is Security.framework interop that
// cannot run on a runner — but which branch it takes is ours, and that is what is
// checked here.
public class CredentialStoreDisabledTests
{
    private const string Variable = "CLAUDE_BUDDY_NO_CREDENTIAL_STORE";

    // The bootstrap sets it for the whole assembly, so this is the state every
    // other test in this suite actually runs under. Asserting it here is what
    // turns "we set an environment variable somewhere" into a checked fact.
    [Fact]
    public void TheCredentialStoreIsDisabledForThisAssembly()
    {
        Assert.True(ClaudeCliCredentials.CredentialStoreDisabled);
    }

    [Fact]
    public void AnUnsetVariableLeavesTheStoreEnabled()
    {
        var was = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, null);
            Assert.False(ClaudeCliCredentials.CredentialStoreDisabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, was);
        }
    }

    // Empty is not set. Guards against a bootstrap that writes "" and a reader
    // that treats any non-null as on — which would disable the store in
    // production for anyone with the variable exported blank.
    [Fact]
    public void AnEmptyVariableLeavesTheStoreEnabled()
    {
        var was = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, "");
            Assert.False(ClaudeCliCredentials.CredentialStoreDisabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, was);
        }
    }

    // Any non-empty value, not just "1". A future bootstrap writing "true" or
    // "yes" should not silently stop protecting anything.
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("anything at all")]
    public void AnyNonEmptyValueDisablesTheStore(string value)
    {
        var was = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, value);
            Assert.True(ClaudeCliCredentials.CredentialStoreDisabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, was);
        }
    }

    // SourceFor still chooses the Keychain class on macOS. The seam deliberately
    // did not move up to here: which source a platform gets is a decision this
    // repository owns and ClaudeCloudCredentialPlatformTests asserts, and
    // constructing one queries nothing. Only Read() does, and only Read() is
    // guarded. This pins that the fix did not quietly change the platform choice.
    [Fact]
    public void DisablingTheStoreDoesNotChangeWhichSourceAPlatformGets()
    {
        Assert.IsType<KeychainCredentialSource>(
            ClaudeCliCredentials.SourceFor(isMacOS: true, home: "/tmp/does-not-matter"));
    }

    // The point of the whole change: with the store disabled, a read answers
    // immediately and says NotLoggedIn rather than reaching Security.framework.
    // NotLoggedIn rather than Denied or NoAnswer on purpose — those two carry
    // meaning about a user's choice and about a hang, and a test process has made
    // no choice and is not hanging. It is simply a process with no credential.
    [Fact]
    public void ADisabledReadAnswersImmediatelyAndSaysNotLoggedIn()
    {
        var read = new KeychainCredentialSource().Read();

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    // Stamp() is the attributes-only query and is not the one the consent prompt
    // guards, so it was never the hang — but it still touches the Keychain, and a
    // test process should not. Null is the same answer it gives for an item that
    // is not there, which every caller already handles as "nothing to compare".
    [Fact]
    public void ADisabledStampAnswersNullRatherThanQuerying()
    {
        Assert.Null(new KeychainCredentialSource().Stamp());
    }
}
