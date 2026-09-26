using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawSessions.StartConversationAsync: CB-168's OpenClaw "start a new
// chat" entry point. The method itself is [ExcludeFromCodeCoverage] — it
// creates a session on a real gateway, the same treatment SendAsync already
// gets — but its early "not connected" return needs no network and no
// gateway to reach, the same way OpenClawChatSendTests reaches SendAsync's
// equivalent branch through the "Couldn't send:" note.
//
// [Collection("Settings")]: OpenClawSessions' _gateway field is a
// process-wide static, and this asserts a specific value of it (null) —
// the same reason OpenClawLiveImageResolutionTests,
// OpenClawCronRunRecoveryEndToEndTests and OpenClawRoomSendTests are all in
// this collection, since each sets it through SetGatewayForTests. Without
// this, xUnit is free to run this class in parallel with any of those,
// and a gateway set there is visible here too — this class asserting
// "not connected to the gateway" would then depend on which test from an
// unrelated class happened to be mid-run, rather than being reliably null
// the way the header used to claim before this fix.
[Collection("Settings")]
public class OpenClawStartConversationTests
{
    [Fact]
    public async Task WithNoLiveConnectionItReportsNotConnectedRatherThanThrowing()
    {
        var (session, failure) =
            await OpenClawSessions.StartConversationAsync("main", CancellationToken.None);

        Assert.Null(session);
        Assert.Equal("not connected to the gateway", failure);
    }
}

// OpenClawSessions.ParseCreateResult: what StartConversationAsync makes of
// the gateway's own response to sessions.create, pulled out so a malformed
// reply is a decision this can drive directly with a hand-built JsonElement
// rather than something only reachable behind a real socket (QA, CB-168,
// rowan-achterberg).
public class OpenClawParseCreateResultTests
{
    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ASuccessfulResponseReturnsTheKeyAndNoFailure()
    {
        var (key, failure) = OpenClawSessions.ParseCreateResult(
            ParseJson("""{"ok":true,"key":"agent:main:dashboard:abc-123"}"""));

        Assert.Equal("agent:main:dashboard:abc-123", key);
        Assert.Null(failure);
    }

    [Fact]
    public void AMissingKeyPropertyIsAFailure()
    {
        var (key, failure) = OpenClawSessions.ParseCreateResult(ParseJson("""{"ok":true}"""));

        Assert.Null(key);
        Assert.Equal("gateway didn't return a session key", failure);
    }

    // A malformed reply — the gateway's own contract says `key` is a
    // string, but nothing stops a future protocol version, a bug on the
    // gateway side, or a hand-typed `raw` probe call from handing this a
    // number or an object instead.
    [Fact]
    public void AKeyOfTheWrongJsonTypeIsAFailure()
    {
        var (key, failure) = OpenClawSessions.ParseCreateResult(
            ParseJson("""{"ok":true,"key":12345}"""));

        Assert.Null(key);
        Assert.Equal("gateway didn't return a session key", failure);
    }

    [Fact]
    public void AnEmptyStringKeyIsAFailure()
    {
        var (key, failure) = OpenClawSessions.ParseCreateResult(
            ParseJson("""{"ok":true,"key":""}"""));

        Assert.Null(key);
        Assert.Equal("gateway returned an empty session key", failure);
    }

    [Fact]
    public void AWhitespaceOnlyKeyIsAFailure()
    {
        var (key, failure) = OpenClawSessions.ParseCreateResult(
            ParseJson("""{"ok":true,"key":"   "}"""));

        Assert.Null(key);
        Assert.Equal("gateway returned an empty session key", failure);
    }
}
