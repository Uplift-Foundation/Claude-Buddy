using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// OpenClawSessions.StartConversationAsync: CB-168's OpenClaw "start a new
// chat" entry point. The method itself is [ExcludeFromCodeCoverage] — it
// creates a session on a real gateway, the same treatment SendAsync already
// gets — but its early "not connected" return needs no network and no
// gateway to reach, the same way OpenClawChatSendTests reaches SendAsync's
// equivalent branch through the "Couldn't send:" note. No test process here
// ever starts OpenClawSessions' connect loop, so its gateway field is
// reliably null.
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
