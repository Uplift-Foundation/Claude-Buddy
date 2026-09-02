using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// A room orb wearing the people who are in it.
//
// The pie itself is covered in tests/UnitTests/AvatarCompositeTests.cs, which
// asks about pixels. This asks the two questions above that: which members a
// room hands to it, and whether the orb ends up drawing the answer instead of
// the channel's initials.
//
// Membership is published by running a real sessions.list payload through
// OpenClawSessions.Parse, the way ChatPanelAvatarTests deals a colour: Parse is
// the only thing that fills the room table, and a membership dealt any other
// way would not be the one MembersOfRoom answers with.
[Collection("Settings")]
public class OrbRoomAvatarTests
{
    private static byte[] Png(byte r, byte g, byte b)
    {
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    // Agent ids are unique per case because the decoded-picture cache and the
    // composite cache are both process-wide; two cases sharing an id would
    // answer each other.
    private static string Agent(string what) => what + Guid.NewGuid().ToString("N")[..8];

    // The room key OpenClawSessionKind.RoomOf builds: "<surface>:<channel id>".
    private static string Room() => "discord:" + Guid.NewGuid().ToString("N")[..12];

    private static string Key(string agent, string room) =>
        $"agent:{agent}:discord:channel:{room["discord:".Length..]}";

    private static void Publish(params (string Agent, byte[]? Picture)[] agents) =>
        OpenClawSessions.SetIdentitiesForTests(
            agents.ToDictionary(
                a => a.Agent,
                a => new OpenClawSessions.AgentIdentity(a.Agent, null, a.Picture)));

    private static void PublishNothing() =>
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>());

    // Puts the named agents in the named channel, in the order given, each one
    // a second older than the one before — so the first argument is the most
    // recently active and the order the room hands back is the order written
    // here.
    private static void Standing(string room, params string[] agents)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rows = agents.Select((agent, i) =>
            $$"""{"key":"{{Key(agent, room)}}","lastActivityAt":{{now - i * 1000}}}""");

        var json = $$"""{"sessions":[{{string.Join(",", rows)}}]}""";

        ClaudeBuddySettings.OpenClawActiveWithinMinutes = ClaudeBuddySettings.OpenClawActiveWithinAll;
        OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, DateTime.UtcNow);
    }

    private static SessionStatus RoomStatus() => new()
    {
        Source = SessionSource.OpenClaw,
        IsRoom = true,
        State = "idle",
        Title = "smoke-test",
        Kind = SessionKind.Channel,
    };

    // --- what a room draws ----------------------------------------------

    // The feature: an orb standing for a conversation wears the conversation.
    [AvaloniaFact]
    public void ARoomWithTwoPicturedMembersWearsBothOfThem()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");
            var annabel = Agent("annabel");

            Publish((zara, Png(0xE0, 0x20, 0x20)), (annabel, Png(0x20, 0x20, 0xE0)));
            Standing(room, zara, annabel);

            var orb = new OrbWindow(SessionManager.RoomId(room));
            orb.UpdateFrom(RoomStatus());

            Assert.IsType<ImageBrush>(orb.Orb.Fill);
            Assert.False(orb.Glyph.IsVisible, "the channel's initials should give way to the faces");
        }
        finally
        {
            PublishNothing();
        }
    }

    // The room's picture and the panel's portrait are the same object, because
    // both ask AvatarForSession. Two surfaces cutting their own pie would drift
    // the moment either changed.
    [AvaloniaFact]
    public void TheOrbAndTheChatHeaderAskTheSameQuestion()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");
            var annabel = Agent("annabel");

            Publish((zara, Png(0xE0, 0x20, 0x20)), (annabel, Png(0x20, 0x20, 0xE0)));
            Standing(room, zara, annabel);

            var byRoomKey = OpenClawSessions.RoomAvatar(room);
            var bySessionId = OpenClawSessions.AvatarForSession(SessionManager.RoomId(room));

            Assert.NotNull(byRoomKey);
            Assert.Same(byRoomKey, bySessionId);
        }
        finally
        {
            PublishNothing();
        }
    }

    // One member is not a composite. Handing back that agent's own avatar keeps
    // whatever animation it has — a composite is a still — and a channel only
    // one agent talks in genuinely does look like that agent.
    [AvaloniaFact]
    public void ARoomWithOneMemberIsThatMembersOwnPicture()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");

            Publish((zara, Png(0xE0, 0x20, 0x20)));
            Standing(room, zara);

            var roomAvatar = OpenClawSessions.RoomAvatar(room);
            var agentAvatar = OpenClawSessions.AvatarForSession(
                $"openclaw:agent:{zara}:discord:channel:1");

            Assert.NotNull(roomAvatar);
            Assert.Same(agentAvatar, roomAvatar);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Somebody in the room with no picture still counts: they take a wedge in
    // their own colour rather than giving their share away. Asserted as "there
    // is a composite at all", since a room that dropped them would be a room of
    // one and would hand back the pictured agent's own avatar instead.
    [AvaloniaFact]
    public void AMemberWithNoPictureStillTakesAWedge()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");
            var quiet = Agent("quiet");

            Publish((zara, Png(0xE0, 0x20, 0x20)), (quiet, null));
            Standing(room, zara, quiet);

            var composite = OpenClawSessions.RoomAvatar(room);
            var alone = OpenClawSessions.AvatarForSession(
                $"openclaw:agent:{zara}:discord:channel:1");

            Assert.NotNull(composite);
            Assert.NotSame(alone, composite);
        }
        finally
        {
            PublishNothing();
        }
    }

    // --- what a room does not draw ---------------------------------------

    // Nobody with a picture leaves the channel's initials, which say which
    // channel this is. A pie of flat colours would say less than the ring does.
    [AvaloniaFact]
    public void ARoomWhereNobodyHasAPictureKeepsItsInitials()
    {
        try
        {
            var room = Room();
            var one = Agent("one");
            var two = Agent("two");

            Publish((one, null), (two, null));
            Standing(room, one, two);

            var orb = new OrbWindow(SessionManager.RoomId(room));
            orb.UpdateFrom(RoomStatus());

            Assert.Null(OpenClawSessions.RoomAvatar(room));
            Assert.True(orb.Glyph.IsVisible, "the channel's initials are the fallback");
            Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A room nobody is standing in — every member outside whatever the gateway
    // last listed. Nothing to compose, and nothing to fall over on.
    [AvaloniaFact]
    public void AnEmptyRoomHasNoPicture()
    {
        Assert.Null(OpenClawSessions.RoomAvatar(Room()));
    }

    // --- who gets a wedge -------------------------------------------------

    // Four wedges, and they are the four most recently active — not the four
    // the gateway happened to list first, and not four alphabetically.
    //
    // Asserted through who has a picture rather than by counting wedges, which
    // is not observable from out here: the four most recent have none, so there
    // is nothing to draw, even though two members further down the list do.
    [AvaloniaFact]
    public void OnlyTheFourMostRecentMembersAreDrawn()
    {
        try
        {
            var room = Room();
            var recent = Enumerable.Range(0, 4).Select(i => Agent($"recent{i}")).ToArray();
            var older = Enumerable.Range(0, 2).Select(i => Agent($"older{i}")).ToArray();

            Publish(recent.Select(a => (a, (byte[]?)null))
                .Concat(older.Select(a => (a, (byte[]?)Png(0xE0, 0x20, 0x20))))
                .ToArray());

            Standing(room, recent.Concat(older).ToArray());

            Assert.Null(OpenClawSessions.RoomAvatar(room));
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and with the pictures on the recent four instead, the same room does
    // have something to draw — so the assertion above is about *which* four
    // rather than about the cap swallowing everything.
    [AvaloniaFact]
    public void TheFourMostRecentMembersAreTheOnesDrawn()
    {
        try
        {
            var room = Room();
            var recent = Enumerable.Range(0, 4).Select(i => Agent($"recent{i}")).ToArray();
            var older = Enumerable.Range(0, 2).Select(i => Agent($"older{i}")).ToArray();

            Publish(recent.Select(a => (a, (byte[]?)Png(0xE0, 0x20, 0x20)))
                .Concat(older.Select(a => (a, (byte[]?)null)))
                .ToArray());

            Standing(room, recent.Concat(older).ToArray());

            Assert.NotNull(OpenClawSessions.RoomAvatar(room));
        }
        finally
        {
            PublishNothing();
        }
    }

    // --- stability --------------------------------------------------------

    // The same room asked twice is the same picture, not a fresh one: the scan
    // runs a couple of times a second, and a new brush on every tick is what
    // ApplyAvatar's own reference check exists to avoid.
    [AvaloniaFact]
    public void TheSameRoomIsTheSamePictureTwice()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");
            var annabel = Agent("annabel");

            Publish((zara, Png(0xE0, 0x20, 0x20)), (annabel, Png(0x20, 0x20, 0xE0)));
            Standing(room, zara, annabel);

            Assert.Same(OpenClawSessions.RoomAvatar(room), OpenClawSessions.RoomAvatar(room));
        }
        finally
        {
            PublishNothing();
        }
    }

    // Somebody speaking moves them to the front of the member list, and must
    // not move their wedge. This is why the four that are chosen by recency are
    // then sorted by id — chosen and ordered by recency, two agents in a fast
    // exchange would swap halves of the orb every time either of them spoke.
    [AvaloniaFact]
    public void SpeakingDoesNotSwapTheWedgesAround()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");
            var annabel = Agent("annabel");

            Publish((zara, Png(0xE0, 0x20, 0x20)), (annabel, Png(0x20, 0x20, 0xE0)));

            Standing(room, zara, annabel);
            var first = OpenClawSessions.RoomAvatar(room);

            Standing(room, annabel, zara);
            var second = OpenClawSessions.RoomAvatar(room);

            Assert.NotNull(first);
            Assert.Same(first, second);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Somebody joining is a different picture, though — that is a change to who
    // is in the room, which is the whole thing this orb says.
    [AvaloniaFact]
    public void SomebodyJoiningRecutsThePie()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");
            var annabel = Agent("annabel");
            var amber = Agent("amber");

            Publish(
                (zara, Png(0xE0, 0x20, 0x20)),
                (annabel, Png(0x20, 0x20, 0xE0)),
                (amber, Png(0x20, 0xE0, 0x20)));

            Standing(room, zara, annabel);
            var two = OpenClawSessions.RoomAvatar(room);

            Standing(room, zara, annabel, amber);
            var three = OpenClawSessions.RoomAvatar(room);

            Assert.NotNull(two);
            Assert.NotNull(three);
            Assert.NotSame(two, three);
        }
        finally
        {
            PublishNothing();
        }
    }
}
