using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Two lookups that run in front of a user: the face drawn beside a message in a
// merged room, and the sentence the orb's speak button reads out.
//
// AvatarForAgentName works by display name, which is the wrong way round and is
// what a merged room view leaves you with — a turn carries who said it, not which
// agent id said it, because the panel's turn model is deliberately
// transport-agnostic and an agent id is not something it knows about. So the
// interesting behaviour is what it does when a name is not enough to identify
// anyone, and that is the part worth pinning: agent ids are unique and their
// names are whatever somebody typed.
//
// Serialised: both read the process-wide identity and name tables.
[Collection("Settings")]
public class OpenClawNameLookupTests
{
    // A real, decodable PNG. Four bytes of a plausible-looking header is not
    // enough: OpenClawAvatars.For actually decodes what it is handed and
    // correctly returns nothing for nonsense — which is the behaviour
    // ChatPanel's own image path was found NOT to have, and the reason this file
    // lives in the UI suite where Skia is real.
    private static byte[] Png()
    {
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(0x5A, 0xF7, 0x8E, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    // Keyed by the BARE agent id — "zara", not "openclaw:agent:zara:main".
    // IdentityOf is handed parts[1] of a split session key, so the identity table
    // is keyed by the middle segment and nothing else. Getting that wrong is a
    // silent miss rather than an error, which is how the first draft of this file
    // came to assert a null it had caused itself.
    private static void Identities(params (string Id, string Name)[] agents)
    {
        var identities = new Dictionary<string, OpenClawSessions.AgentIdentity>();
        var names = new Dictionary<string, string>();

        foreach (var (id, name) in agents)
        {
            identities[id] = new OpenClawSessions.AgentIdentity(name, null, Png());
            names[id] = name;
        }

        OpenClawSessions.SetIdentitiesForTests(identities, names);
    }

    // ---- AvatarForAgentName ---------------------------------------------

    [AvaloniaFact]
    public void ANameThatIdentifiesExactlyOneAgentGetsThatAgentsFace()
    {
        Identities(("zara", "Zara"), ("kai", "Kai"));

        Assert.NotNull(OpenClawSessions.AvatarForAgentName("Zara"));
    }

    // The refusal, and the reason it is a refusal rather than a guess: the
    // initials chip is a fine answer and the wrong face is not.
    [AvaloniaFact]
    public void ANameTwoAgentsShareGetsNoFaceAtAll()
    {
        Identities(("zara", "Zara"), ("zara2", "Zara"));

        Assert.Null(OpenClawSessions.AvatarForAgentName("Zara"));
    }

    // Three is the same answer as two — the check is "more than one", not "two".
    [AvaloniaFact]
    public void ANameThreeAgentsShareAlsoGetsNoFace()
    {
        Identities(
            ("a", "Zara"), ("b", "Zara"), ("c", "Zara"));

        Assert.Null(OpenClawSessions.AvatarForAgentName("Zara"));
    }

    [AvaloniaFact]
    public void ANameNobodyHasGetsNoFace()
    {
        Identities(("zara", "Zara"));

        Assert.Null(OpenClawSessions.AvatarForAgentName("Nobody"));
    }

    // Ordinal, so two agents whose names differ only by case are two agents
    // rather than an ambiguity — and asking for one of them by the other's
    // spelling finds nothing.
    [AvaloniaFact]
    public void TheNameMatchIsCaseSensitive()
    {
        Identities(("zara", "Zara"));

        Assert.Null(OpenClawSessions.AvatarForAgentName("zara"));
        Assert.NotNull(OpenClawSessions.AvatarForAgentName("Zara"));
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoNameAtAllGetsNoFace(string? name)
    {
        Identities(("zara", "Zara"));

        Assert.Null(OpenClawSessions.AvatarForAgentName(name));
    }

    // A name that resolves to an agent with no picture stored gets nothing,
    // rather than an empty avatar the panel would try to draw.
    //
    // A distinct agent id, and Forget first, because OpenClawAvatars.For caches
    // per agent — see the test below. Reusing "zara" here returned the face an
    // earlier case in this class had published, which is the cache working and
    // was my mistake, not its.
    [AvaloniaFact]
    public void AnAgentWithNoPictureGetsNoFace()
    {
        OpenClawAvatars.Forget("never-pictured");
        OpenClawSessions.SetIdentitiesForTests(
            new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["never-pictured"] = new("Unpictured", null, null),
            },
            new Dictionary<string, string> { ["never-pictured"] = "Unpictured" });

        Assert.Null(OpenClawSessions.AvatarForAgentName("Unpictured"));
    }

    // The cache is consulted before the bytes are even looked at, and it caches
    // the *absence* of a picture too. So an agent whose picture is later removed
    // keeps showing the old one until something calls Forget — which is what
    // Forget is for, and worth an assertion because "returns a face for a null
    // picture" reads like a bug until you see the cache.
    [AvaloniaFact]
    public void ThePictureCacheIsKeyedByAgentAndSurvivesTheBytesChanging()
    {
        const string agent = "cache-probe";
        OpenClawAvatars.Forget(agent);

        var first = OpenClawAvatars.For(agent, Png());
        Assert.NotNull(first);

        // Same agent, no bytes at all: the cache answers first.
        Assert.Same(first, OpenClawAvatars.For(agent, null));

        OpenClawAvatars.Forget(agent);
        Assert.Null(OpenClawAvatars.For(agent, null));
    }

    // Zero-length is treated as no picture rather than as something to decode.
    [AvaloniaFact]
    public void AnEmptyPictureIsTreatedAsNoPicture()
    {
        const string agent = "empty-probe";
        OpenClawAvatars.Forget(agent);

        Assert.Null(OpenClawAvatars.For(agent, System.Array.Empty<byte>()));
    }

    // ---- LastAssistantText ----------------------------------------------

    // What the orb's flyout speak button reads out. It has no panel open, so
    // there is no transcript on screen to take it from — this walks the session's
    // history backwards instead.
    private static OpenClawChatSession Session(
        params (ChatRole Role, string Text)[] turns)
    {
        var session = new OpenClawChatSession("openclaw:agent:zara:main", "zara", "Zara");
        session.SetHistory(turns
            .Select(t => new HistoryTurn(t.Role, t.Text, null, "",
                          new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
                          null, null))
            .ToList());
        return session;
    }

    [AvaloniaFact]
    public void TheLastThingTheAgentSaidIsWhatIsRead()
    {
        var session = Session(
            (ChatRole.Assistant, "first"),
            (ChatRole.User, "a question"),
            (ChatRole.Assistant, "second"));

        Assert.Equal("second", OpenClawSessions.LastAssistantText(session));
    }

    // Walked backwards past the user's own turn, which is the point — the speak
    // button reads what the agent said, not the last thing in the transcript.
    [AvaloniaFact]
    public void YourOwnLastMessageIsNotWhatIsRead()
    {
        var session = Session(
            (ChatRole.Assistant, "the build is green"),
            (ChatRole.User, "thanks"));

        Assert.Equal("the build is green", OpenClawSessions.LastAssistantText(session));
    }

    // A blank assistant turn is skipped rather than read as silence — a tool-only
    // turn would otherwise make the speak button do nothing while looking like it
    // worked.
    [AvaloniaFact]
    public void ABlankAssistantTurnIsSkippedForTheLastRealOne()
    {
        var session = Session(
            (ChatRole.Assistant, "the build is green"),
            (ChatRole.Assistant, "   "));

        Assert.Equal("the build is green", OpenClawSessions.LastAssistantText(session));
    }

    [AvaloniaFact]
    public void ASessionWithNothingFromTheAgentHasNothingToRead()
    {
        Assert.Null(OpenClawSessions.LastAssistantText(Session((ChatRole.User, "hello?"))));
    }

    [AvaloniaFact]
    public void AnEmptySessionHasNothingToRead()
    {
        Assert.Null(OpenClawSessions.LastAssistantText(Session()));
    }

    // A System turn is the app talking, not the agent, so it is not read out.
    [AvaloniaFact]
    public void ANoteFromTheAppItselfIsNotReadOut()
    {
        var session = Session(
            (ChatRole.Assistant, "the build is green"),
            (ChatRole.System, "Replying is off."));

        Assert.Equal("the build is green", OpenClawSessions.LastAssistantText(session));
    }
}
