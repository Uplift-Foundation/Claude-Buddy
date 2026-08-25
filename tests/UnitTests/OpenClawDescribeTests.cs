using System;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawSessions.Describe: the sentence a user reads when the gateway will not
// connect.
//
// This is the entire diagnostic surface for a feature whose failures are all
// invisible — a connection that never came up looks exactly like a connection
// nobody asked for. Five outcomes, each needing to say something different
// enough to act on, and one of them needing to say what to actually type.
public class OpenClawDescribeTests
{
    private static string Describe(OpenClawGateway.Outcome outcome, string? detail = null) =>
        OpenClawSessions.Describe(new OpenClawGateway.ConnectResult(outcome, detail));

    // Pending pairing is the one outcome with a fix the user can run, so the
    // message carries the command rather than describing it. Asserted on the
    // command text: paraphrasing it here would be the same as not having it.
    [Fact]
    public void PairingPendingTellsTheUserTheCommandToRun()
    {
        var text = Describe(OpenClawGateway.Outcome.PairingPending);

        Assert.Contains("openclaw devices approve --latest", text);
        Assert.Contains("approved", text);
    }

    // A refusal is terminal — bad token, wrong identity, scope refused — so the
    // gateway's own reason is the only useful part and has to survive into the
    // message.
    [Fact]
    public void AuthRejectedCarriesTheGatewaysOwnReason()
    {
        var text = Describe(OpenClawGateway.Outcome.AuthRejected, "scope 'chat.write' refused");

        Assert.Contains("refused these credentials", text);
        Assert.Contains("scope 'chat.write' refused", text);
    }

    // A certificate mismatch must not read as a network problem: this is the
    // failure that means something is wrong rather than something is off, and a
    // user who reads it as "try again later" is the wrong outcome.
    [Fact]
    public void ACertificateMismatchSaysItIsADifferentCertificate()
    {
        var text = Describe(OpenClawGateway.Outcome.CertificateMismatch);

        Assert.Contains("certificate", text);
        Assert.Contains("trusts", text);
        Assert.DoesNotContain("can't reach", text);
    }

    [Fact]
    public void UnreachableCarriesTheTransportDetail()
    {
        var text = Describe(OpenClawGateway.Outcome.Unreachable, "connection refused");

        Assert.Contains("can't reach the gateway", text);
        Assert.Contains("connection refused", text);
    }

    // The fallback arm, which Connected also lands in. It exists so that a new
    // Outcome added to the enum produces something readable rather than an
    // exception — but a detail-less fallback still has to say something, hence
    // "not connected" rather than an empty string on screen.
    [Fact]
    public void AnOutcomeWithNoSpecificMessageFallsBackToItsDetail()
    {
        Assert.Equal("half-open", Describe(OpenClawGateway.Outcome.Connected, "half-open"));
    }

    [Fact]
    public void AFallbackWithNoDetailStillSaysSomething()
    {
        Assert.Equal("not connected", Describe(OpenClawGateway.Outcome.Connected));
    }

    // Every outcome has to produce non-empty text, whatever the enum grows to.
    // A [Theory] would read better but cannot be used here: Outcome is internal,
    // and an InlineData parameter of a less-accessible type than the public test
    // method it feeds does not compile. Enumerating the enum is also the stronger
    // form — it picks up a new Outcome without anyone remembering to add a row.
    [Fact]
    public void EveryOutcomeProducesSomethingToRead()
    {
        foreach (OpenClawGateway.Outcome outcome
                 in Enum.GetValues<OpenClawGateway.Outcome>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Describe(outcome)),
                $"{outcome} describes as blank");
        }
    }

    // A null detail on the two arms that concatenate one produces a dangling
    // "…: " with nothing after it. Recorded as the behaviour rather than as the
    // behaviour I would prefer: both arms only run when the gateway supplied a
    // reason, so this is not reachable today — but it is exactly the sort of
    // thing that becomes reachable the moment a new caller appears, and a test
    // that says so is how that gets noticed.
    [Fact]
    public void AConcatenatingArmWithNoDetailEndsInASeparator()
    {
        Assert.EndsWith(": ", Describe(OpenClawGateway.Outcome.AuthRejected));
        Assert.EndsWith(": ", Describe(OpenClawGateway.Outcome.Unreachable));
    }
}
