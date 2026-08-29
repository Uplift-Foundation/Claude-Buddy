using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClaudeBuddy.Tests;
using Xunit;

namespace ClaudeBuddy.UiTests;

// What a room actually looks like once the gateway has said who was talking.
//
// The rules themselves are covered a case at a time in tests/UnitTests, against
// lists. What only this can check is that they reach the screen: a room turn is
// drawn by the same TurnView that draws every other turn, through bindings that
// read Role and Speaker and nothing else, so a rule that produces the right
// ChatTurn and the wrong bubble would pass every unit test in the branch.
//
// Driven through real OpenClawChatSession members and a real
// OpenClawRoomChatSession, the way OpenClawRoomRebuildTests does, rather than
// through FakeChatSession: the merge is the thing under test and a fake would
// only be asserting what the fake was told.
[Collection("Settings")]
public class ChatPanelRoomAttributionTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Controls.Controls RenderedRows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static OpenClawChatSession Member(string agent)
    {
        var chat = new OpenClawChatSession(
            $"openclaw:agent:{agent}:discord:channel:900",
            $"agent:{agent}:discord:channel:900",
            agent);

        // Nothing held back, so the trust window never constrains a test that is
        // about attribution.
        chat.HasMore = false;
        return chat;
    }

    private static void Give(
        OpenClawChatSession session,
        params (ChatRole Role, string Text, int Minute, bool Mine, string? Speaker)[] turns) =>
        session.SetHistory(turns
            .Select(t => new HistoryTurn(t.Role, t.Text, null, "", T0.AddMinutes(t.Minute),
                                         t.Speaker, null, t.Mine))
            .ToList());

    private static OpenClawRoomChatSession Room(
        string name,
        params (OpenClawChatSession Chat, string Agent, string Colour)[] members)
    {
        var room = new OpenClawRoomChatSession(
            "openclaw:room:discord:900:" + Guid.NewGuid().ToString("N")[..8], name);

        room.SetMembers(members.ToList());
        room.Rebuild();
        return room;
    }

    // The bubble is the Border the row's alignment is bound to. Found by having
    // an alignment at all rather than by name: the template has several borders
    // in it — the speaker's chip, the avatar's clip — and only the bubble takes
    // a side.
    private static Border Bubble(Control row) =>
        row.GetVisualDescendants().OfType<Border>()
           .First(b => b.HorizontalAlignment != HorizontalAlignment.Stretch);

    private static System.Collections.Generic.IEnumerable<string?> TextsIn(Control row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text);

    // --- your own message ---------------------------------------------------

    // The whole point of the ticket, on screen: a message you sent to a channel
    // comes back looking like yours. Before this it was drawn as the room's own
    // neutral voice, on the left with no name — because the copies in the
    // members' transcripts are user-role like everybody else's and nothing said
    // which was which.
    [AvaloniaFact]
    public void AMessageYouSentIsDrawnAsYourOwnBubble()
    {
        var quill = Member("quill");
        Give(quill, (ChatRole.User, "anyone free to look at the build?", 1, true, null));

        var room = Room("#lobby", (quill, "Quill", "#7f7"));
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        var row = RenderedRows(ChatPanelTestAccess.Instance!)[0];

        Assert.Equal(HorizontalAlignment.Right, Bubble(row).HorizontalAlignment);
        Assert.Contains("anyone free to look at the build?", TextsIn(row));
    }

    // ...and wears nobody's name. A chip on your own bubble would be the panel
    // telling you who you are.
    [AvaloniaFact]
    public void YourOwnBubbleCarriesNoSpeakerChip()
    {
        var quill = Member("quill");
        Give(quill, (ChatRole.User, "anyone free to look at the build?", 1, true, null));

        var room = Room("#lobby", (quill, "Quill", "#7f7"));
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        var row = RenderedRows(ChatPanelTestAccess.Instance!)[0];

        Assert.DoesNotContain("Quill", TextsIn(row));
    }

    // --- somebody else ------------------------------------------------------

    // A named relay — an agent whose own session is not in this room, or another
    // person in the channel — draws on the left with their name above it. The
    // alternative it replaces is an anonymous grey bubble you have to work out
    // from context.
    [AvaloniaFact]
    public void ANamedRelayIsDrawnWithItsName()
    {
        var quill = Member("quill");
        Give(quill, (ChatRole.User, "Nodes are loaded.", 1, false, "Thistle"));

        var room = Room("#lobby", (quill, "Quill", "#7f7"));
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        var row = RenderedRows(ChatPanelTestAccess.Instance!)[0];

        Assert.Equal(HorizontalAlignment.Left, Bubble(row).HorizontalAlignment);
        Assert.Contains("Thistle", TextsIn(row));
    }

    // Both sides of one conversation, in one panel, which is the reading this
    // was worth doing for: your message on the right, an agent's answer on the
    // left under its own name.
    [AvaloniaFact]
    public void AConversationDrawsWithSidesRatherThanAsOneColumn()
    {
        var quill = Member("quill");
        Give(quill,
            (ChatRole.User, "anyone free to look at the build?", 1, true, null),
            (ChatRole.Assistant, "Taking it now.", 2, false, null));

        var room = Room("#lobby", (quill, "Quill", "#7f7"));
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        var rows = RenderedRows(ChatPanelTestAccess.Instance!);

        Assert.Equal(HorizontalAlignment.Right, Bubble(rows[0]).HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Left, Bubble(rows[1]).HorizontalAlignment);
        Assert.Contains("Quill", TextsIn(rows[1]));
    }

    // --- a send that cannot happen ------------------------------------------

    // A room whose members have nowhere to deliver refuses, and the refusal is
    // on screen. Silence is the failure this ticket is about: the message looked
    // sent and nobody in the channel had it.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task AnUndeliverableSendPutsItsReasonOnScreen()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawReplyEnabled = true;

        var quill = Member("quill");
        var room = Room("#lobby", (quill, "Quill", "#7f7"));
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        await room.SendAsync("anyone free to look at the build?");
        FlushRender();

        var texts = RenderedRows(ChatPanelTestAccess.Instance!)
            .SelectMany(r => TextsIn((Control)r))
            .ToList();

        Assert.Contains(texts, t => t is not null && t.Contains("Couldn't post to #lobby"));
        Assert.Contains(texts, t => t is not null && t.Contains("anyone free to look at the build?"));
    }

    // ...and it is still there after the next rebuild, which is the half that
    // was broken. A member event schedules a coalesced rebuild on the
    // dispatcher, Rebuild throws the transcript away and re-merges from the
    // members, and a note the room wrote has no member to be merged back from.
    // Until now it simply vanished — and it vanished a moment later, so what a
    // person saw was a reason that appeared and then went.
    //
    // Here rather than in tests/UnitTests because that coalescing is the
    // mechanism: it takes a real dispatcher pass for a member's event to reach
    // the room at all.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task TheReasonSurvivesAMembersNextEvent()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.OpenClawReplyEnabled = true;

        var quill = Member("quill");
        var room = Room("#lobby", (quill, "Quill", "#7f7"));
        _toClean.Add(room.SessionId);

        ChatPanel.OpenFor(NewOrb(), room);
        FlushRender();

        await room.SendAsync("anyone free to look at the build?");
        FlushRender();

        // Something arrives in the channel, which is what any busy room does
        // within seconds of a failed send.
        Give(quill, (ChatRole.Assistant, "Still here.", 5, false, null));
        FlushRender();

        var texts = RenderedRows(ChatPanelTestAccess.Instance!)
            .SelectMany(r => TextsIn((Control)r))
            .ToList();

        Assert.Contains(texts, t => t is not null && t.Contains("Still here."));
        Assert.Contains(texts, t => t is not null && t.Contains("Couldn't post to #lobby"));
    }
}
