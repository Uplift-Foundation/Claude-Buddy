using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The room's rebuild, and the coalescing around it.
//
// Here rather than in tests/UnitTests because ScheduleRebuild posts to the
// dispatcher at Background priority — which is the whole mechanism: a streaming
// reply raises TurnUpdated per snapshot, several times a second, and each one
// would otherwise re-merge and re-sort every transcript in the room for a single
// row whose text changed. The panel cannot draw faster than that anyway.
//
// The sibling suite in tests/UnitTests covers the merge itself by calling Rebuild
// directly. What is only observable here is that a member's event reaches the room
// at all, and that many of them cost one rebuild.
[Collection("Settings")]
public class OpenClawRoomRebuildTests
{
    private static OpenClawChatSession Member(string agent) =>
        new($"openclaw:agent:{agent}:discord:channel:1",
            $"agent:{agent}:discord:channel:1", agent);

    private static readonly System.DateTimeOffset T0 =
        new(2026, 8, 24, 12, 0, 0, System.TimeSpan.Zero);

    private static void Give(
        OpenClawChatSession session,
        params (ChatRole Role, string Text, int Minute)[] turns)
    {
        session.SetHistory(turns
            .Select(t => new HistoryTurn(t.Role, t.Text, null, "", T0.AddMinutes(t.Minute),
                          null, null))
            .ToList());
    }

    private static OpenClawRoomChatSession Room(
        params (OpenClawChatSession Chat, string Agent, string Colour)[] members)
    {
        var room = new OpenClawRoomChatSession("openclaw:room:discord:1", "#general");
        foreach (var (chat, _, _) in members) chat.HasMore = false;

        room.SetMembers(members.ToList());
        room.Rebuild();
        return room;
    }

    // A member speaking reaches the room without anything calling Rebuild by
    // hand. That is the subscription working, and it is the thing that breaks
    // silently — an unsubscribed member's messages simply stop appearing.
    [AvaloniaFact]
    public void AMemberSpeakingReachesTheRoomOnItsOwn()
    {
        var zara = Member("zara");
        zara.HasMore = false;
        var room = Room((zara, "Zara", "#ff0000"));

        Give(zara, (ChatRole.Assistant, "the build is green", 1));

        // The rebuild is posted, so nothing has happened yet.
        Assert.Empty(room.History);

        Dispatcher.UIThread.RunJobs();

        Assert.Contains(room.History, t => t.Text == "the build is green");
    }

    // Several changes in one pass of the dispatcher cost one rebuild. Asserted
    // through HistoryReplaced, which the room raises once per rebuild: a
    // streaming reply arriving as ten snapshots must not re-sort the room ten
    // times.
    [AvaloniaFact]
    public void ManyChangesInOnePassCostOneRebuild()
    {
        var zara = Member("zara");
        zara.HasMore = false;
        var room = Room((zara, "Zara", "#ff0000"));

        var rebuilds = 0;
        room.HistoryReplaced += () => rebuilds++;

        for (var i = 0; i < 10; i++)
        {
            Give(zara, (ChatRole.Assistant, "still typing " + i, 1));
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, rebuilds);
    }

    // And a change after the queue has drained schedules a fresh one, rather than
    // the flag being left set and every later change ignored — which is the way
    // this kind of coalescing usually breaks.
    [AvaloniaFact]
    public void AChangeAfterTheQueueDrainsSchedulesAnotherRebuild()
    {
        var zara = Member("zara");
        zara.HasMore = false;
        var room = Room((zara, "Zara", "#ff0000"));

        var rebuilds = 0;
        room.HistoryReplaced += () => rebuilds++;

        Give(zara, (ChatRole.Assistant, "first", 1));
        Dispatcher.UIThread.RunJobs();

        Give(zara, (ChatRole.Assistant, "second", 2));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, rebuilds);
        Assert.Contains(room.History, t => t.Text == "second");
    }

    // Paging a member back is the one thing that widens the window the room can
    // be trusted over, so it has to redraw — and the subscription for it was
    // missing once, which meant a page of history fetched successfully would not
    // have shown until something else happened to trigger a rebuild.
    [AvaloniaFact]
    public void OlderHistoryArrivingOnTheFrontTriggersARebuild()
    {
        var zara = Member("zara");
        zara.HasMore = false;
        Give(zara, (ChatRole.Assistant, "recent", 10));
        var room = Room((zara, "Zara", "#ff0000"));

        // Counted across BOTH events, because the rebuild reports itself as a
        // prepend rather than a replacement when the merge only grew at the
        // front — which is the distinction the sibling suite in tests/UnitTests
        // covers, and the reason a test watching only HistoryReplaced here saw
        // nothing and looked like the subscription was missing.
        var rebuilds = 0;
        room.HistoryReplaced += () => rebuilds++;
        room.HistoryPrepended += _ => rebuilds++;

        // Prepending is what a fetched page does.
        zara.PrependHistory(new[]
        {
            new HistoryTurn(ChatRole.Assistant, "older", null, "", T0.AddMinutes(1), null, null),
        }.ToList());

        Dispatcher.UIThread.RunJobs();

        Assert.True(rebuilds >= 1, "a prepend should have scheduled a rebuild");
        Assert.Contains(room.History, t => t.Text == "older");
    }
}
