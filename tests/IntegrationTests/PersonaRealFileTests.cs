using System.Reflection;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-135's own use case, against a real tree — and the reason this file exists
// rather than another row in a grammar table.
//
// **Every test CB-133 shipped was green while this repository's own persona
// file resolved to nothing.** Each of them wrote a markdown file designed to
// make something happen and then asserted that it had. That is a necessary
// suite and it is not a sufficient one: it can only ever say the parser does
// what the parser was written to do, never that a file somebody actually wrote
// is read. The same gap let the portrait-watch defect ship.
//
// So the fixture here is the real `.claude/PERSONA.MD` from this repository,
// line for line, reached the way the app reaches it: through the `@` import on
// line 12 of CLAUDE.md, with the uppercase `.MD` extension it really has, out
// of a dot-directory. Nothing about it is arranged to be easy to parse.
//
// The picture is generated rather than committed — `cto.png` is two megabytes
// and a repository is a bad place to keep a copy of one — but its *size* is
// the real file's, because that number is the whole of the second half of this
// ticket: 2,039,952 bytes against the 2 MiB cap CB-133 shipped is 57 KB of
// headroom, and nothing anywhere said so.
[Collection("LogDir")]
public class PersonaRealFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-real-" + Guid.NewGuid());

    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-log-" + Guid.NewGuid());

    private readonly string? _logWas;

    public PersonaRealFileTests()
    {
        Directory.CreateDirectory(_root);

        _logWas = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logDir);

        // The dedupe is process-wide and never expires by design, so a message
        // another case already wrote would be silently skipped here.
        PersonaLog.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logWas);
        PersonaLog.ResetForTests();

        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { }
    }

    // The bytes of `.claude/cto.png` in this repository, to the byte.
    private const int RealPortraitBytes = 2_039_952;

    // A real PNG header on the front of a file of a chosen size. The header
    // matters because the app decodes these for real; the size matters because
    // the cap is what this half of the ticket is about.
    private static byte[] PortraitOf(int bytes)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var padded = new byte[bytes];
        Array.Copy(png, padded, Math.Min(png.Length, bytes));
        return padded;
    }

    // The repository's own persona file, verbatim — including the two trailing
    // spaces on the third line, which are a Markdown hard break and are exactly
    // the kind of thing a fixture written from memory loses.
    private const string RealPersonaFile =
        "# Claude Buddy Persona\n" +
        "\n" +
        "I'm a female AI Architect who built this cute little Claudy Buddy Agentic AI harness.  \n" +
        "\n" +
        "I'm the CTO in charge of the project and I give the orders.\n" +
        "\n" +
        "## Attributes\n" +
        "\n" +
        "Name Jennifer\n" +
        "Profile Photo cto.png\n" +
        "Voice is 50% sky and 50% nicole\n";

    // The tree this repository actually has: a CLAUDE.md whose twelfth line is
    // the import, the persona file in `.claude/` with an uppercase extension,
    // and the picture beside it.
    private string WriteTheRealTree(int portraitBytes = RealPortraitBytes, string picture = "cto.png")
    {
        var project = Path.Combine(_root, "Claude-Buddy");
        var dotClaude = Path.Combine(project, ".claude");
        Directory.CreateDirectory(dotClaude);

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n\nNotes for Claude Code.\n\n@.claude/PERSONA.MD\n\n## How a feature gets built\n");

        File.WriteAllText(
            Path.Combine(dotClaude, "PERSONA.MD"),
            RealPersonaFile.Replace("cto.png", picture));

        File.WriteAllBytes(Path.Combine(dotClaude, picture), PortraitOf(portraitBytes));

        return project;
    }

    private static LocalPersona.Persona Resolve(string project) =>
        LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

    private string LogText() =>
        File.Exists(PersonaLog.Path_) ? File.ReadAllText(PersonaLog.Path_) : "";

    // The lines about one picture, rather than the whole file.
    //
    // CLAUDE_BUDDY_LOG_DIR is one process-wide variable and the classes that
    // are *not* in this collection go on resolving personas of their own while
    // these run — LocalPersonaFilesTests refuses a picture on purpose a dozen
    // times over. Their lines land in this scratch directory too, so "the log
    // is empty" is not a claim this suite can make and "the log says this about
    // my file" is. Each case names its own picture for exactly that reason.
    private string[] LinesAbout(string picture) =>
        LogText()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(picture, StringComparison.Ordinal))
            .ToArray();

    // --- the ticket's own use case ----------------------------------------

    // **The joint end-to-end criterion CB-135 and CB-136 share, in one test.**
    //
    // CB-135 landed first and asserted its own two thirds; the ticket says
    // whichever lands second owns the whole of it, and CB-136 is second. So
    // all three fields are asserted here against one tree — the real file's
    // lines verbatim, reached through the real `@.claude/PERSONA.MD` import,
    // with the picture beside it — rather than three tests each arranging the
    // thing it is about.
    //
    // That distinction is the reason the criterion was written at all. Every
    // test CB-133 shipped was green while this exact file produced nothing,
    // because each of them wrote a markdown file designed to make something
    // happen. A per-line grammar table is necessary and it is not sufficient:
    // the seam CB-135 fell through was between two tickets' bounds, and no
    // test of either half could have been standing at it.
    //
    // The voice is asserted as far as this suite can honestly take it: the
    // mixture, its parts and its shares, resolved against an injected list of
    // what a machine with the engine on would offer. Materialising it needs a
    // real `.npy` on a disk, which is VoiceBlendFileTests', and hearing it is
    // Warren's.
    [Fact]
    public void TheRepositorysOwnPersonaFileResolvesToJenniferHerPhotoAndHerBlend()
    {
        var project = WriteTheRealTree();

        var persona = Resolve(project);

        Assert.Equal("Jennifer", persona.Name);

        Assert.Equal(Path.Combine(project, ".claude", "cto.png"), persona.AvatarPath);
        var bytes = PersonaFiles.ReadAvatarFile(persona.AvatarPath!);
        Assert.NotNull(bytes);
        Assert.Equal(RealPortraitBytes, bytes!.Length);

        // The voice half, which resolved to nothing at all until CB-136 moved
        // the bounds this line was refused by.
        Assert.Equal("50% sky and 50% nicole", persona.Voice);

        var installed = new[]
        {
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.Neural, "af_sky", "af_sky (Kokoro)"),
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.Neural, "af_nicole", "af_nicole (Kokoro)"),
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.Neural, "af_bella", "af_bella (Kokoro)"),
            new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha (system)"),
        };

        var blend = VoiceBlend.Parse(persona.Voice);
        Assert.NotNull(blend);

        var resolved = VoiceBlend.Resolve(blend!, installed);
        Assert.NotNull(resolved);
        Assert.Equal(new[] { "af_sky", "af_nicole" }, resolved!.Parts.Select(p => p.Option.Name));
        Assert.Equal(new[] { 0.5, 0.5 }, resolved.Parts.Select(p => p.Weight));
        Assert.Equal("af_blend_sky50-nicole50", resolved.Name);
    }

    // CB-135's half of the criterion above, kept as its own case: it is the
    // one that fails if the name or the picture regresses while the voice
    // still works, and a single test asserting all three cannot say which
    // third broke.
    [Fact]
    public void TheRepositorysOwnPersonaFileResolvesToJenniferAndHerPhoto()
    {
        var project = WriteTheRealTree();

        var persona = Resolve(project);

        Assert.Equal("Jennifer", persona.Name);
        Assert.Equal(Path.Combine(project, ".claude", "cto.png"), persona.AvatarPath);
        Assert.Equal(Path.Combine(project, ".claude", "PERSONA.MD"), persona.AvatarSource);

        // Non-zero bytes, read back through the same guard the decode uses —
        // "a path was set" and "a picture can be drawn" are two different
        // claims and the second is the one a user cares about.
        var bytes = PersonaFiles.ReadAvatarFile(persona.AvatarPath!);
        Assert.NotNull(bytes);
        Assert.Equal(RealPortraitBytes, bytes!.Length);

        // The import is what carried it: the persona file is in the read list
        // even though nothing walks into `.claude/PERSONA.MD` by name.
        Assert.Contains(Path.Combine(project, ".claude", "PERSONA.MD"), persona.Files);
    }

    // The picture that started this, at the size it really is. Under the old
    // 2 MiB cap it cleared by 57 KB and nothing said so either way; the point
    // of naming the number is that a portrait one phone photo larger was
    // silently invisible.
    [Fact]
    public void TheRealPortraitIsUnderTheRaisedCapWithRoomToSpare()
    {
        Assert.True(RealPortraitBytes < PersonaFiles.MaxAvatarBytes);
        Assert.True(RealPortraitBytes > 2 * 1024 * 1024 - 64 * 1024);

        var persona = Resolve(WriteTheRealTree(picture: "under-cap.png"));

        Assert.NotNull(persona.AvatarPath);
        Assert.Empty(LinesAbout("under-cap.png"));
    }

    [Fact]
    public void AThreeMebibytePortraitIsAccepted()
    {
        var persona = Resolve(WriteTheRealTree(3 * 1024 * 1024, "three-mib.png"));

        Assert.NotNull(persona.AvatarPath);
        Assert.Equal(3 * 1024 * 1024, PersonaFiles.ReadAvatarFile(persona.AvatarPath!)!.Length);
        Assert.Empty(LinesAbout("three-mib.png"));
    }

    // The refusal, and the line that says so. Both halves matter: before this
    // ticket an oversized portrait was dropped in silence and looked exactly
    // like a persona that named no picture at all. Nine mebibytes was over
    // CB-135's 8 MiB cap and is the size CB-146 raised the ceiling past — see
    // PersonaFiles.MaxAvatarBytes for why the ceiling moved rather than the
    // picture — so this now has to sit over the new 16 MiB cap instead.
    [Fact]
    public void ASeventeenMebibytePortraitIsRefusedAndSaysWhyInTheLog()
    {
        var persona = Resolve(WriteTheRealTree(17 * 1024 * 1024, "oversized.png"));

        Assert.Equal("Jennifer", persona.Name);
        Assert.Null(persona.AvatarPath);

        var line = Assert.Single(LinesAbout("oversized.png"));
        Assert.Contains("too large", line);
        Assert.Contains("17,825,792 bytes", line);
        Assert.Contains("16,777,216 bytes", line);
    }

    [Fact]
    public void APictureThatIsNotThereIsLoggedAsUnreadable()
    {
        var project = WriteTheRealTree(picture: "absent.png");
        File.Delete(Path.Combine(project, ".claude", "absent.png"));

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Contains("unreadable", Assert.Single(LinesAbout("absent.png")));
    }

    [UnixFact]
    public void APictureThisProcessMayNotOpenIsLoggedAsUnreadable()
    {
        var project = WriteTheRealTree(picture: "unreadable.png");
        File.SetUnixFileMode(Path.Combine(project, ".claude", "unreadable.png"), UnixFileMode.None);

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Contains("unreadable", Assert.Single(LinesAbout("unreadable.png")));
    }

    // The other two categories, at the resolving end, so every reason a
    // reader can meet in persona.log is one a test has actually produced.
    [SymlinkFact]
    public void APictureReachedThroughALinkOutOfItsDirectoryIsLoggedAsEscapingTheRoot()
    {
        var project = WriteTheRealTree(picture: "linked.png");
        var elsewhere = Path.Combine(_root, "elsewhere.png");
        File.WriteAllBytes(elsewhere, PortraitOf(1024));

        var picture = Path.Combine(project, ".claude", "linked.png");
        File.Delete(picture);
        File.CreateSymbolicLink(picture, elsewhere);

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Contains("escapes root", Assert.Single(LinesAbout("linked.png")));
    }

    // **CB-140 inverted this test, on purpose.** Written as a bullet, because
    // that used to be the only shape that could carry a rooted path this far —
    // the prose arm and the colon-less arm refused one on sight and the
    // explicit bullet grammar did not — so the filesystem's own refusal was
    // the one that fired, and it fired under AvatarRejection.Rooted: a string
    // beginning with a separator, refused before anybody asked where it
    // resolved to.
    //
    // That string-level rule is gone, from both PersonaMarkdown and
    // PersonaFiles (§A). What decides an absolute path now is exactly what
    // decides a relative one: does it canonicalise inside the directory of
    // the markdown that named it. This fixture's absolute path names
    // `rooted.png`, which `WriteTheRealTree` already wrote *beside*
    // `PERSONA.MD` — the same directory, by construction — so it resolves,
    // silently, the same as `- Profile photo: rooted.png` would have. This is
    // the shape a profile generator needs: it writes an absolute path because
    // the persona file is `@`-imported from an arbitrary directory and a
    // relative one would not survive that, and the path it writes names a
    // file right beside itself.
    [Fact]
    public void AnAbsolutePicturePathInsideTheNamingFilesOwnRootResolves()
    {
        var project = WriteTheRealTree(picture: "rooted.png");
        var absolute = Path.Combine(project, ".claude", "rooted.png");

        File.WriteAllText(
            Path.Combine(project, ".claude", "PERSONA.MD"),
            "## Attributes\n\n- Profile photo: " + absolute + "\n");

        var persona = Resolve(project);

        Assert.Equal(absolute, persona.AvatarPath);
        Assert.Empty(LinesAbout("rooted.png"));
    }

    // The negative control that proves the guard above is still a guard: an
    // absolute path to a real, readable file is refused when that file is
    // *not* inside either candidate root — not the naming file's own
    // directory (`.claude`), and, since CB-147, not the workspace root
    // (`project`) either. The file sits a level *above* `project` itself, in
    // `_root`, so this is a value D3 says must still be refused exactly as
    // before: escaping both roots is escaping both roots, however many of
    // them there now are. (CB-147 moved the file this test writes from
    // directly inside `project` to _root — see
    // AnAbsolutePathInsideTheWorkspaceButOutsideTheNamingFilesDirectoryIsNowRead
    // just below for what a path in that in-between place resolves to now.)
    [Fact]
    public void AnAbsolutePicturePathOutsideTheNamingFilesRootIsRefused()
    {
        var project = WriteTheRealTree(picture: "inside.png");
        var sibling = Path.Combine(_root, "sibling-9214.png");
        File.WriteAllBytes(sibling, PortraitOf(1024));

        File.WriteAllText(
            Path.Combine(project, ".claude", "PERSONA.MD"),
            "## Attributes\n\n- Profile photo: " + sibling + "\n");

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Contains("escapes root", Assert.Single(LinesAbout("sibling-9214.png")));
    }

    // The same negative control, with a space in the sibling directory's own
    // name — the shape that used to defeat the rooted-path guard before it
    // ever reached this check. Containment refuses it exactly the same way
    // whether or not a space is involved, which is the point: the space was
    // only ever a problem for the string-level rule this ticket removed. As
    // above, the sibling now sits outside `project` entirely rather than
    // merely outside `.claude`, so it escapes both CB-147 candidate roots
    // rather than only the first.
    [Fact]
    public void AnAbsolutePicturePathOutsideTheRootWithASpaceInItIsRefused()
    {
        var project = WriteTheRealTree(picture: "inside-space.png");
        var siblingDir = Path.Combine(_root, "sibling with space");
        Directory.CreateDirectory(siblingDir);
        var sibling = Path.Combine(siblingDir, "outside-4471.png");
        File.WriteAllBytes(sibling, PortraitOf(1024));

        File.WriteAllText(
            Path.Combine(project, ".claude", "PERSONA.MD"),
            "## Attributes\n\n- Profile photo: " + sibling + "\n");

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Contains("escapes root", Assert.Single(LinesAbout("outside-4471.png")));
    }

    // CB-147, D4: the deliberate widening, stated as its own test rather than
    // left as a side effect discovered by whoever next edits the two
    // negative controls above. This is the exact value those two tests used
    // to write — an absolute path directly inside `project` but outside the
    // naming file's own directory (`project/.claude`) — and before this
    // ticket it was refused. It is accepted now, because `project` is the
    // workspace root and Path.Combine returns an absolute second argument
    // unchanged, so the value resolves to the same file under either
    // candidate root and differs only in which one's containment it passes.
    // The ticket's non-goal is "not changing anything about absolute-path
    // handling" — read as CB-140's IsWithin-only rule staying put, which it
    // does; this is a new root being tried, not a weaker check being applied
    // to the roots that already existed.
    [Fact]
    public void AnAbsolutePathInsideTheWorkspaceButOutsideTheNamingFilesDirectoryIsNowRead()
    {
        var project = WriteTheRealTree(picture: "inside.png");
        var workspaceRelative = Path.Combine(project, "workspace-relative-6650.png");
        File.WriteAllBytes(workspaceRelative, PortraitOf(1024));

        File.WriteAllText(
            Path.Combine(project, ".claude", "PERSONA.MD"),
            "## Attributes\n\n- Profile photo: " + workspaceRelative + "\n");

        var persona = Resolve(project);

        Assert.Equal(workspaceRelative, persona.AvatarPath);
        Assert.Empty(LinesAbout("workspace-relative-6650.png"));
    }

    // CB-146's own reason to exist. This exact number — 8,391,801 — is the
    // real animated persona CB-140 measured against the old 8 MiB cap
    // (`PersonaFiles.MaxAvatarBytes` = 8,388,608 then) and refused, 3,193
    // bytes over; that file named no still, so the refusal cost the persona
    // its picture entirely rather than falling back to a lesser one. CB-146
    // raised the cap to 16 MiB specifically so this file is read rather than
    // refused — see PersonaFiles.MaxAvatarBytes for why the resident-cost
    // argument that kept the old cap in place was never actually about this
    // constant. What was a refusal test is now the admission it should
    // always have been: the exact bytes come back and nothing is logged.
    [Fact]
    public void TheRealAnimatedPersonaAtItsExactSizeIsReadAndNotRefused()
    {
        var persona = Resolve(WriteTheRealTree(8_391_801, "real-animated.gif"));

        Assert.NotNull(persona.AvatarPath);
        var bytes = PersonaFiles.ReadAvatarFile(persona.AvatarPath!);
        Assert.NotNull(bytes);
        Assert.Equal(8_391_801, bytes!.Length);

        Assert.Empty(LinesAbout("real-animated.gif"));
    }

    // The boundary itself, both sides — CB-146 is a file that missed the old
    // one by 3,193 bytes, so the check in ReadAvatarFile
    // (`info.Length > MaxAvatarBytes`) is worth pinning exactly rather than
    // trusting by inspection. A file of precisely the cap is `<=`, not `>`,
    // so it has to be read and to log nothing.
    [Fact]
    public void APictureExactlyAtTheCapIsReadAndLogsNothing()
    {
        var persona = Resolve(WriteTheRealTree((int)PersonaFiles.MaxAvatarBytes, "at-cap.png"));

        Assert.NotNull(persona.AvatarPath);
        var bytes = PersonaFiles.ReadAvatarFile(persona.AvatarPath!);
        Assert.NotNull(bytes);
        Assert.Equal(PersonaFiles.MaxAvatarBytes, bytes!.Length);

        Assert.Empty(LinesAbout("at-cap.png"));
    }

    // One byte past the same boundary is enough to flip `>` and refuse, and
    // the refusal names both the file's size and the cap it measured against.
    [Fact]
    public void APictureOneByteOverTheCapIsRefusedAndSaysWhyInTheLog()
    {
        var persona = Resolve(WriteTheRealTree((int)PersonaFiles.MaxAvatarBytes + 1, "over-cap.png"));

        Assert.Null(persona.AvatarPath);

        var line = Assert.Single(LinesAbout("over-cap.png"));
        Assert.Contains("too large", line);
        Assert.Contains("16,777,217 bytes", line);
        Assert.Contains("16,777,216 bytes", line);
    }

    // --- the bytes nobody keeps -------------------------------------------

    // The structural half of the amendment, and it is written to fail if
    // anybody reintroduces retention later rather than to describe today's
    // code. A persona is held per session for the life of the session, and two
    // sessions in one repository are two entries by design — so a byte[] on
    // this record is one copy of the same portrait per agent, on a machine that
    // routinely runs twenty or thirty of them.
    [Fact]
    public void NoPersonaInTheRegistryRetainsThePicturesBytes()
    {
        var project = WriteTheRealTree();
        var sessionId = "persona-real-" + Guid.NewGuid();

        LocalPersonas.Set(sessionId, Resolve(project));

        try
        {
            var persona = LocalPersonas.For(sessionId);
            Assert.NotNull(persona);
            Assert.NotNull(persona!.AvatarPath);

            var type = persona.GetType();
            const BindingFlags Members =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var retained = type.GetProperties(Members)
                .Select(member => (member.Name, Type: member.PropertyType))
                .Concat(type.GetFields(Members).Select(member => (member.Name, Type: member.FieldType)))
                .Where(member => typeof(IEnumerable<byte>).IsAssignableFrom(member.Type))
                .Select(member => member.Name)
                .ToList();

            Assert.Empty(retained);

            // ...and the picture is still perfectly drawable, which is what
            // makes the assertion above about *retention* rather than about
            // there being no picture.
            Assert.Equal(
                RealPortraitBytes,
                OpenClawAvatars.ForFile(LocalPersonas.AvatarKey(sessionId), persona.AvatarPath!) is null
                    ? PersonaFiles.ReadAvatarFile(persona.AvatarPath!)!.Length
                    : RealPortraitBytes);
        }
        finally
        {
            LocalPersonas.Forget(sessionId);
        }
    }

    // --- reading the picture again, at decode time -------------------------

    [Fact]
    public void TheDecodeTimeReadRefusesAFileThatOutgrewTheCapAfterwards()
    {
        var project = WriteTheRealTree(picture: "grew.png");
        var picture = Resolve(project).AvatarPath!;

        // 9 MiB outgrew the 8 MiB cap CB-135 shipped; it does not outgrow
        // CB-146's 16 MiB one, so this now has to grow past that instead.
        File.WriteAllBytes(picture, PortraitOf(17 * 1024 * 1024));

        Assert.Null(PersonaFiles.ReadAvatarFile(picture));
        Assert.Contains("too large", Assert.Single(LinesAbout("grew.png")));
    }

    [Fact]
    public void TheDecodeTimeReadRefusesAnEmptyFile()
    {
        var project = WriteTheRealTree(picture: "emptied.png");
        var picture = Resolve(project).AvatarPath!;

        File.WriteAllBytes(picture, Array.Empty<byte>());

        Assert.Null(PersonaFiles.ReadAvatarFile(picture));
        Assert.Contains("unreadable", Assert.Single(LinesAbout("emptied.png")));
    }

    [Fact]
    public void TheDecodeTimeReadRefusesAFileThatHasGoneAway()
    {
        var project = WriteTheRealTree(picture: "vanished.png");
        var picture = Resolve(project).AvatarPath!;

        File.Delete(picture);

        Assert.Null(PersonaFiles.ReadAvatarFile(picture));
        Assert.Contains("unreadable", Assert.Single(LinesAbout("vanished.png")));
    }

    // The one guard the decode-time read can still make about *where* the file
    // is: the containment proof was made against the directory of the markdown
    // that named it, and what was carried forward is the canonical file that
    // proof admitted. A link put there afterwards resolves somewhere else, the
    // two stop matching, and the read is refused rather than following it.
    [SymlinkFact]
    public void TheDecodeTimeReadRefusesAPathThatHasBecomeALinkElsewhere()
    {
        var project = WriteTheRealTree(picture: "swapped.png");
        var picture = Resolve(project).AvatarPath!;

        var elsewhere = Path.Combine(_root, "elsewhere.png");
        File.WriteAllBytes(elsewhere, PortraitOf(1024));

        File.Delete(picture);
        File.CreateSymbolicLink(picture, elsewhere);

        Assert.True(File.Exists(picture));
        Assert.Null(PersonaFiles.ReadAvatarFile(picture));
        Assert.Contains("escapes root", Assert.Single(LinesAbout("swapped.png")));
    }

    [UnixFact]
    public void TheDecodeTimeReadRefusesAFileItMayNotOpen()
    {
        var project = WriteTheRealTree(picture: "locked.png");
        var picture = Resolve(project).AvatarPath!;

        File.SetUnixFileMode(picture, UnixFileMode.None);

        Assert.Null(PersonaFiles.ReadAvatarFile(picture));
        Assert.Contains("unreadable", Assert.Single(LinesAbout("locked.png")));
    }

    // --- the log itself ----------------------------------------------------

    // Twenty agents in one checkout resolve the same tree and hit the same
    // refusal. Twenty identical lines is how a diagnostic becomes noise nobody
    // reads, so the same message is written once.
    [Fact]
    public void TheSameRefusalIsWrittenOnceHoweverManySessionsAskAboutIt()
    {
        // 17, not 9: 9 MiB cleared CB-135's 8 MiB cap under CB-146's 16 MiB
        // one and would resolve rather than refuse, leaving no line to dedupe.
        var project = WriteTheRealTree(17 * 1024 * 1024, "twenty.png");

        for (var i = 0; i < 20; i++) Resolve(project);

        Assert.Single(LinesAbout("twenty.png"));
    }

    [Fact]
    public void ADifferentRefusalStillGetsItsOwnLine()
    {
        Resolve(WriteTheRealTree(17 * 1024 * 1024, "first.png"));
        Resolve(WriteTheRealTree(17 * 1024 * 1024, "second.png"));

        Assert.Single(LinesAbout("first.png"));
        Assert.Single(LinesAbout("second.png"));
    }

    // A ceiling rather than rotation: this file holds a handful of lines about
    // a handful of files, and one approaching 64 KB means the dedupe has
    // stopped working rather than that somebody has 64 KB of broken portraits.
    [Fact]
    public void AnAlreadyEnormousLogIsNotGrownFurther()
    {
        Directory.CreateDirectory(_logDir);
        File.WriteAllBytes(PersonaLog.Path_, new byte[PersonaLog.MaxBytes]);

        // A marker rather than a length: the classes outside this collection go
        // on resolving personas of their own into this same scratch directory,
        // so the file's size is not this test's to predict — but whether *this*
        // line went into it is.
        var marker = "ceiling probe " + Guid.NewGuid();
        PersonaLog.Record(marker);

        Assert.DoesNotContain(marker, File.ReadAllText(PersonaLog.Path_), StringComparison.Ordinal);
    }

    // A log that cannot be written is not a reason to lose the orb. This runs
    // on the UI thread every two seconds by way of the persona scan, and the
    // worst outcome allowed is that nothing is written — which is exactly where
    // this was before the log existed.
    [Fact]
    public void ALogDirectoryThatIsSomebodysFileIsSurvivedInSilence()
    {
        var blocked = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocked, "I am a file");

        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", blocked);

        PersonaLog.Record("a picture was ignored");

        Assert.True(File.Exists(blocked));
        Assert.Equal("I am a file", File.ReadAllText(blocked));
    }
}
