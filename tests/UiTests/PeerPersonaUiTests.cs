using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// A persona that belongs to a session on another machine, drawn on this one.
//
// The point of the whole ticket is a case that a naive implementation passes
// silently: two machines with the same project checked out at the same path,
// each with its own CLAUDE.md. Resolving a remote session's persona against
// this disk would find *this* machine's file, hand it to the remote orb, and
// look exactly like it had worked — the CB-140 failure, where an orb wore a
// name that was not its own. So the assertion below publishes both personas at
// once under one session id and insists the peer's wins; there is no
// arrangement of the local walk that can satisfy it.
//
// [Collection("Settings")] for the same reason LocalPersonaUiTests is in it:
// constructing an OrbWindow reads a colour setting in a field initializer.
[Collection("Settings")]
public class PeerPersonaUiTests : IDisposable
{
    private readonly List<string> _avatarKeysToClean = new();

    public void Dispose()
    {
        foreach (var key in _avatarKeysToClean) OpenClawAvatars.Forget(key);

        // Both registries are process-wide, so a persona left behind is one a
        // later class in this assembly resolves for a session it never gave
        // one to.
        PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
    }

    // Never closed — closing an OrbWindow corrupts a process-wide FontManager
    // resource shared by every other headless window in the suite. See
    // ChatPanelAvatarTests' comment on the same helper.
    private static OrbWindow NewOrb(string sessionId) => new(sessionId);

    // A real PNG: the bytes go through OpenClawAvatars' actual decoder on the
    // way to the orb, and only an image it can decode proves the wire half and
    // the drawing half are joined to each other.
    private static byte[] Portrait(byte r = 0x8A, byte g = 0x6F, byte b = 0xD4)
    {
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Session ids are unique per case because the decoded-picture cache is
    // process-wide and keyed by one, so two cases sharing an id would answer
    // each other's questions. The "rc:" prefix is not decoration: it is what
    // SessionIdentity.IsPeer keys on, and a remote orb's real id carries it.
    private string PublishPeer(MirrorProtocol.PeerPersona persona)
    {
        var sessionId = "rc:peer-account:" + Guid.NewGuid();
        PeerPersonas.SetForTests(
            new Dictionary<string, MirrorProtocol.PeerPersona> { [sessionId] = persona });

        _avatarKeysToClean.Add(PeerPersonas.AvatarKey(sessionId));
        return sessionId;
    }

    // Exactly the status SessionManager builds for a peer row, and the absence
    // of Cwd is part of the fixture rather than an omission: a remote session
    // has no path on this disk, and that is the first of the two reasons the
    // local walk cannot reach it.
    private static SessionStatus Remote(string title = "job-hunter") => new()
    {
        Source = SessionSource.RemoteControl,
        RemoteCli = MirrorProtocol.CliClaudeCode,
        State = "idle",
        Title = title,
        Color = "",
        Kind = SessionKind.Remote,
    };

    // --- the case the ticket exists for ---

    // Both registries hold a persona for this one id, which is precisely the
    // same-path collision: the local walk, if it ever ran for a remote orb,
    // would produce "Homebody". It must produce "Faraday".
    [AvaloniaFact]
    public void ARemoteOrbWearsThePeersPersonaAndNeverThisMachinesOwn()
    {
        var sessionId = PublishPeer(new MirrorProtocol.PeerPersona("Faraday"));

        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
        {
            [sessionId] = new("Homebody", null, null, null, null, Array.Empty<string>()),
        });

        ClaudeBuddySettings.TwoLetterGlyphs = true;
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Remote());

        Assert.Equal("Fa", orb.GlyphText);
    }

    // The structural half of the same statement, asserted where the guard
    // actually lives rather than through a window: a remote source produces no
    // candidate files at all, so there is nothing for the walk to read even if
    // a path were somehow available to it.
    [Fact]
    public void ARemoteSessionOffersTheLocalWalkNoFilesToRead()
    {
        var files = LocalPersona.CandidateFiles(
            "/Users/test/haunted-mansion", LocalPersona.UserConfigDirs(),
            SessionSource.RemoteControl, agentName: "");

        Assert.Empty(files);
    }

    // --- the picture ---

    // Bytes off the wire become the orb's fill, and the letters give way, the
    // same as a gateway agent's picture does. An orb showing both is
    // unreadable, and one showing neither is the feature not working.
    [AvaloniaFact]
    public void APeerPortraitReplacesTheLetters()
    {
        var sessionId = PublishPeer(new MirrorProtocol.PeerPersona("Faraday", Avatar: Portrait()));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Remote());

        Assert.False(orb.Glyph.IsVisible, "the letters should give way to the picture");
        Assert.IsType<ImageBrush>(orb.Orb.Fill);
    }

    // Bytes that are not an image are refused by the decoder and the orb falls
    // back to its letters, rather than drawing nothing or throwing. This is the
    // acceptance criterion about an unreadable portrait, and it is the arm a
    // hand-written byte array in a wire test cannot reach.
    [AvaloniaFact]
    public void AnUnreadablePeerPortraitLeavesTheOrbWearingItsLetters()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        var sessionId = PublishPeer(
            new MirrorProtocol.PeerPersona("Faraday", Avatar: new byte[] { 0, 1, 2, 3, 4 }));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Remote());

        Assert.True(orb.Glyph.IsVisible);
        Assert.Equal("Fa", orb.GlyphText);
    }

    // --- no regression for the common case ---

    // A remote session whose far machine has no persona draws exactly as it did
    // before this ticket: its own title, its own letters. Every remote orb on a
    // machine with no personas anywhere takes this branch, so it is the one
    // worth being surest about.
    [AvaloniaFact]
    public void ARemoteSessionWithNoPersonaKeepsTheTitleItAlwaysHad()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        PeerPersonas.SetForTests(new Dictionary<string, MirrorProtocol.PeerPersona>());

        var orb = NewOrb("rc:peer-account:" + Guid.NewGuid());
        orb.UpdateFrom(Remote(title: "job-hunter"));

        Assert.Equal("Jh", orb.GlyphText);
    }

    // The tooltip is the half a persona must not take over — it is the only
    // place still saying which session this orb is. Same rule as the local
    // persona's, asserted separately because it reaches the name through a
    // different registry.
    [AvaloniaFact]
    public void TheTooltipStillNamesTheSessionAPeerPersonaRenamed()
    {
        var sessionId = PublishPeer(new MirrorProtocol.PeerPersona("Faraday"));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Remote(title: "job-hunter"));

        var tip = Assert.IsAssignableFrom<Control>(ToolTip.GetTip(orb.Root));
        var text = string.Join(" ", tip.GetSelfAndLogicalDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text));

        Assert.Contains("Faraday", text);
        Assert.Contains("job-hunter", text);
    }

    // --- the voice ---

    // A peer persona naming a voice this machine has installed resolves to it,
    // through the same matcher a local persona uses. A voice that is not
    // installed here is the other arm, and it answers null rather than
    // substituting one — CB-128's rule, applied one registry over.
    [AvaloniaFact]
    public void APeerPersonaResolvesAVoiceThisMachineActuallyHas()
    {
        var installed = TextToSpeech.SystemVoices()[0];
        var options = TextToSpeech.SystemVoices()
            .Select(name => new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, name, name))
            .ToList();

        var sessionId = PublishPeer(new MirrorProtocol.PeerPersona("Faraday", installed, 1.2));

        var voice = PeerPersonas.VoiceForSession(sessionId, options);
        Assert.NotNull(voice);
        Assert.Equal(installed, voice!.Name);
        Assert.Equal(1.2, PeerPersonas.RateForSession(sessionId));

        var unknown = PublishPeer(new MirrorProtocol.PeerPersona("Faraday", "Not A Real Voice"));
        Assert.Null(PeerPersonas.VoiceForSession(unknown, options));
    }
}
