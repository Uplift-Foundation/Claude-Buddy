using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// CB-92: conversations nobody is looking at give their memory back.
//
// The unit suite next door covers the policy — which chats a sweep picks, given
// a description of what is resident. This is the other half, and it is the half
// that was actually broken: whether the app ever *asks*, whether the answer
// reaches the dictionary that was holding everything forever, and whether the
// bytes are genuinely gone afterwards rather than merely unlisted.
//
// In the UI suite because half of it goes through a real ChatPanel binding and
// unbinding, which is the seam that says a conversation is on screen. The rest
// could live in the unit suite and is here instead so the whole story reads in
// one file — the panel cases and the sweep cases are the same claim checked at
// two distances.
//
// Keys are per-test Guids. OpenClawSessions.Chats is process-wide, and a test
// that evicted a fixed key would be quietly deciding what a test running beside
// it sees.
[Collection("Settings")]
public class OpenClawChatResidencyTests
{
    private static OpenClawChatSession Fresh(out string key)
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = true;

        key = "agent:cb92-" + Guid.NewGuid().ToString("N") + ":main";

        var chat = OpenClawSessions.ChatFor("openclaw:" + key, "Nova") as OpenClawChatSession;
        Assert.NotNull(chat);

        return chat!;
    }

    // A picture-carrying transcript, built the way the history parser builds
    // one: inline bytes, no url, which is the shape CB-91 started decoding and
    // this ticket started releasing.
    private static void GiveItPictures(OpenClawChatSession chat, int count, int size)
    {
        var turns = new List<HistoryTurn>();
        for (var i = 0; i < count; i++)
        {
            turns.Add(new HistoryTurn(
                ChatRole.Assistant, "here you go", null, "shot.png",
                DateTimeOffset.UtcNow, null, null, ImageBytes: new byte[size]));
        }

        chat.SetHistory(turns);
    }

    private static bool Resident(string key) =>
        OpenClawSessions.OpenChats().Any(c => c.GatewayKey == key);

    // ---- what a conversation says about itself -----------------------------

    [AvaloniaFact]
    public void ANewConversationHasNoPanelOnIt()
    {
        var chat = Fresh(out _);

        // Created by ChatFor, which is also what the orb's speak button and a
        // room's member merge use. None of those is a panel, and treating
        // "never had one" as "still in use" is what let those accumulate.
        Assert.False(chat.HasOpenPanel);
    }

    [AvaloniaFact]
    public void PanelsAreCountedRatherThanFlagged()
    {
        var chat = Fresh(out _);

        chat.PanelOpened();
        chat.PanelOpened();
        chat.PanelClosed();

        // Two panels on one conversation is momentary — a pinned panel and the
        // transient during a rebind — and a flag would have the first close
        // claim nobody is looking.
        Assert.True(chat.HasOpenPanel);

        chat.PanelClosed();
        Assert.False(chat.HasOpenPanel);
    }

    [AvaloniaFact]
    public void ClosingMorePanelsThanWereOpenedIsHarmless()
    {
        var chat = Fresh(out _);

        chat.PanelClosed();

        Assert.False(chat.HasOpenPanel);
    }

    [AvaloniaFact]
    public void TheIdleClockStartsWhenTheLastPanelCloses()
    {
        var chat = Fresh(out _);
        var atCreation = chat.IdleSince;

        chat.PanelOpened();

        // Still the creation stamp: the clock is not running while somebody is
        // looking.
        Assert.Equal(atCreation, chat.IdleSince);

        chat.PanelClosed();

        Assert.True(chat.IdleSince >= atCreation);
    }

    [AvaloniaFact]
    public void AConversationWeighsWhatItsPicturesWeigh()
    {
        var chat = Fresh(out _);
        GiveItPictures(chat, count: 3, size: 1000);

        Assert.Equal(3000, chat.ResidentImageBytes);
    }

    [AvaloniaFact]
    public void ReleasingKeepsTheWordsAndReportsWhatItFreed()
    {
        var chat = Fresh(out _);
        GiveItPictures(chat, count: 2, size: 500);

        var freed = chat.ReleaseImages();

        Assert.Equal(1000, freed);
        Assert.Equal(0, chat.ResidentImageBytes);

        // The transcript itself survives — this is a cache being dropped, not
        // the conversation being cut short. The gateway re-sends the pictures
        // with the history when it is next opened.
        Assert.Equal(2, chat.History.Count);
        Assert.All(chat.History, t => Assert.Equal("here you go", t.Text));
    }

    [AvaloniaFact]
    public void ReleasingTwiceFreesNothingTheSecondTime()
    {
        var chat = Fresh(out _);
        GiveItPictures(chat, count: 1, size: 64);

        Assert.Equal(64, chat.ReleaseImages());
        Assert.Equal(0, chat.ReleaseImages());
    }

    // The claim this whole ticket rests on, checked the only way that is not a
    // restatement of the code: the array becomes unreachable.
    //
    // Counting bytes proves the field was set to null. It does not prove the
    // picture is *gone*, and "we set a reference to null" is exactly the kind of
    // claim that inherits confidence from adjacent evidence. So the array is
    // allocated in a frame that has returned before the collection runs — a
    // local in this method would be kept alive to the end of it in a Debug
    // build, and the test would pass or fail on build configuration rather than
    // on behaviour.
    [AvaloniaFact]
    public void AReleasedPictureBecomesUnreachable()
    {
        var chat = Fresh(out _);
        var picture = AttachOnePicture(chat);

        Assert.True(picture.IsAlive);

        chat.ReleaseImages();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(picture.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachOnePicture(OpenClawChatSession chat)
    {
        var bytes = new byte[64 * 1024];
        GiveItPictures(chat, count: 0, size: 0);

        chat.SetHistory(new List<HistoryTurn>
        {
            new(ChatRole.Assistant, "a screenshot", null, "shot.png",
                DateTimeOffset.UtcNow, null, null, ImageBytes: bytes)
        });

        return new WeakReference(bytes);
    }

    // ---- the sweep ---------------------------------------------------------

    [AvaloniaFact]
    public void AConversationIdlePastTheGraceStopsBeingResident()
    {
        var chat = Fresh(out var key);
        GiveItPictures(chat, count: 4, size: 2048);

        Assert.True(Resident(key));

        var swept = OpenClawSessions.SweepChats(
            DateTime.UtcNow, null, OpenClawChatMemory.DefaultBudgetBytes, TimeSpan.Zero);

        Assert.Contains(key, swept.Evicted);

        // The dictionary entry is gone — which, before this, was something no
        // line in the codebase could do — and the pictures went with it rather
        // than living on behind whatever else holds the session.
        Assert.False(Resident(key));
        Assert.Equal(0, chat.ResidentImageBytes);
        Assert.True(swept.FreedBytes >= 4 * 2048);
    }

    [AvaloniaFact]
    public void AConversationWithAPanelOnItIsLeftAlone()
    {
        var chat = Fresh(out var key);
        GiveItPictures(chat, count: 2, size: 4096);
        chat.PanelOpened();

        // Zero grace and zero budget: everything a sweep could do, it would do
        // here, and it still must not touch the one on screen.
        var swept = OpenClawSessions.SweepChats(DateTime.UtcNow, null, 0, TimeSpan.Zero);

        Assert.DoesNotContain(key, swept.Evicted);
        Assert.DoesNotContain(key, swept.Released);
        Assert.True(Resident(key));
        Assert.Equal(8192, chat.ResidentImageBytes);

        chat.PanelClosed();
    }

    [AvaloniaFact]
    public void TheConversationBeingOpenedIsHeldOutOfItsOwnSweep()
    {
        var chat = Fresh(out var key);

        var swept = OpenClawSessions.SweepChats(DateTime.UtcNow, key, 0, TimeSpan.Zero);

        Assert.DoesNotContain(key, swept.Evicted);
        Assert.True(Resident(key));
    }

    [AvaloniaFact]
    public void OverTheBudgetTheStillRecentOnesGiveUpTheirPictures()
    {
        var chat = Fresh(out var key);
        GiveItPictures(chat, count: 2, size: 4096);

        // A grace nothing can be past, so eviction is off the table and the
        // byte bound is the only thing that can act.
        var swept = OpenClawSessions.SweepChats(
            DateTime.UtcNow, null, budgetBytes: 0, grace: TimeSpan.FromDays(1));

        Assert.Contains(key, swept.Released);
        Assert.DoesNotContain(key, swept.Evicted);

        // Still there to reopen instantly — just without the pictures, which
        // the gateway re-sends anyway.
        Assert.True(Resident(key));
        Assert.Equal(0, chat.ResidentImageBytes);
    }

    [AvaloniaFact]
    public void ASweepWithNothingToDoReportsNothing()
    {
        var chat = Fresh(out var key);
        chat.PanelOpened();

        var swept = OpenClawSessions.SweepChats(
            DateTime.UtcNow, null, OpenClawChatMemory.DefaultBudgetBytes, TimeSpan.FromDays(1));

        Assert.DoesNotContain(key, swept.Evicted);
        Assert.DoesNotContain(key, swept.Released);

        chat.PanelClosed();
    }

    // Opening any conversation is one of the two moments the app sweeps, and
    // the default bounds are what it sweeps with — so a chat idle for seconds
    // survives somebody else's panel opening.
    [AvaloniaFact]
    public void OpeningAnotherConversationDoesNotEvictOneClosedAMomentAgo()
    {
        var chat = Fresh(out var key);
        GiveItPictures(chat, count: 1, size: 1024);

        Fresh(out _);

        Assert.True(Resident(key));
        Assert.Equal(1024, chat.ResidentImageBytes);
    }

    // ---- the panel saying so -----------------------------------------------

    [AvaloniaFact]
    public void BindingAPanelMarksTheConversationAsBeingLookedAt()
    {
        var chat = Fresh(out var key);
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        ChatPanel.OpenFor(orb, chat);
        Flush();

        Assert.True(chat.HasOpenPanel);

        // And the sweep cannot take it while it is up, whatever the bounds say.
        var swept = OpenClawSessions.SweepChats(DateTime.UtcNow, null, 0, TimeSpan.Zero);
        Assert.DoesNotContain(key, swept.Evicted);

        ChatPanel.CloseFor(chat.SessionId);
        Flush();

        Assert.False(chat.HasOpenPanel);

        // Now it can go — which is the whole fix: before this, closing the
        // panel left the transcript and every decoded picture in it resident
        // for the life of the process.
        var after = OpenClawSessions.SweepChats(DateTime.UtcNow, null, 0, TimeSpan.Zero);
        Assert.Contains(key, after.Evicted);
        Assert.False(Resident(key));
    }

    [AvaloniaFact]
    public void ReBindingAPanelHandsTheResidencyOver()
    {
        var first = Fresh(out _);
        var second = Fresh(out _);
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        ChatPanel.OpenFor(orb, first);
        Flush();
        ChatPanel.OpenFor(orb, second);
        Flush();

        // The transient panel is reused rather than a second one opened, so
        // the conversation it left has to be released as surely as if the
        // window had closed.
        Assert.False(first.HasOpenPanel);
        Assert.True(second.HasOpenPanel);

        ChatPanel.CloseFor(second.SessionId);
        Flush();
    }

    // A session that is not OpenClaw's reaches the same two calls — every panel
    // binding does — and has to fall straight through them.
    [AvaloniaFact]
    public void APanelOnSomethingElseEntirelyIsNotOurBusiness()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawEnabled = true;

        var fake = new ClaudeBuddy.Tests.FakeChatSession(null)
        {
            SessionId = "cb92-not-openclaw-" + Guid.NewGuid(),
            DisplayName = "Local"
        };

        var orb = new OrbWindow(Guid.NewGuid().ToString());

        ChatPanel.OpenFor(orb, fake);
        Flush();
        ChatPanel.CloseFor(fake.SessionId);
        Flush();
    }

    // ---- a room holds its members ------------------------------------------

    [AvaloniaFact]
    public void AnOpenRoomKeepsTheConversationsItIsMergingResident()
    {
        var member = Fresh(out var key);
        var room = new OpenClawRoomChatSession("openclaw:room:cb92", "#channel");

        room.SetMembers(new[] { (member, "Nova", "#FF0000") });

        Assert.False(member.HasOpenPanel);

        room.PanelOpened();

        // No panel is bound to the member directly, and somebody is looking at
        // it all the same — the room is drawing its turns.
        Assert.True(member.HasOpenPanel);

        var swept = OpenClawSessions.SweepChats(DateTime.UtcNow, null, 0, TimeSpan.Zero);
        Assert.DoesNotContain(key, swept.Evicted);

        room.PanelClosed();
        Assert.False(member.HasOpenPanel);
    }

    [AvaloniaFact]
    public void AMemberJoiningAnOpenRoomIsHeldTooAndOneLeavingIsLetGo()
    {
        var first = Fresh(out _);
        var second = Fresh(out _);
        var room = new OpenClawRoomChatSession("openclaw:room:cb92-churn", "#channel");

        room.SetMembers(new[] { (first, "Nova", "#FF0000") });
        room.PanelOpened();

        room.SetMembers(new[] { (second, "Aurora", "#00FF00") });

        Assert.False(first.HasOpenPanel);
        Assert.True(second.HasOpenPanel);

        room.PanelClosed();
        Assert.False(second.HasOpenPanel);
    }

    [AvaloniaFact]
    public void ARoomsPanelsAreCountedTheSameWayAConversationsAre()
    {
        var member = Fresh(out _);
        var room = new OpenClawRoomChatSession("openclaw:room:cb92-counted", "#channel");

        room.SetMembers(new[] { (member, "Nova", "#FF0000") });

        // Closing one that was never opened has to be harmless here too: the
        // panel calls this on every unbind, including for a room it never
        // reported opening because the session was swapped underneath it.
        room.PanelClosed();
        Assert.False(member.HasOpenPanel);

        room.PanelOpened();
        room.PanelOpened();
        room.PanelClosed();

        // Still held: the second panel is still on the channel, and telling the
        // members otherwise would let them be swept out from under a window.
        Assert.True(member.HasOpenPanel);

        room.PanelClosed();
        Assert.False(member.HasOpenPanel);
    }

    [AvaloniaFact]
    public void ARoomWithAPanelOpenHandsTheResidencyToARebuiltMember()
    {
        var original = Fresh(out var key);
        var room = new OpenClawRoomChatSession("openclaw:room:cb92-live-swap", "#channel");

        room.SetMembers(new[] { (original, "Nova", "#FF0000") });
        room.PanelOpened();
        Assert.True(original.HasOpenPanel);

        // The same key on a new object, which is what a sweep followed by a
        // fresh lookup produces. The room is still on screen, so the new
        // instance has to inherit being looked at and the old one has to stop
        // claiming it — otherwise the evicted one is pinned open forever by a
        // room that no longer reads it.
        var rebuilt = new OpenClawChatSession("openclaw:" + key, key, "Nova");
        room.SetMembers(new[] { (rebuilt, "Nova", "#FF0000") });

        Assert.False(original.HasOpenPanel);
        Assert.True(rebuilt.HasOpenPanel);

        room.PanelClosed();
        Assert.False(rebuilt.HasOpenPanel);
    }

    [AvaloniaFact]
    public void ARoomReachesTheSameTwoCallsAConversationDoes()
    {
        var member = Fresh(out var key);
        var room = new OpenClawRoomChatSession("openclaw:room:cb92-entrypoint", "#channel");

        room.SetMembers(new[] { (member, "Nova", "#FF0000") });

        // The entry point the panel actually uses, rather than the room's own
        // method — a room arriving at it has to be recognised as ours.
        OpenClawSessions.PanelOpened(room);
        Assert.True(member.HasOpenPanel);

        var swept = OpenClawSessions.SweepChats(DateTime.UtcNow, null, 0, TimeSpan.Zero);
        Assert.DoesNotContain(key, swept.Evicted);

        OpenClawSessions.PanelClosed(room);
        Assert.False(member.HasOpenPanel);
    }

    // The regression eviction made possible: the same gateway key arriving on a
    // new object, because the old one was swept and the next lookup built a
    // fresh session. Matching on the key alone left the room subscribed to the
    // instance nobody feeds any more.
    [AvaloniaFact]
    public void ARoomFollowsAMemberThatWasSweptAndRebuilt()
    {
        var original = Fresh(out var key);
        var room = new OpenClawRoomChatSession("openclaw:room:cb92-rebuilt", "#channel");

        room.SetMembers(new[] { (original, "Nova", "#FF0000") });

        OpenClawSessions.SweepChats(DateTime.UtcNow, null, 0, TimeSpan.Zero);
        Assert.False(Resident(key));

        var rebuilt = OpenClawSessions.ChatFor("openclaw:" + key, "Nova") as OpenClawChatSession;
        Assert.NotNull(rebuilt);
        Assert.NotSame(original, rebuilt);

        room.SetMembers(new[] { (rebuilt!, "Nova", "#FF0000") });

        // The room is listening to the new one: a turn on it reaches the merge.
        rebuilt!.SetHistory(new List<HistoryTurn>
        {
            new(ChatRole.Assistant, "after the sweep", null, "",
                DateTimeOffset.UtcNow, null, null)
        });

        Flush();

        Assert.Contains(room.History, t => t.Text == "after the sweep");
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
