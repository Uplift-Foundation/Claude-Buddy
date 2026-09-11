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
    private readonly List<string> _dirsToClean = new();
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

        // Process-wide for the same reason the persona registry is, and with
        // a sharper consequence: left set, a later class materialises a blend
        // into a directory this one has already deleted; left unset while a
        // blend test runs, it writes into the developer's own voices folder.
        VoiceBlends.SetPathsForTests(null);

        foreach (var directory in _dirsToClean)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
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

    // A picture on disk rather than bytes in the record, which is CB-135's
    // change: a persona now carries the path to its portrait and nothing else,
    // and the decode reads the file. A fixture that named a file which was not
    // there would draw no picture at all — so these write one, in a directory
    // of their own that Dispose takes away again.
    private string PortraitFile(byte r = 0x8A, byte g = 0x6F, byte b = 0xD4)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-persona-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        _dirsToClean.Add(directory);

        var path = Path.Combine(directory, "leota.png");
        File.WriteAllBytes(path, Portrait(r, g, b));
        return path;
    }

    private static LocalPersona.Persona Persona(
        string? name = "Leota", string? voice = null, double? rate = null, string? avatarPath = null)
    {
        var markdown = avatarPath is null
            ? "/tmp/leota/CLAUDE.md"
            : Path.Combine(Path.GetDirectoryName(avatarPath)!, "CLAUDE.md");

        return new(name, voice, rate, avatarPath is null ? null : markdown, avatarPath, new[] { markdown });
    }

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
        var sessionId = PublishPersona(Persona(avatarPath: PortraitFile()));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Local());

        Assert.False(orb.Glyph.IsVisible, "the letters should give way to the picture");
        Assert.IsType<ImageBrush>(orb.Orb.Fill);
    }

    // The other side of CB-135's change, and the one only a file can ask: the
    // persona carries a path now, so a portrait the guards refuse at *decode*
    // time has to leave the orb wearing its letters rather than an empty
    // circle. Nothing about the record says the picture is unusable — the path
    // is set, the file is there — and the refusal happens inside the read.
    [AvaloniaFact]
    public void APortraitTooBigToReadLeavesTheOrbItsLetters()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var oversized = PortraitFile();
        File.WriteAllBytes(oversized, new byte[9 * 1024 * 1024]);

        var sessionId = PublishPersona(Persona(avatarPath: oversized));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Local());

        Assert.True(orb.Glyph.IsVisible);
        Assert.Equal("Le", orb.GlyphText);
        Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
    }

    // A persona whose picture has been deleted since it was resolved is the
    // same story one step further along, and it is not hypothetical: the scan
    // resolves a persona once and hands the same object back until something
    // it watches moves, so there is a window in which the path outlives the
    // file.
    [AvaloniaFact]
    public void APortraitThatHasGoneAwayLeavesTheOrbItsLetters()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var portrait = PortraitFile();
        File.Delete(portrait);

        var sessionId = PublishPersona(Persona(avatarPath: portrait));
        var orb = NewOrb(sessionId);

        orb.UpdateFrom(Local());

        Assert.True(orb.Glyph.IsVisible);
        Assert.Equal("Le", orb.GlyphText);
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
                [sessionId] = Persona(avatarPath: PortraitFile()),
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
        var sessionId = PublishPersona(Persona(avatarPath: PortraitFile()));
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

            var manager = PinnedManager(statusDir);
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

    // CB-140, end to end: a directory name with a space in it, a front-matter
    // persona.md `@`-imported from CLAUDE.md, and — in the same CLAUDE.md — an
    // ordinary `- **Name**: ...` bullet describing a person rather than any
    // persona. Before CB-140 the front-matter file named nobody at all (no
    // `name:` arm) and the absolute `image:` path silently truncated around
    // the space in the directory, so the orb fell through to that bullet and
    // wore the wrong name with no picture. Both defects are fixed in the
    // grammar; this is the proof they reach the screen.
    [AvaloniaFact]
    public void ARealScanWithASpaceInTheProjectDirectoryReadsThePersonaOverTheOrdinaryNameBullet()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.ClaudeCodeEnabled = true;

        var project = Path.Combine(Path.GetTempPath(), "cb persona ui " + Guid.NewGuid());
        var personaDir = Path.Combine(project, ".claude", "persona");
        var statusDir = Path.Combine(Path.GetTempPath(), "cb-persona-scan-space-dir-" + Guid.NewGuid());
        Directory.CreateDirectory(personaDir);
        Directory.CreateDirectory(statusDir);

        var sessionId = "persona-scan-space-ui-" + Guid.NewGuid();
        var picture = Path.Combine(personaDir, "avatar.png");
        File.WriteAllBytes(picture, Portrait());

        try
        {
            File.WriteAllText(
                Path.Combine(project, "CLAUDE.md"),
                "# Notes\n\n@.claude/persona/persona.md\n\n- **Name**: Jordan Casey, MBA / MSc\n");

            File.WriteAllText(
                Path.Combine(personaDir, "persona.md"),
                "---\n" +
                "name: \"Skyler\"\n" +
                "image: \"" + picture + "\"\n" +
                "---\n");

            File.WriteAllText(
                Path.Combine(statusDir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cli = "",
                    Title = "cb-persona-scan-space",
                    Cwd = project,
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

            var manager = PinnedManager(statusDir);
            manager.ScanAndUpdate();

            // The persona's name wins, not the bullet's — the bullet's value
            // is five words carrying a comma and a slash, and NameValue's
            // bound refuses it, so it never even competes.
            Assert.Equal("Skyler", LocalPersonas.For(sessionId)?.Name);

            var orb = OrbFor(manager, sessionId);
            Assert.False(orb.Glyph.IsVisible, "the letters should give way to the picture");
            Assert.IsType<ImageBrush>(orb.Orb.Fill);

            var fake = new FakeChatSession(null)
            {
                SessionId = sessionId,
                DisplayName = "cb-persona-scan-space",
            };
            _panelsToClean.Add(sessionId);
            ChatPanel.OpenFor(orb, fake);
            Flush();

            Assert.Equal("Skyler", ChatPanelTestAccess.Instance!.TitleText.Text);
        }
        finally
        {
            LocalPersonas.Forget(sessionId);
            try { Directory.Delete(project, recursive: true); } catch { }
            try { Directory.Delete(statusDir, recursive: true); } catch { }
        }
    }

    // CB-141, end to end: profile-gen's `claude-md` output mode — the whole
    // persona inside a ```yaml fence, wrapped in `<!-- profile-gen:start -->`
    // and `<!-- profile-gen:end -->` markers — written into a real CLAUDE.md,
    // found by a real scan, reaching a real orb.
    //
    // The grammar is pinned in tests/UnitTests and the resolver seam in
    // tests/IntegrationTests; neither can say the answer arrives on screen,
    // which is the gap this file's own header describes and the one CB-133
    // shipped through. The registry is the specific thing being asserted: a
    // shape the parser now reads has to become a `LocalPersonas` entry and
    // then a portrait on an orb, and the two steps after the parse are the
    // ones nobody has exercised for this shape.
    //
    // Both surfaces are asserted because they fail differently — the orb takes
    // the picture and drops its letters, the chat panel's header takes the
    // name — and a persona that reached one and not the other has been half
    // wired up before.
    [AvaloniaFact]
    public void ARealScanReadsAProfileGenEmbeddedBlockAllTheWayToTheOrb()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.ClaudeCodeEnabled = true;

        var project = Path.Combine(Path.GetTempPath(), "cb-persona-embedded-ui-" + Guid.NewGuid());
        var pictures = Path.Combine(project, "profiles", "aurora-vance");
        var statusDir = Path.Combine(Path.GetTempPath(), "cb-persona-embedded-dir-" + Guid.NewGuid());
        Directory.CreateDirectory(pictures);
        Directory.CreateDirectory(statusDir);

        var sessionId = "persona-embedded-ui-" + Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(pictures, "aurora-vance.png"), Portrait());

        try
        {
            // profile-gen's embedded block as its renderer emits it, appended
            // to a CLAUDE.md that already had prose in it — which is what the
            // mode does to an existing file rather than what it writes into an
            // empty one.
            File.WriteAllLines(
                Path.Combine(project, "CLAUDE.md"),
                new[]
                {
                    "# Notes",
                    "",
                    "Notes for Claude Code.",
                    "",
                    "<!-- profile-gen:start slug=aurora-vance -->",
                    "### Aurora Vance",
                    "",
                    "![Aurora Vance](profiles/aurora-vance/aurora-vance.png)",
                    "",
                    "```yaml",
                    "schema_version: 1",
                    "name: \"Aurora Vance\"",
                    "slug: \"aurora-vance\"",
                    "image: \"profiles/aurora-vance/aurora-vance.png\"",
                    "",
                    "voice: \"af_bella\"",
                    "",
                    "nsfw: false",
                    "```",
                    "",
                    "**Personality:** Warm, precise, and a little wry.",
                    "",
                    "<!-- profile-gen:end slug=aurora-vance -->",
                });

            File.WriteAllText(
                Path.Combine(statusDir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cli = "",
                    Title = "cb-persona-embedded",
                    Cwd = project,
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

            var manager = new SessionManager(statusDir);
            manager.ScanAndUpdate();

            // The registry first: the scan published a persona for this
            // session, with all three fields the fenced block declared.
            var persona = LocalPersonas.For(sessionId);
            Assert.NotNull(persona);
            Assert.Equal("Aurora Vance", persona!.Name);
            Assert.Equal("af_bella", persona.Voice);
            Assert.Equal(
                Path.Combine(pictures, "aurora-vance.png"), persona.AvatarPath);

            // Then the orb, which is the only surface that can say the picture
            // was decoded rather than merely named.
            var orb = OrbFor(manager, sessionId);
            Assert.False(orb.Glyph.IsVisible, "the letters should give way to the picture");
            Assert.IsType<ImageBrush>(orb.Orb.Fill);

            // And the header of the panel that opens under it, which is where
            // the *name* shows up rather than the picture.
            var fake = new FakeChatSession(null)
            {
                SessionId = sessionId,
                DisplayName = "cb-persona-embedded",
            };
            _panelsToClean.Add(sessionId);
            ChatPanel.OpenFor(orb, fake);
            Flush();

            Assert.Equal("Aurora Vance", ChatPanelTestAccess.Instance!.TitleText.Text);
        }
        finally
        {
            LocalPersonas.Forget(sessionId);
            try { Directory.Delete(project, recursive: true); } catch { }
            try { Directory.Delete(statusDir, recursive: true); } catch { }
        }
    }

    // The paired refusal, and it is the half that makes the case above a
    // measurement rather than a hope. The *same* CLAUDE.md with the two marker
    // lines deleted and nothing else touched: the fence, the keys and the
    // picture on disk are all still there, so anything the orb wears now came
    // from the marker and not from the yaml.
    //
    // An orb falling back to its folder letters is what "no persona" looks like
    // on screen, which is why that is what is asserted rather than a null in
    // the registry alone.
    [AvaloniaFact]
    public void ARealScanIgnoresTheSameYamlBlockWhenNothingMarksItAsAPersona()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.ClaudeCodeEnabled = true;

        var project = Path.Combine(Path.GetTempPath(), "cb-persona-unmarked-ui-" + Guid.NewGuid());
        var pictures = Path.Combine(project, "profiles", "aurora-vance");
        var statusDir = Path.Combine(Path.GetTempPath(), "cb-persona-unmarked-dir-" + Guid.NewGuid());
        Directory.CreateDirectory(pictures);
        Directory.CreateDirectory(statusDir);

        var sessionId = "persona-unmarked-ui-" + Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(pictures, "aurora-vance.png"), Portrait());

        try
        {
            File.WriteAllLines(
                Path.Combine(project, "CLAUDE.md"),
                new[]
                {
                    "# Notes",
                    "",
                    "A persona is declared like this:",
                    "",
                    "```yaml",
                    "schema_version: 1",
                    "name: \"Aurora Vance\"",
                    "slug: \"aurora-vance\"",
                    "image: \"profiles/aurora-vance/aurora-vance.png\"",
                    "",
                    "voice: \"af_bella\"",
                    "",
                    "nsfw: false",
                    "```",
                });

            File.WriteAllText(
                Path.Combine(statusDir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cli = "",
                    Title = "cb-persona-unmarked",
                    Cwd = project,
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

            var manager = new SessionManager(statusDir);
            manager.ScanAndUpdate();

            Assert.Null(LocalPersonas.For(sessionId)?.Name);

            var orb = OrbFor(manager, sessionId);
            Assert.True(orb.Glyph.IsVisible, "an unmarked yaml example must not dress an orb");
            Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            LocalPersonas.Forget(sessionId);
            try { Directory.Delete(project, recursive: true); } catch { }
            try { Directory.Delete(statusDir, recursive: true); } catch { }
        }
    }

    // The other half: take the persona file away, and the bullet still must
    // not win. The tree is the ordinary case CB-133 always meant to cover —
    // no front matter at all — asserted here rather than assumed, because the
    // fix that bounds the bullet arm is exactly what makes this true now and
    // was not what made it true before.
    [AvaloniaFact]
    public void ARealScanWithNoPersonaFileLeavesTheOrbItsFolderLetters()
    {
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.ClaudeCodeEnabled = true;

        var project = Path.Combine(Path.GetTempPath(), "cb persona ui none " + Guid.NewGuid());
        var statusDir = Path.Combine(Path.GetTempPath(), "cb-persona-scan-none-dir-" + Guid.NewGuid());
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(statusDir);

        var sessionId = "persona-scan-none-ui-" + Guid.NewGuid();

        try
        {
            File.WriteAllText(
                Path.Combine(project, "CLAUDE.md"),
                "# Notes\n\n- **Name**: Jordan Casey, MBA / MSc\n");

            File.WriteAllText(
                Path.Combine(statusDir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cli = "",
                    Title = "cb-persona-scan-none",
                    Cwd = project,
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

            var manager = PinnedManager(statusDir);
            manager.ScanAndUpdate();

            Assert.Null(LocalPersonas.For(sessionId)?.Name);

            var orb = OrbFor(manager, sessionId);
            Assert.True(orb.Glyph.IsVisible);
            Assert.IsNotType<ImageBrush>(orb.Orb.Fill);
        }
        finally
        {
            LocalPersonas.Forget(sessionId);
            try { Directory.Delete(project, recursive: true); } catch { }
            try { Directory.Delete(statusDir, recursive: true); } catch { }
        }
    }

    // A scan whose user-level config directories are this fixture's answer and
    // not the machine's — CB-143's seam, used here for the second reason that
    // ticket found. The scan reaches ~/.claude for a Claude Code session, so
    // ARealScanWithNoPersonaFileLeavesTheOrbItsFolderLetters was asserting that
    // *the person running the suite* had not written a persona into their own
    // config directory. That held on every machine it has run on so far and is
    // not a property of this test.
    private static SessionManager PinnedManager(string statusDir) =>
        new(statusDir, null, userConfigDirs: () => Array.Empty<string>());

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

    // --- the voice, written as a mixture (CB-136) ---

    // A tiny voice file, and a scratch pair of directories for the blend to
    // be read out of and written into. Tiny because this suite is asking
    // whether the orb's entry point is wired to the blend at all, not whether
    // the arithmetic is right — that is NumpyVoiceTests', over the real
    // shape, and VoiceBlendFileTests', over real files.
    private string VoicesDirectory(params (string Name, float Value)[] voices)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-blend-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        _dirsToClean.Add(directory);

        foreach (var (name, value) in voices)
        {
            var tensor = new NumpyVoices.Tensor(new[] { 4 }, Enumerable.Repeat(value, 4).ToArray());
            File.WriteAllBytes(Path.Combine(directory, name + ".npy"), NumpyVoices.Write(tensor));
        }

        VoiceBlends.SetPathsForTests(new VoiceBlends.Paths(new[] { directory }, directory));
        return directory;
    }

    // The whole of CB-136 as the orb meets it: this repository's own persona
    // line, published the way the scan publishes one, reaching the speak
    // path as a voice the engine can be handed.
    [AvaloniaFact]
    public void ABlendedPersonaVoiceReachesTheOrbsSpeakPathAsAMaterialisedVoice()
    {
        var directory = VoicesDirectory(("af_sky", 1f), ("af_nicole", 3f));
        var sessionId = PublishPersona(Persona(voice: "50% sky and 50% nicole"));

        var option = OrbWindow.VoiceForLocalSpeech(
            sessionId, new[] { Neural("af_sky"), Neural("af_nicole"), Neural("af_bella") });

        Assert.NotNull(option);
        Assert.Equal(TextToSpeech.SpeakEngine.Neural, option!.Engine);
        Assert.Equal("af_blend_sky50-nicole50", option.Name);
        Assert.Contains("blend", option.Label, StringComparison.OrdinalIgnoreCase);

        // The name is not a label the orb invented — it is a file the engine
        // will find, in the directory the engine is passed as --user-voices.
        Assert.True(File.Exists(Path.Combine(directory, option.Name + ".npy")));
    }

    // The neural engine switched off, which is the case a persona cannot see
    // coming. AllVoiceOptions offers no neural voices then, so there is
    // nothing for a part to resolve to — and the answer has to be the user's
    // global voice rather than silence, and rather than a system voice picked
    // because one of the words looked close enough.
    [AvaloniaFact]
    public void ABlendIsIgnoredWhenTheNeuralEngineIsOff()
    {
        var directory = VoicesDirectory(("af_sky", 1f), ("af_nicole", 3f));
        var sessionId = PublishPersona(Persona(voice: "50% sky and 50% nicole", rate: 1.1));

        var systemOnly = new[]
        {
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha"),
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "Sky", "Sky"),
        };

        Assert.Null(OrbWindow.VoiceForLocalSpeech(sessionId, systemOnly));

        // ...and nothing was built. A blend that cannot be spoken must not
        // leave a file behind for a later run to find and reuse.
        Assert.Empty(Directory.GetFiles(directory, "*blend*"));

        // The rate survives the voice not matching and is simply never read,
        // the same as for a single voice that did not match.
        Assert.Equal(1.1, LocalPersonas.RateForSession(sessionId));
    }

    // A mixture naming a voice this machine has not got is the global voice
    // too — the whole blend, not the half that resolved. Speaking a two-voice
    // mixture as one of its halves would be a voice nobody chose, and it
    // would be indistinguishable from the blend having worked.
    [AvaloniaFact]
    public void AMixtureWithOnePartThisMachineLacksFallsBackWholesale()
    {
        var directory = VoicesDirectory(("af_sky", 1f));
        var sessionId = PublishPersona(Persona(voice: "50% sky and 50% nicole"));

        Assert.Null(OrbWindow.VoiceForLocalSpeech(
            sessionId, new[] { Neural("af_sky"), Neural("af_bella") }));

        Assert.Empty(Directory.GetFiles(directory, "*blend*"));
    }

    // ...and the remote half of the same seam, because a workspace
    // IDENTITY.md is read by the same parser and now means the same thing. A
    // blend honoured on one orb and ignored on the other is exactly the drift
    // PersonaMarkdown was lifted out of OpenClawWorkspaceIdentity to prevent.
    [AvaloniaFact]
    public void AGatewayAgentsBlendReachesTheRemoteSpeakPathToo()
    {
        VoicesDirectory(("af_sky", 1f), ("af_nicole", 3f));

        const string sessionId = "openclaw:agent:blend:blend";
        OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>
        {
            ["blend"] = new("Jennifer", null, null, "50% sky and 50% nicole"),
        });

        try
        {
            var option = OrbWindow.VoiceForRemoteSpeech(
                sessionId, new[] { Neural("af_sky"), Neural("af_nicole") });

            Assert.Equal("af_blend_sky50-nicole50", option!.Name);
        }
        finally
        {
            OpenClawSessions.SetIdentitiesForTests(
                new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    private static TextToSpeech.VoiceOption Neural(string name) =>
        new(TextToSpeech.SpeakEngine.Neural, name, name + " (Kokoro)");
}
