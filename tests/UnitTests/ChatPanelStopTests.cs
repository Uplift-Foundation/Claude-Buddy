using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// ChatPanel.StopOffered, the one rule for whether Stop is shown and whether a
// press on it reaches Cancel (CB-199). Pure, so every arm is a case here rather
// than a panel to build — including shapes no transport produces today, which
// is exactly where a rule written for one transport breaks for the next.
public class ChatPanelStopTests
{
    private class Plain : IRemoteChatSession
    {
        public string SessionId => "s";
        public string DisplayName => "s";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History => Array.Empty<ChatTurn>();

        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }

        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Failed);
        public void Cancel() { }
    }

    private class Interruptible : Plain, IRemoteChatInterrupt
    {
        public bool CanInterrupt { get; init; }
        public event Action? InterruptChanged { add { } remove { } }
    }

    private sealed class InterruptibleReadOnly : Interruptible, IRemoteChatReadOnly
    {
        public bool IsReadOnly { get; init; }
        public string? ReplyUrl { get; init; }
        public event Action? ReadOnlyChanged { add { } remove { } }
    }

    [Fact]
    public void NothingBoundOffersNoStop() => Assert.False(ChatPanel.StopOffered(null));

    [Fact]
    public void ATransportWithoutInterruptOffersNoStop() => Assert.False(ChatPanel.StopOffered(new Plain()));

    [Fact]
    public void AnIdleSessionOffersNoStop() =>
        Assert.False(ChatPanel.StopOffered(new Interruptible { CanInterrupt = false }));

    // No read-only answer at all is not a read-only session.
    [Fact]
    public void AnInterruptibleSessionWithNoReadOnlyAnswerOffersStop() =>
        Assert.True(ChatPanel.StopOffered(new Interruptible { CanInterrupt = true }));

    [Fact]
    public void AWritableInterruptibleSessionOffersStop() =>
        Assert.True(ChatPanel.StopOffered(new InterruptibleReadOnly { CanInterrupt = true, IsReadOnly = false }));

    // A session that claims both is read-only for the panel's purposes: nothing
    // is stopped over a session that no longer takes input.
    [Fact]
    public void AReadOnlySessionOffersNoStopWhateverItSays() =>
        Assert.False(ChatPanel.StopOffered(new InterruptibleReadOnly { CanInterrupt = true, IsReadOnly = true }));

    // ChatPanel.SessionLinkUrl, the rule for the live-session link (CB-199
    // follow-up). One case per arm.
    [Fact]
    public void NothingBoundHasNoSessionLink() => Assert.Null(ChatPanel.SessionLinkUrl(null));

    [Fact]
    public void ATransportWithoutAReadOnlyAnswerHasNoSessionLink() =>
        Assert.Null(ChatPanel.SessionLinkUrl(new Plain()));

    [Fact]
    public void ALiveSessionWithAnAddressOffersIt() =>
        Assert.Equal("https://claude.ai/code/s",
            ChatPanel.SessionLinkUrl(new InterruptibleReadOnly { ReplyUrl = "https://claude.ai/code/s" }));

    [Fact]
    public void ALiveSessionWithoutAnAddressOffersNone() =>
        Assert.Null(ChatPanel.SessionLinkUrl(new InterruptibleReadOnly { ReplyUrl = null }));

    [Fact]
    public void AnEmptyAddressOffersNone() =>
        Assert.Null(ChatPanel.SessionLinkUrl(new InterruptibleReadOnly { ReplyUrl = "" }));

    // A read-only session gets its link from the read-only box, so this one
    // stays out of the way rather than making two.
    [Fact]
    public void AReadOnlySessionLeavesTheLinkToTheReadOnlyBox() =>
        Assert.Null(ChatPanel.SessionLinkUrl(
            new InterruptibleReadOnly { IsReadOnly = true, ReplyUrl = "https://claude.ai/code/s" }));
}
