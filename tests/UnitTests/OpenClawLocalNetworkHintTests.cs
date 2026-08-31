using System;
using System.IO;
using System.Net.Sockets;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawGateway.ExplainConnectFailure and IsHostUnreachable: turning
// EHOSTUNREACH into a sentence that points at the actual cause.
//
// CB-38. Upgrading the app replaces the bundle, macOS re-evaluates Local Network
// consent against the new code identity, and the grant quietly does not carry
// over. What the app then sees is "No route to host" for a gateway that is
// plainly up — and because ping, nc, curl and ssh are all Apple-signed and
// exempt from that gate, every obvious check tells the user the network is fine.
// The failure is invisible in exactly the way the Automation-consent one is.
//
// The bug being fixed is a *message*, so the message is what these assert: that
// the hint appears when it should, that it does not appear when it shouldn't,
// and that the platform's own words survive either way.
public class OpenClawLocalNetworkHintTests
{
    private static SocketException Unreachable() =>
        new((int)SocketError.HostUnreachable);

    private static SocketException Refused() =>
        new((int)SocketError.ConnectionRefused);

    // The plain case: TcpClient.ConnectAsync throws SocketException straight up,
    // which is what actually happens today.
    [Fact]
    public void AHostUnreachableSocketErrorIsRecognised()
    {
        Assert.True(OpenClawGateway.IsHostUnreachable(Unreachable()));
    }

    // Nothing wraps the connect today, but something will. Asserted so the
    // recognition survives the first layer that gets added over it rather than
    // silently reverting to the bare errno.
    [Fact]
    public void AWrappedHostUnreachableIsStillRecognised()
    {
        var wrapped = new IOException("connect failed", Unreachable());

        Assert.True(OpenClawGateway.IsHostUnreachable(wrapped));
    }

    // An AggregateException keeps its causes in InnerExceptions, beside
    // InnerException rather than under it, so a plain walk down the chain steps
    // past them. Dual-stack connects are the realistic way one shows up here.
    [Fact]
    public void AHostUnreachableInsideAnAggregateIsRecognised()
    {
        var agg = new AggregateException(
            new IOException("v6 leg"),
            Unreachable());

        Assert.True(OpenClawGateway.IsHostUnreachable(agg));
    }

    // The other side of the aggregate arm. Searching InnerExceptions has to be
    // able to come back empty-handed, or an aggregate would be treated as proof
    // of a consent problem merely by being an aggregate — a dual-stack connect
    // that failed for two unrelated reasons is the obvious way to hit it.
    [Fact]
    public void AnAggregateWithNothingRelevantInsideIsNotAHostUnreachable()
    {
        var agg = new AggregateException(
            new IOException("v6 leg"),
            new TimeoutException("v4 leg"));

        Assert.False(OpenClawGateway.IsHostUnreachable(agg));
    }

    // An aggregate is not the end of the walk: the loop still has to step past
    // one and keep going down InnerException. Nested because that is the shape
    // Task machinery actually produces when a retry wraps a wrapped failure.
    [Fact]
    public void TheWalkContinuesPastAnAggregateThatDoesNotMatch()
    {
        var chain = new InvalidOperationException(
            "outer",
            new AggregateException(new TimeoutException("unrelated")));

        Assert.False(OpenClawGateway.IsHostUnreachable(chain));
    }

    // The negative half, and the one that matters most: a refused port is the
    // ordinary "gateway isn't running" case, and blaming macOS consent for it
    // would send people to a settings pane that has nothing wrong in it.
    [Fact]
    public void ARefusedConnectionIsNotAHostUnreachable()
    {
        Assert.False(OpenClawGateway.IsHostUnreachable(Refused()));
    }

    [Fact]
    public void AnExceptionWithNoSocketErrorAnywhereIsNotAHostUnreachable()
    {
        var chain = new InvalidOperationException(
            "outer", new TimeoutException("inner"));

        Assert.False(OpenClawGateway.IsHostUnreachable(chain));
    }

    // The hint is appended, never substituted. EHOSTUNREACH genuinely can mean
    // an unplugged cable or a host that has gone away, so dropping the
    // platform's own words would trade a confusing message for a misleading one.
    [Fact]
    public void OnMacOSAHostUnreachableGainsTheLocalNetworkHint()
    {
        var ex = Unreachable();

        var text = OpenClawGateway.ExplainConnectFailure(ex, onMacOS: true);

        Assert.Contains(OpenClawGateway.LocalNetworkHint, text);
        Assert.Contains(OpenClawGateway.Flatten(ex), text);
    }

    // The hint names a System Settings pane that exists only on macOS, so
    // showing it on Windows would be an instruction the user cannot follow. CI
    // runs this suite on both runners; passing the platform in as an argument is
    // what lets the Windows arm be asserted from a Mac and the macOS arm from a
    // Windows runner, rather than each leg silently skipping half the rule.
    [Fact]
    public void OffMacOSTheSameFailureGetsNoHint()
    {
        var ex = Unreachable();

        var text = OpenClawGateway.ExplainConnectFailure(ex, onMacOS: false);

        Assert.DoesNotContain("Local Network", text);
        Assert.Equal(OpenClawGateway.Flatten(ex), text);
    }

    [Fact]
    public void OnMacOSAnUnrelatedFailureGetsNoHint()
    {
        var ex = Refused();

        var text = OpenClawGateway.ExplainConnectFailure(ex, onMacOS: true);

        Assert.DoesNotContain("Local Network", text);
        Assert.Equal(OpenClawGateway.Flatten(ex), text);
    }

    // The hint has to survive the trip to the screen, not just out of the
    // helper. Describe is what the settings window actually renders, and the
    // Unreachable arm concatenates — so this pins the whole sentence a user
    // reads, which is the thing CB-38 is actually about.
    [Fact]
    public void TheHintReachesTheSentenceTheUserReads()
    {
        var detail = OpenClawGateway.ExplainConnectFailure(Unreachable(), onMacOS: true);

        var text = OpenClawSessions.Describe(
            new OpenClawGateway.ConnectResult(
                OpenClawGateway.Outcome.Unreachable, detail));

        Assert.Contains("can't reach the gateway", text);
        Assert.Contains("Privacy & Security", text);
    }
}
