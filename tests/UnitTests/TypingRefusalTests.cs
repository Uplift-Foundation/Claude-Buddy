using System;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// What a live view says when a keystroke was refused on the other machine, and
// when a message this side sent stops waiting to be matched.
//
// Both were switches buried inside a send that needs a live relay. Pulled out
// because each arm is about a different machine's state and only the person
// reading it can act on any of them — three are things to change *over there*,
// and getting one wrong is a dead end rather than a wrong pixel.
public class TypingRefusalTests
{
    private const string Remote = "job-hunter";

    // Its own setting, on its own machine, and the note has to say so — the
    // switch in this window's Settings has no effect on it, and someone who
    // flips that one and tries again learns nothing.
    [Fact]
    public void ReplyingOffOnTheFarMachineSaysWhereTheSettingIs()
    {
        var said = RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrReplyOff, Remote);

        Assert.Contains(Remote, said);
        Assert.Contains("turned on over there", said);
    }

    // The far session exists but has no pane to type into. Distinct from the
    // one below, which is the session being gone entirely — the same
    // distinction LocalCliChatSession draws for a local session, and for the
    // same reason: one is waiting for you in a terminal, the other is not there.
    [Fact]
    public void NoPaneOnTheFarMachineSaysThereIsNowhereToType()
    {
        var said = RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrNoPane, Remote);

        Assert.Contains("tmux pane", said);
        Assert.Contains(Remote, said);
    }

    [Fact]
    public void ASessionTheFarBuddyNoLongerHasIsNamed()
    {
        var said = RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrNoSession, Remote);

        Assert.Contains("no longer has a session", said);
        Assert.Contains(Remote, said);
    }

    // Refused rather than typed in a form you did not write — which is the
    // whole point of hashing the input, and the note says so because "it
    // failed" would leave someone wondering whether half of it went through.
    [Fact]
    public void AMessageThatDidNotSurviveTheTripSaysItWasRefusedRatherThanMangled()
    {
        var said = RemoteControlChatSession.TypingRefusal(MirrorProtocol.ErrBadHash, Remote);

        Assert.Contains("refused rather than typed", said);
        Assert.Contains("Try sending it again", said);
    }

    // The arm that runs when the far machine is newer than this one, and the
    // only one nothing else can produce. A blank or a bare code on screen would
    // be the worst of the five, so it at least names the session it was about.
    [Theory]
    [InlineData("err-from-a-later-version")]
    [InlineData("")]
    [InlineData(null)]
    public void ACodeThisVersionDoesNotKnowStillNamesTheSession(string? code)
    {
        var said = RemoteControlChatSession.TypingRefusal(code, Remote);

        Assert.Equal($"Couldn't type that into {Remote}.", said);
    }

    // Every one of them says something, which is the property that actually
    // matters: this text is the only thing on screen after a message did not go.
    [Theory]
    [InlineData(MirrorProtocol.ErrReplyOff)]
    [InlineData(MirrorProtocol.ErrNoPane)]
    [InlineData(MirrorProtocol.ErrNoSession)]
    [InlineData(MirrorProtocol.ErrBadHash)]
    [InlineData("anything else")]
    public void NoRefusalIsSilent(string code) =>
        Assert.NotEmpty(RemoteControlChatSession.TypingRefusal(code, Remote));

    // ---- a sent message that is still waiting to be matched ----------------

    // The mirrored transcript will produce the message just sent, because it
    // went through the terminal — so the row that comes back adopts the turn
    // already on screen rather than adding a second one.
    [Fact]
    public void AMessageSentAMomentAgoIsStillWaitingToBeMatched()
    {
        var now = DateTimeOffset.Now;

        Assert.False(RemoteControlChatSession.PendingHasGoneStale(now, now));
        Assert.False(RemoteControlChatSession.PendingHasGoneStale(
            now, now + TimeSpan.FromSeconds(90)));
    }

    // The bound is the point. An identical message sent twice an hour apart must
    // not have the second swallowed by a pending turn from the first that never
    // arrived — matching on text alone would do exactly that.
    [Fact]
    public void AMessageThatNeverCameBackStopsWaitingAfterTwoMinutes()
    {
        var now = DateTimeOffset.Now;

        Assert.True(RemoteControlChatSession.PendingHasGoneStale(
            now, now + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1)));
        Assert.True(RemoteControlChatSession.PendingHasGoneStale(
            now, now + TimeSpan.FromHours(1)));
    }

    // Exactly at the boundary is not stale: the comparison is strictly greater,
    // so a reply that takes precisely two minutes is still matched.
    [Fact]
    public void ExactlyTwoMinutesIsStillWaiting()
    {
        var now = DateTimeOffset.Now;

        Assert.False(RemoteControlChatSession.PendingHasGoneStale(
            now, now + TimeSpan.FromMinutes(2)));
    }
}
