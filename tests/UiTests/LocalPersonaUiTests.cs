using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// What a CLAUDE.md persona actually does to the two surfaces a user looks at:
// the orb, and the header of the chat panel that opens under it.
//
// Everything about *reading* a persona is covered elsewhere — the grammar in
// tests/UnitTests, the file walk and its security bounds in
// tests/IntegrationTests. This suite starts from a persona already resolved,
// published through LocalPersonas.SetForTests the way the scan would publish
// one, and asks the only question those cannot: whether the answer reaches the
// screen. That gap is not hypothetical. The OpenClaw workspace identity shipped
// with a fully-tested parser and an orb that never drew what it parsed, which
// is why OrbAvatarTests exists at all, and this is the same seam one registry
// over.
//
// [Collection("Settings")] because half of these read ClaudeBuddySettings while
// constructing a window (OrbWindow picks up a colour in a field initializer)
// and TwoLetterGlyphs is a setting these tests set outright.
[Collection("Settings")]
public class LocalPersonaUiTests : IDisposable
{
    private readonly List<string> _panelsToClean = new();
    private readonly List<string> _avatarKeysToClean = new();
    private readonly bool _twoLetterWas = ClaudeBuddySettings.TwoLetterGlyphs;

    public void Dispose()
    {
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
        foreach (var key in _avatarKeysToClean) OpenClawAvatars.Forget(key);

        // The registry is process-wide, exactly like OpenClawSessions' identity
        // table, so a persona left behind is a persona some later class in this
        // assembly resolves for a session it never gave one to.
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
        ClaudeBuddySettings.TwoLetterGlyphs = _twoLetterWas;
    }

    // Never closed — see ChatPanelAvatarTests' own comment on the same helper:
    // closing an OrbWindow corrupts a process-wide FontManager resource shared
    // by every other headless window in the suite.
    private static OrbWindow NewOrb(string sessionId) => new(sessionId);

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // A real PNG rather than a hand-written byte array: the picture goes through
    // OpenClawAvatars' actual decoder on the way to the orb, and only a file it
    // can decode proves the two halves are wired to each other.
    private static byte[] Portrait(byte r = 0x8A, byte g = 0x6F, byte b = 0xD4)
    {
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static LocalPersona.Persona Persona(
        string? name = "Leota", string? voice = null, double? rate = null, byte[]? avatar = null) =>
        new(name, voice, rate, avatar,
            avatar is null ? null : "/tmp/leota/CLAUDE.md",
            avatar is null ? null : "/tmp/leota/leota.png",
            new[] { "/tmp/leota/CLAUDE.md" });

    // Session ids are unique per case for the same reason agent ids are in
    // OrbAvatarTests: the decoded-picture cache is process-wide and keyed by
    // one, so two cases sharing an id would answer each other's questions.
    private string PublishPersona(LocalPersona.Persona persona)
    {
        var sessionId = "local-persona-" + Guid.NewGuid();
        LocalPersonas.SetForTests(
            new Dictionary<string, LocalPersona.Persona> { [sessionId] = persona });

        _avatarKeysToClean.Add(LocalPersonas.AvatarKey(sessionId));
        return sessionId;
    }

    private static SessionStatus Local(string title = "claude-buddy", string agent = "") => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Title = title,
        Agent = agent,
        Cwd = "/Users/test/haunted-mansion",
        Color = "",
        Cli = "",
    };

    // --- the orb's letters ---

    // The letters first, before any picture: a persona that names an agent and
    // gives it no portrait is the ordinary case, and "Le" is the whole of what
    // it changes. Asserted against the literal rather than against
    // OrbGlyph.For(...) so the test says what a user would see — computing the
    // expectation with the same function under test is how a glyph bug survived
    // for a year (see tests/GlyphTests).
    [AvaloniaFact]
    public void APersonaNamesTheOrbAndTheOrbWearsItsLetters()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var sessionId = PublishPersona(Persona());
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Local());

        Assert.Equal("Le", orb.GlyphText);
        Assert.True(orb.Glyph.IsVisible);
    }

    // The precedence, both ends of it, on one status each. The middle rung is
    // the one worth pinning: a title is what Claude Code decided this
    // conversation was about, and a persona is somebody writing down what the
    // agent is called, so the persona wins — while an agent name, which is
    // per-member where a persona is per-repository, wins over both.
    [AvaloniaFact]
    public void APersonaBeatsTheTitleAndAnAgentNameBeatsThePersona()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var sessionId = PublishPersona(Persona());

        var titled = NewOrb(sessionId);
        titled.UpdateFrom(Local(title: "Ticket Triage"));
        Assert.Equal("Le", titled.GlyphText);

        var withAgent = NewOrb(sessionId);
        withAgent.UpdateFrom(Local(title: "Ticket Triage", agent: "Menu UX"));
        Assert.Equal("Mu", withAgent.GlyphText);
    }

    // The tooltip is the half a persona must *not* take over. It is the only
    // place left saying which repository and which conversation this orb is,
    // and an orb that has stopped saying either is worse than one that never
    // said who.
    [AvaloniaFact]
    public void TheTooltipStillNamesTheSessionAPersonaRenamed()
    {
        var sessionId = PublishPersona(Persona());
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Local(title: "Ticket Triage"));

        var tip = Assert.IsAssignableFrom<Control>(ToolTip.GetTip(orb.Root));
        var text = string.Join(" ", tip.GetSelfAndLogicalDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text));

        Assert.Contains("Leota", text);
        Assert.Contains("Ticket Triage", text);
    }

    // A session with no persona is untouched — the branch that matters most,
    // since every orb on a machine with no CLAUDE.md personas takes it.
    [AvaloniaFact]
    public void ASessionWithNoPersonaKeepsTheTitleItAlwaysHad()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var orb = NewOrb("local-none-" + Guid.NewGuid());
        orb.UpdateFrom(Local(title: "Ticket Triage"));

        Assert.Equal("Tt", orb.GlyphText);
    }

    // --- the orb's face ---

    // The same rule the gateway's portraits follow, now reached from the other
    // registry: the picture becomes the fill and the letters go away. An orb
    // drawing both is unreadable, and an orb drawing neither is the bug this
    // whole file exists to catch.
    [AvaloniaFact]
    public void APersonaPortraitReplacesTheOrbsLetters()
    {
        var sessionId = PublishPersona(Persona(avatar: Portrait()));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Local());

        Assert.False(orb.Glyph.IsVisible, "the letters should give way to the picture");
        Assert.IsType<ImageBrush>(orb.Orb.Fill);
    }

    // A gateway session is not offered a persona even if one is somehow
    // published under its id. Its identity comes from the gateway, and the two
    // registries answering the same orb is precisely the drift SessionIdentity
    // was written to prevent.
    [AvaloniaFact]
    public void AGatewaySessionIgnoresALocalPersonaEntirely()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var sessionId = "openclaw:agent:nova" + Guid.NewGuid().ToString("N")[..8] + ":main";
        LocalPersonas.SetForTests(
            new Dictionary<string, LocalPersona.Persona>
            {
                [sessionId] = Persona(avatar: Portrait()),
            });
        _avatarKeysToClean.Add(LocalPersonas.AvatarKey(sessionId));

        var orb = NewOrb(sessionId);
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            State = "idle",
            Title = "Nova",
            Kind = SessionKind.Direct,
        });

        Assert.Equal("No", orb.GlyphText);
        Assert.True(orb.Glyph.IsVisible);
        Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
    }

    // --- the chat panel's header ---

    // The header wears the name, not the folder. Same argument as the orb's
    // letters and worth its own case because it arrives by a different route:
    // the orb reads the registry through OrbLabel, the panel through
    // SessionIdentity, and the two agreeing is the point of there being one
    // helper rather than two call sites.
    [AvaloniaFact]
    public void TheChatHeaderWearsThePersonasName()
    {
        var sessionId = PublishPersona(Persona());
        _panelsToClean.Add(sessionId);

        var fake = new FakeChatSession(null)
        {
            SessionId = sessionId,
            DisplayName = "haunted-mansion",
        };

        ChatPanel.OpenFor(NewOrb(sessionId), fake);
        Flush();

        Assert.Equal("Leota", ChatPanelTestAccess.Instance!.TitleText.Text);
    }

    // The place half of a two-part display name survives. "who" and "where" are
    // separate lines in this header and a persona only ever answers the first.
    [AvaloniaFact]
    public void ThePersonaRenamesTheHeaderWithoutMovingThePlace()
    {
        var sessionId = PublishPersona(Persona());
        _panelsToClean.Add(sessionId);

        var fake = new FakeChatSession(null)
        {
            SessionId = sessionId,
            DisplayName = "haunted-mansion — mini",
        };

        ChatPanel.OpenFor(NewOrb(sessionId), fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Equal("Leota", panel.TitleText.Text);
        Assert.Equal("mini", panel.SubtitleText.Text);
        Assert.True(panel.SubtitleText.IsVisible);
    }

    // The header's circle takes the persona's picture. Without this the panel
    // would borrow the orb's letters — correct, but a step down from the face
    // the orb beside it is already wearing.
    [AvaloniaFact]
    public void TheChatHeaderWearsThePersonasFace()
    {
        var sessionId = PublishPersona(Persona(avatar: Portrait()));
        _panelsToClean.Add(sessionId);

        var fake = new FakeChatSession(null)
        {
            SessionId = sessionId,
            DisplayName = "haunted-mansion",
        };

        ChatPanel.OpenFor(NewOrb(sessionId), fake);
        FlushRender();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.True(panel.Avatar.IsVisible);
        Assert.IsType<ImageBrush>(panel.Avatar.Fill);
        Assert.False(panel.AvatarEmoji.IsVisible);
    }

    // A persona with a name and no picture goes the *other* way deliberately:
    // it borrows the orb's letters and colour rather than drawing initials on
    // an empty circle. The letters it borrows are the persona's, because the
    // orb is already wearing them — which is the whole reason a name is not
    // treated as a face of its own. See SessionIdentity.Face.DrawsItsOwnCircle.
    [AvaloniaFact]
    public void ANamedPersonaWithNoPictureBorrowsTheOrbsCircle()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var sessionId = PublishPersona(Persona());
        _panelsToClean.Add(sessionId);

        var orb = NewOrb(sessionId);
        orb.UpdateFrom(Local());

        var fake = new FakeChatSession(null)
        {
            SessionId = sessionId,
            DisplayName = "haunted-mansion",
        };

        ChatPanel.OpenFor(orb, fake);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Equal("Le", panel.AvatarEmoji.Text);
        Assert.True(panel.AvatarEmoji.IsVisible);
        Assert.True(panel.Avatar.IsVisible);
        Assert.Equal(orb.OrbColor, ((ISolidColorBrush)panel.Avatar.Fill!).Color);
    }

    // --- the whole way through ---

    // A status file on disk, a CLAUDE.md beside the directory it names, one
    // real scan — and an orb wearing the name out of that file.
    //
    // Every other case here starts from a persona already published, which is
    // the only way to test the drawing in isolation and is also the way to
    // ship a feature where each half works and the two are not connected.
    // ScanAndUpdate is the connection, and it can only run here: it builds
    // OrbWindows, which is why SessionScanTests lives in this assembly and why
    // ApplyPersona's own filesystem cases are in tests/IntegrationTests
    // instead.
    //
    // Shaped like SessionScanTests' own fixtures for the reasons its header
    // gives: this process's pid and a term_program, so nothing in the scan
    // shells out to `claude agents` or to a terminal.
    [AvaloniaFact]
    public void ARealScanGivesAnOrbThePersonaFromItsProjectsClaudeMd()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.ClaudeCodeEnabled = true;

        var project = Path.Combine(Path.GetTempPath(), "cb-persona-scan-ui-" + Guid.NewGuid());
        var statusDir = Path.Combine(Path.GetTempPath(), "cb-persona-scan-dir-" + Guid.NewGuid());
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(statusDir);

        var sessionId = "persona-scan-ui-" + Guid.NewGuid();

        try
        {
            File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "# Notes\n\nHer name is Leota.\n");
            File.WriteAllText(
                Path.Combine(statusDir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cli = "",
                    Title = "cb-persona-scan-ui",
                    Cwd = project,
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

            var manager = new SessionManager(statusDir);
            manager.ScanAndUpdate();

            Assert.Equal("Leota", LocalPersonas.For(sessionId)?.Name);
            Assert.Equal("Le", OrbFor(manager, sessionId).GlyphText);

            // And it goes when the session does. A registry that only ever
            // grew would hold a decoded portrait per session for the life of
            // the process.
            File.Delete(Path.Combine(statusDir, sessionId + ".txt"));
            manager.ScanAndUpdate();

            Assert.Null(LocalPersonas.For(sessionId));
        }
        finally
        {
            LocalPersonas.Forget(sessionId);
            try { Directory.Delete(project, recursive: true); } catch { }
            try { Directory.Delete(statusDir, recursive: true); } catch { }
        }
    }

    // Read rather than widened, the same reasoning SessionScanTests records for
    // reaching the scan's own window table.
    private static OrbWindow OrbFor(SessionManager manager, string sessionId)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (IReadOnlyDictionary<string, OrbWindow>)field!.GetValue(manager)!;
        Assert.True(windows.ContainsKey(sessionId), "the scan should have built an orb for the session");

        return windows[sessionId];
    }

    // --- asking about a session that isn't one ---

    // The three entry points, asked about nothing.
    //
    // Not defensive padding: ChatPanel asks all of these as
    // `_session?.SessionId`, and a panel between Unbind and its next Bind has
    // no session at all. Every one of them has to answer "I don't know" rather
    // than throw, because the alternative is an exception on the UI thread
    // inside a poll tick — and the panel would already be showing the right
    // thing, so nothing on screen would hint at where it came from.
    [AvaloniaFact]
    public void NothingIsKnownAboutASessionThatIsNullOrEmpty()
    {
        Assert.False(SessionIdentity.IsGateway(null));
        Assert.False(SessionIdentity.IsGateway(""));

        Assert.Null(SessionIdentity.NameFor(null));
        Assert.Null(SessionIdentity.NameFor(""));
        Assert.Null(SessionIdentity.LocalNameFor(null));

        Assert.Same(SessionIdentity.Unknown, SessionIdentity.For(null));
        Assert.Same(SessionIdentity.Unknown, SessionIdentity.For(""));
    }

    // A local id nobody has published a persona for. Distinct from the case
    // above: the id is real, the registry simply has nothing under it, which is
    // every orb on a machine with no personas at all.
    [AvaloniaFact]
    public void AnUnknownLocalSessionHasNoIdentityRatherThanAnEmptyOne()
    {
        var sessionId = "local-unknown-" + Guid.NewGuid();

        Assert.Null(SessionIdentity.NameFor(sessionId));
        Assert.Null(SessionIdentity.LocalNameFor(sessionId));
        Assert.Same(SessionIdentity.Unknown, SessionIdentity.For(sessionId));
    }

    // Both halves of the gateway/local split, from the one function, in one
    // case — which is the property SessionIdentity exists for and the one that
    // would break silently if a later change keyed on the wrong thing.
    [AvaloniaFact]
    public void TheGatewayAndLocalHalvesAnswerFromTheirOwnRegistries()
    {
        var agent = "nova" + Guid.NewGuid().ToString("N")[..8];
        var gatewayId = $"openclaw:agent:{agent}:discord:direct:1";
        var localId = PublishPersona(Persona());

        try
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>
                {
                    [agent] = new("Gateway Nova", "✨", null),
                });

            Assert.True(SessionIdentity.IsGateway(gatewayId));
            Assert.Equal("Gateway Nova", SessionIdentity.NameFor(gatewayId));

            // ...and a gateway session never answers the *local* question, so
            // the chat header cannot substitute an agent's name for a room's.
            Assert.Null(SessionIdentity.LocalNameFor(gatewayId));

            Assert.False(SessionIdentity.IsGateway(localId));
            Assert.Equal("Leota", SessionIdentity.NameFor(localId));
            Assert.Equal("Leota", SessionIdentity.LocalNameFor(localId));
        }
        finally
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    // --- the voice ---

    // No persona at all answers null, and null is not silence: it is the
    // user's own global voice. Getting this wrong in the other direction would
    // make every orb on a machine with no personas stop speaking.
    [AvaloniaFact]
    public void ASessionWithNoPersonaHasNoVoiceOfItsOwn()
    {
        var options = new[] { Neural("af_bella"), Neural("bf_isabella") };

        Assert.Null(OrbWindow.VoiceForLocalSpeech("local-none-" + Guid.NewGuid(), options));
    }

    // A persona that names no voice is the same answer by a different route —
    // a name-only CLAUDE.md, which is most of them.
    [AvaloniaFact]
    public void APersonaThatNamesNoVoiceKeepsTheGlobalOne()
    {
        var sessionId = PublishPersona(Persona());

        Assert.Null(OrbWindow.VoiceForLocalSpeech(sessionId, new[] { Neural("af_bella") }));
    }

    // A voice this machine does not have is also the global one. Deliberately
    // not a different kind of default from "named none": a persona written on
    // somebody else's machine must degrade to the user's own setting rather
    // than to silence or to an arbitrary near-match.
    [AvaloniaFact]
    public void AVoiceThisMachineDoesNotHaveFallsBackToTheGlobalOne()
    {
        var sessionId = PublishPersona(Persona(voice: "Cadaverous Baritone"));

        Assert.Null(OrbWindow.VoiceForLocalSpeech(sessionId, new[] { Neural("af_bella") }));
    }

    // The match that matters, and the rate with it. Kokoro's names carry a
    // two-letter prefix nobody writes in prose, so "Bella" in a CLAUDE.md has
    // to reach af_bella — that rule is TextToSpeech.MatchVoiceOption's and is
    // tested there; what this asserts is that the orb's own entry point is
    // wired to it at all.
    [AvaloniaFact]
    public void APersonaVoiceAndRateReachTheOrbsSpeakPath()
    {
        var bella = Neural("af_bella");
        var sessionId = PublishPersona(Persona(voice: "af_bella", rate: 1.3));

        Assert.Equal(bella, OrbWindow.VoiceForLocalSpeech(sessionId, new[] { bella }));
        Assert.Equal(1.3, LocalPersonas.RateForSession(sessionId));
    }

    // The neural engine switched off is the case a persona cannot see coming.
    // TextToSpeech.AllVoiceOptions is already gated by the user's settings, so
    // "af_bella" is simply not on the list, and the fallback has to be the
    // global voice rather than a system voice picked because its name looked
    // close enough.
    [AvaloniaFact]
    public void AKokoroVoiceIsIgnoredWhenTheNeuralEngineIsOff()
    {
        var sessionId = PublishPersona(Persona(voice: "af_bella", rate: 1.3));
        var systemOnly = new[]
        {
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha"),
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "Daniel", "Daniel"),
        };

        Assert.Null(OrbWindow.VoiceForLocalSpeech(sessionId, systemOnly));

        // The rate survives the voice not matching, and is simply never read —
        // the same shape OpenClawSessions.RateForSession has, and for the same
        // reason: a rate is not a property of the voice that was chosen.
        Assert.Equal(1.3, LocalPersonas.RateForSession(sessionId));
    }

    private static TextToSpeech.VoiceOption Neural(string name) =>
        new(TextToSpeech.SpeakEngine.Neural, name, name + " (Kokoro)");
}
