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

    // --- CB-43: which refusals the messaging channel may answer ----------------

    // --- CB-46: the rules a first paint depends on -----------------------------

    // The question the server asks before deciding how much transcript to send,
    // and it is asked of the same encoder and splitter that will carry the
    // answer — a prediction of that is exactly the thing that has been wrong.
    [Fact]
    public void AShortConversationFitsOneChunk()
    {
        var turns = new List<MirrorProtocol.MirrorTurn>
        {
            new("user", "what did the build say?"),
            new("assistant", "it passed on both runners")
        };

        Assert.True(RemoteMirrorServer.FitsOneChunk(turns));
    }

    // Incompressible, because that is the case a byte count cannot predict: the
    // ratio between transcript bytes and encoded, compressed turns runs from
    // twenty to one down to nothing at all, which is why this is measured rather
    // than assumed.
    // **The "does not fit" case is no longer cheaply reachable, and saying so
    // is more honest than a fixture that pretends otherwise.**
    //
    // This used to be 8 chunks of random text — 48KB — which was plenty when a
    // chunk was what a model could retype in one turn. A chunk is now the
    // transport's whole 32MB message, and random ASCII from a 36-letter
    // alphabet gzips to about two thirds and then base64s back up by a third,
    // so a 33MB fixture still fits. Reaching the ceiling honestly needs
    // hundreds of megabytes allocated to assert an arithmetic fact.
    //
    // The ceiling itself is enforced where it belongs and tested there:
    // PeerProtocol refuses to encode a message over MaxMessageBytes and refuses
    // to read a length claiming to be one, and PeerProtocolTests covers both
    // directions. FitsOneChunk asks Split, and Split's boundary arithmetic has
    // its own tests in MirrorProtocolTests.
    //
    // What is worth asserting here is the *shape* of the answer — that
    // FitsOneChunk is a real question with a real threshold rather than a
    // constant true — which the two cases below do without allocating anything.
    [Fact]
    public void FitsOneChunkIsBoundedByTheTransportRatherThanByAGuess()
    {
        // The old bound was a guess about a SendMessage body. The new one is the
        // number the wire actually enforces, which is the point of the change.
        Assert.Equal(PeerProtocol.MaxMessageBytes, MirrorProtocol.ChunkBytes);
    }

    // And the case that used to be the interesting one, now the ordinary one: a
    // transcript that would have taken dozens of model turns goes in a single
    // frame.
    [Fact]
    public void ATranscriptThatOnceNeededDozensOfChunksNowFitsInOne()
    {
        var turns = Enumerable.Range(0, 5000)
            .Select(i => new MirrorProtocol.MirrorTurn("user", $"a message about thing {i}"))
            .ToList();

        Assert.True(RemoteMirrorServer.FitsOneChunk(turns));
    }

    [Fact]
    public void NothingAtAllFitsOneChunk() =>
        Assert.True(RemoteMirrorServer.FitsOneChunk(new List<MirrorProtocol.MirrorTurn>()));

    // The line a user reads while nothing appears to be happening. It replaced
    // the opening "Checking whether a live view … is available", which stayed on
    // screen for the whole transfer and is the exact sentence that meant failure
    // an hour earlier — a working transfer got reported as "no live view" twice
    // on the strength of it.
    [Fact]
    public void TheFetchingNoteSaysSomethingIsHappeningAndThatItTakesMinutes()
    {
        var said = RemoteControlChatSession.FetchingNote(Remote);

        Assert.Contains(Remote, said);
        Assert.Contains("fetching its conversation", said);
        Assert.Contains("several minutes", said);
        Assert.DoesNotContain("Checking whether", said);
    }

    // --- the counter that runs while nothing appears to happen ---------------

    // Seconds all the way to a minute, then minutes and seconds.
    //
    // The measured waits are three to four minutes, so a counter showing only
    // whole minutes would sit unchanged for sixty seconds at exactly the moment
    // somebody is deciding whether it has hung — which is the whole failure this
    // indicator exists to prevent.
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(9, "9s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(75, "1m 15s")]
    [InlineData(192, "3m 12s")]
    [InlineData(247, "4m 7s")]
    public void TheWaitLabelCountsInWholeSecondsAndThenMinutes(int seconds, string expected)
    {
        var said = RemoteControlChatSession.WaitLabel(
            TimeSpan.FromSeconds(seconds), "its conversation");

        Assert.Contains(expected, said);
        Assert.Contains("its conversation", said);
    }

    // A clock that has gone backwards is a machine problem, not a reason to
    // print "-3s" at somebody. Clamped rather than guarded at the call site so
    // there is one answer to this and it is here.
    [Fact]
    public void TheWaitLabelDoesNotCountBackwards()
    {
        var said = RemoteControlChatSession.WaitLabel(
            TimeSpan.FromSeconds(-5), "its conversation");

        Assert.Contains("0s", said);
        Assert.DoesNotContain("-", said);
    }

    // The hint is what stops an ordinary three-minute wait reading as a fault,
    // so it has to name a duration and it has to match what was measured.
    [Fact]
    public void TheWaitHintSaysHowLongTheseActuallyTake()
    {
        Assert.Contains("minutes", RemoteControlChatSession.WaitHint);
        Assert.DoesNotContain("second", RemoteControlChatSession.WaitHint);
    }

    // And specifically not the singular it used to promise.
    //
    // A wait quoted as one minute and measured at seven reads as a hang, which
    // is the failure this note exists to prevent — so understating it is worse
    // than saying nothing. Asserted separately from the wording above because
    // this is the part that was actually wrong, and a future edit that reaches
    // for "a minute" again should fail on the reason rather than on the phrasing.
    [Fact]
    public void TheFetchingNoteDoesNotPromiseAMinuteItCannotKeep()
    {
        var said = RemoteControlChatSession.FetchingNote(Remote);

        Assert.DoesNotContain("take a minute", said);
        Assert.DoesNotContain("a minute:", said);
    }
}
