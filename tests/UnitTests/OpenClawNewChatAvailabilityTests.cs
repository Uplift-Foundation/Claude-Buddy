using Xunit;

namespace ClaudeBuddy.Tests;

// OpenClawNewChat.AvailabilityFor/ReasonFor: whether the new-chat dialog's
// OpenClaw radio item is a live choice, and what it says under itself when
// it isn't. Pure and settings-free, so every branch is reachable with no
// gateway and no ClaudeBuddySettings singleton in the picture — see the
// header comment on OpenClawNewChatAvailability.cs for why that separation
// matters here specifically.
public class OpenClawNewChatAvailabilityTests
{
    [Fact]
    public void OffEntirelyIsNoGateway()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.NoGateway,
            OpenClawNewChat.AvailabilityFor(enabled: false, host: "avatar.internal", replyEnabled: true));
    }

    [Fact]
    public void EnabledButNoHostIsNoGateway()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.NoGateway,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "", replyEnabled: true));
    }

    // ClaudeBuddySettings.OpenClawHost reads back "" rather than null when
    // unset (ClaudeBuddySettings.cs:886) — this is the shape AvailabilityFor
    // actually receives, not a null, so it's exercised directly rather than
    // trusting IsNullOrWhiteSpace to cover both the same way.
    [Fact]
    public void EnabledButWhitespaceHostIsNoGateway()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.NoGateway,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "   ", replyEnabled: true));
    }

    [Fact]
    public void EnabledWithHostButReplyOffIsReplyDisabled()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.ReplyDisabled,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "avatar.internal", replyEnabled: false));
    }

    [Fact]
    public void EnabledWithHostAndReplyOnIsReady()
    {
        Assert.Equal(
            OpenClawNewChatAvailability.Ready,
            OpenClawNewChat.AvailabilityFor(enabled: true, host: "avatar.internal", replyEnabled: true));
    }

    [Fact]
    public void ReadyHasNoReason()
    {
        Assert.Null(OpenClawNewChat.ReasonFor(OpenClawNewChatAvailability.Ready));
    }

    [Fact]
    public void NoGatewayNamesItself()
    {
        Assert.Equal("no gateway configured", OpenClawNewChat.ReasonFor(OpenClawNewChatAvailability.NoGateway));
    }

    [Fact]
    public void ReplyDisabledPointsAtTheSetting()
    {
        var reason = OpenClawNewChat.ReasonFor(OpenClawNewChatAvailability.ReplyDisabled);
        Assert.Contains("Allow replying to agents", reason);
    }
}
