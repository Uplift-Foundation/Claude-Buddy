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

    // The same, with a recency window and each agent's last activity given in
    // minutes ago — so a case can put somebody in the channel who is plainly not
    // talking in it.
    private static void StandingSince(string room, int windowMinutes,
        params (string Agent, int MinutesAgo)[] agents)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rows = agents.Select(a =>
            $$"""{"key":"{{Key(a.Agent, room)}}","lastActivityAt":{{now - a.MinutesAgo * 60_000L}}}""");

        var json = $$"""{"sessions":[{{string.Join(",", rows)}}]}""";

        ClaudeBuddySettings.OpenClawActiveWithinMinutes = windowMinutes;
        OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, DateTime.UtcNow);
        ClaudeBuddySettings.OpenClawActiveWithinMinutes = ClaudeBuddySettings.OpenClawActiveWithinAll;
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

    // An agent standing in the room that agents.list has not described yet —
    // the ordinary state in the seconds between a gateway connecting and its
    // identities arriving, since sessions.list answers first. No picture and no
    // colour is nothing to draw, and the channel keeps its initials until the
    // identities land.
    [AvaloniaFact]
    public void AMemberWithNoIdentityYetIsNothingToDraw()
    {
        try
        {
            var room = Room();
            PublishNothing();
            Standing(room, Agent("stranger"));

            Assert.Null(OpenClawSessions.RoomAvatar(room));
        }
        finally
        {
            PublishNothing();
        }
    }

    // One agent, two sessions, one room. The gateway does list an agent more
    // than once in a channel, and a member counted twice would take two wedges
    // of a pie divided between the same face.
    [AvaloniaFact]
    public void AnAgentInARoomTwiceStillTakesOneWedge()
    {
        try
        {
            var room = Room();
            var zara = Agent("zara");

            Publish((zara, Png(0xE0, 0x20, 0x20)));
            Standing(room, zara, zara);

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

    // --- who counts as being in the room ----------------------------------

    // The bug this file was extended for. #social-media was drawn as four faces
    // while two agents were talking in it: the other two had a session in the
    // channel and had said nothing for hours.
    //
    // A member the recency filter dropped has no orb, so it has no wedge.
    // Asserted through who has a picture: only the two quiet ones do, so there
    // is nothing left to draw.
    [AvaloniaFact]
    public void AMemberWithNoOrbGetsNoWedge()
    {
        try
        {
            var room = Room();
            var talking = Agent("talking");
            var alsoTalking = Agent("also");
            var quiet = Agent("quiet");
            var alsoQuiet = Agent("alsoquiet");

            Publish(
                (talking, null),
                (alsoTalking, null),
                (quiet, Png(0xE0, 0x20, 0x20)),
                (alsoQuiet, Png(0x20, 0x20, 0xE0)));

            StandingSince(room, 60,
                (talking, 1), (alsoTalking, 2), (quiet, 300), (alsoQuiet, 400));

            Assert.Null(OpenClawSessions.RoomAvatar(room));
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and the two who are talking are the ones drawn, so the assertion above
    // is about *which* members rather than about the room having emptied.
    [AvaloniaFact]
    public void TheMembersWithOrbsAreTheOnesDrawn()
    {
        try
        {
            var room = Room();
            var talking = Agent("talking");
            var alsoTalking = Agent("also");
            var quiet = Agent("quiet");

            Publish(
                (talking, Png(0xE0, 0x20, 0x20)),
                (alsoTalking, Png(0x20, 0x20, 0xE0)),
                (quiet, Png(0x20, 0xE0, 0x20)));

            StandingSince(room, 60, (talking, 1), (alsoTalking, 2), (quiet, 300));

            Assert.NotNull(OpenClawSessions.RoomAvatar(room));

            // Two of the three, named rather than counted. Deliberately not
            // asserted as "the same picture object as after the quiet one
            // leaves": dropping a member re-deals the whole palette, so the
            // colours in the cache key can move even though the faces have not.
            Assert.Equal(
                new[] { Key(talking, room), Key(alsoTalking, room) },
                OpenClawSessions.ParticipantsOfRoom(room));
        }
        finally
        {
            PublishNothing();
        }
    }

    // The room's *chat* is unchanged by all of the above, and has to be: it
    // merges a quiet agent's transcript, and building it from who is currently
    // talking is exactly the bug CB-27 fixed. Two lists, two questions.
    [AvaloniaFact]
    public void AQuietMemberIsStillInTheRoomForItsChat()
    {
        var room = Room();
        var talking = Agent("talking");
        var quiet = Agent("quiet");

        StandingSince(room, 60, (talking, 1), (quiet, 300));

        Assert.Contains(Key(quiet, room), OpenClawSessions.MembersOfRoom(room));
        Assert.DoesNotContain(Key(quiet, room), OpenClawSessions.ParticipantsOfRoom(room));
    }

    // A room orb outliving its sessions — "Keep orbs for" holds one on screen
    // after every member has dropped out of the window. The faces of who was in
    // it are still true, and reverting to the channel's initials at that moment
    // would be a change on screen with nothing behind it.
    [AvaloniaFact]
    public void ARoomWhoseMembersHaveAllGoneQuietKeepsItsFaces()
    {
        try
        {
            var room = Room();
            var one = Agent("one");
            var two = Agent("two");

            Publish((one, Png(0xE0, 0x20, 0x20)), (two, Png(0x20, 0x20, 0xE0)));
            StandingSince(room, 60, (one, 300), (two, 400));

            Assert.Empty(OpenClawSessions.ParticipantsOfRoom(room));
            Assert.NotNull(OpenClawSessions.RoomAvatar(room));
        }
        finally
        {
            PublishNothing();
        }
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
