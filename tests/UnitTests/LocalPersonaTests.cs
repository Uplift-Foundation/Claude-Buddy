using Xunit;

namespace ClaudeBuddy.Tests;

// LocalPersona — which files a session's persona could be written in, which of
// them actually says what, and what the orb ends up called.
//
// In the Settings collection because UserConfigDirs reaches
// ClaudeConfigRoots, which reads ClaudeCodeProfileDirs — one process-wide static
// that a dozen classes in this assembly touch. SettingsCollection.cs has the
// story of the once-in-five failure that bought that rule.
[Collection("Settings")]
public class LocalPersonaTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-local-persona-" + Guid.NewGuid());

    public LocalPersonaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string directory, string name, params string[] lines)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    // --- CandidateFiles: which paths, in which order, with no IO ----------

    [Fact]
    public void EachDirectoryOffersTheFourNamesClaudeCodeItselfReads()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);

        var files = LocalPersona.CandidateFiles(cwd, Array.Empty<string>(), SessionSource.ClaudeCode);

        Assert.Equal(new[]
        {
            Path.Combine(full, "CLAUDE.md"),
            Path.Combine(full, "CLAUDE.local.md"),
            Path.Combine(full, ".claude", "CLAUDE.md"),
            Path.Combine(full, "AGENTS.md"),
        }, files.Take(4));
    }

    // Nearest first: the working directory's own files come before its
    // parent's, all the way up. A name set at the top of a monorepo is a
    // default, not an override.
    [Fact]
    public void TheWalkGoesUpwardsFromTheWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a", "b");
        var full = Path.GetFullPath(cwd);
        var parent = Path.GetDirectoryName(full)!;

        var files = LocalPersona.CandidateFiles(cwd, Array.Empty<string>(), SessionSource.ClaudeCode);

        Assert.Equal(Path.Combine(parent, "CLAUDE.md"), files[4]);
        Assert.Contains(Path.Combine(Path.GetPathRoot(full)!, "CLAUDE.md"), files);
    }

    [Fact]
    public void TheUserLevelFileIsAskedLastAndOnlyForClaudeCode()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "cb-candidates", "a");
        var configDir = Path.Combine(Path.GetTempPath(), "cb-config");
        var expected = Path.Combine(configDir, "CLAUDE.md");

        var claude = LocalPersona.CandidateFiles(cwd, new[] { configDir }, SessionSource.ClaudeCode);
        Assert.Equal(expected, claude[^1]);

        // Codex and Grok keep their own config directories with their own
        // layouts. Reading ~/.claude for one of them would be a persona taken
        // from the wrong CLI, and a wrong name is worse than no name.
        Assert.DoesNotContain(
            expected, LocalPersona.CandidateFiles(cwd, new[] { configDir }, SessionSource.Codex));
        Assert.DoesNotContain(
            expected, LocalPersona.CandidateFiles(cwd, new[] { configDir }, SessionSource.Grok));
    }

    // A gateway conversation has no directory here at all, and a Remote Control
    // session's cwd is a path on somebody else's machine — one that may well
    // exist here too, wearing the same string over a different directory.
    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void ASessionWithNoLocalDirectoryHasNoCandidates(SessionSource source)
    {
        var files = LocalPersona.CandidateFiles(
            Path.GetTempPath(), new[] { Path.GetTempPath() }, source);

        Assert.Empty(files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfigDirIsSkipped(string blank)
    {
        var files = LocalPersona.CandidateFiles(null, new[] { blank }, SessionSource.ClaudeCode);

        Assert.Empty(files);
    }

    [Fact]
    public void SurroundingSpaceOnAConfigDirIsTrimmed()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "cb-config");

        var files = LocalPersona.CandidateFiles(null, new[] { "  " + configDir + "  " }, SessionSource.ClaudeCode);

        Assert.Equal(Path.Combine(configDir, "CLAUDE.md"), Assert.Single(files));
    }

    // A config directory that the upward walk already passed through is one
    // file, not two: stating it twice cannot say anything the first reading did
    // not, and every candidate costs a stat on every scan.
    [Fact]
    public void AFileReachedTwiceIsOnlyAskedOnce()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "cb-candidates", "a");

        var files = LocalPersona.CandidateFiles(configDir, new[] { configDir }, SessionSource.ClaudeCode);

        Assert.Single(files, path => path == Path.Combine(Path.GetFullPath(configDir), "CLAUDE.md"));
    }

    // Not a security bound — the walk terminates at the root on its own — but a
    // bound on what the scan costs, four stats per level every two seconds.
    [Fact]
    public void TheWalkStopsAtAFixedDepth()
    {
        var deep = Path.Combine(
            new[] { Path.GetTempPath() }.Concat(Enumerable.Repeat("d", 45)).ToArray());

        var files = LocalPersona.CandidateFiles(deep, Array.Empty<string>(), SessionSource.ClaudeCode);

        Assert.Equal(40 * 4, files.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoWorkingDirectoryLeavesNothingToWalk(string? cwd)
    {
        Assert.Empty(LocalPersona.CandidateFiles(cwd, Array.Empty<string>(), SessionSource.ClaudeCode));
    }

    // A cwd the platform refuses to resolve is not a directory to walk. It
    // costs the walk rather than the whole persona — the user-level file is
    // still asked.
    [Fact]
    public void AWorkingDirectoryThePlatformRefusesIsSkipped()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "cb-config");

        var files = LocalPersona.CandidateFiles("a\0b", new[] { configDir }, SessionSource.ClaudeCode);

        Assert.Equal(Path.Combine(configDir, "CLAUDE.md"), Assert.Single(files));
    }

    // --- Load: what is actually read, and what it imports ------------------

    [Fact]
    public void OnlyTheCandidatesThatExistAreRead()
    {
        var project = Dir("project");
        var present = Write(project, "CLAUDE.md", "Her name is Leota");

        var read = LocalPersona.Load(new[]
        {
            present,
            Path.Combine(project, "CLAUDE.local.md"),
        });

        Assert.Equal(present, Assert.Single(read).Path);
    }

    // The import is part of how people actually write these: the persona is
    // often in the file the CLAUDE.md pulls in rather than in the CLAUDE.md.
    [Fact]
    public void AnImportedFileIsReadAfterTheFileThatImportedIt()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md", "See @persona.md for who you are");
        var imported = Write(project, "persona.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(new[] { main, imported }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void AnImportIsResolvedAgainstTheImportingFilesOwnDirectory()
    {
        var project = Dir("project");
        var docs = Dir("project", "docs");
        var main = Write(project, "CLAUDE.md", "@docs/persona.md");
        var first = Write(docs, "persona.md", "@second.md");
        var second = Write(docs, "second.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(new[] { main, first, second }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void TwoFilesImportingEachOtherAreEachReadOnce()
    {
        var project = Dir("project");
        var a = Write(project, "CLAUDE.md", "@b.md");
        var b = Write(project, "b.md", "@CLAUDE.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { a });

        Assert.Equal(new[] { a, b }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void ImportsStopAfterFiveHops()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md", "@1.md");
        for (var i = 1; i <= 6; i++) Write(project, i + ".md", "@" + (i + 1) + ".md");

        var read = LocalPersona.Load(new[] { main });

        // The importing file plus five hops; the sixth is written and never
        // opened.
        Assert.Equal(6, read.Count);
        Assert.DoesNotContain(Path.Combine(project, "6.md"), read.Select(entry => entry.Path));
    }

    [Fact]
    public void NoMoreThanTwentyFilesAreReadHoweverManyAreOffered()
    {
        var project = Dir("project");
        var imports = Enumerable.Range(1, 30).Select(i => "@" + i + ".md").ToArray();
        var main = Write(project, "CLAUDE.md", imports);
        foreach (var i in Enumerable.Range(1, 30)) Write(project, i + ".md", "nothing here");
        var second = Write(project, "AGENTS.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main, second });

        Assert.Equal(20, read.Count);
        // The cap stops the second candidate too, not just the imports.
        Assert.DoesNotContain(second, read.Select(entry => entry.Path));
    }

    // An import is a bare token. A sentence mentioning a file is not an
    // instruction to read it, and neither is a link to something that is not
    // markdown at all.
    [Fact]
    public void OnlyABareMarkdownTokenIsAnImport()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md",
            "@notes.txt",
            "@notes.md.",
            "notes.md",
            "@",
            "see @real.md");
        Write(project, "notes.txt", "x");
        Write(project, "notes.md", "x");
        var real = Write(project, "real.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(new[] { main, real }, read.Select(entry => entry.Path));
    }

    [Fact]
    public void AnImportThePlatformRefusesToResolveIsSkipped()
    {
        var project = Dir("project");
        var main = Write(project, "CLAUDE.md", "@a\0b.md", "Her name is Leota");

        var read = LocalPersona.Load(new[] { main });

        Assert.Equal(main, Assert.Single(read).Path);
    }

    // --- Resolve: the fold ------------------------------------------------

    [Fact]
    public void TheNearestFileToNameAFieldOwnsIt()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        Write(parent, "CLAUDE.md", "Her name is Parent", "Her voice is Samantha");
        Write(child, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Leota", persona.Name);
        // Not stated nearer, so the parent still supplies it.
        Assert.Equal("Samantha", persona.Voice);
        Assert.False(persona.IsEmpty);
    }

    [Fact]
    public void TheUserLevelFileIsTheLastResort()
    {
        var project = Dir("tree", "project");
        var configDir = Dir("config");
        Write(configDir, "CLAUDE.md", "Her name is UserLevel");

        var withoutProjectFile =
            LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });
        Assert.Equal("UserLevel", withoutProjectFile.Name);

        Write(project, "CLAUDE.md", "Her name is Leota");
        var withProjectFile =
            LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });
        Assert.Equal("Leota", withProjectFile.Name);
    }

    [Fact]
    public void APictureIsReadFromBesideTheFileThatNamedIt()
    {
        var project = Dir("tree", "project");
        var configDir = Dir("config");
        File.WriteAllBytes(Path.Combine(configDir, "leota.png"), Png());
        var named = Write(configDir, "CLAUDE.md", "Her profile picture is leota.png");
        // A file of the same name beside the session, which must not be the one
        // that wins: the picture belongs to the markdown that named it.
        File.WriteAllBytes(Path.Combine(project, "leota.png"), new byte[] { 1, 2, 3 });
        Write(project, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });

        Assert.Equal(Path.Combine(configDir, "leota.png"), persona.AvatarPath);
        Assert.Equal(Png(), File.ReadAllBytes(persona.AvatarPath!));
        Assert.Equal(named, persona.AvatarSource);
    }

    [Fact]
    public void APictureThatIsNotThereLeavesTheOrbItsLetters()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "Her name is Leota", "Her profile picture is missing.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Leota", persona.Name);
        Assert.Null(persona.AvatarPath);
        Assert.Null(persona.AvatarSource);
    }

    [Fact]
    public void TheFirstPictureThatCanBeReadWins()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        File.WriteAllBytes(Path.Combine(child, "near.png"), Png());
        File.WriteAllBytes(Path.Combine(parent, "far.png"), Png());
        var nearer = Write(child, "CLAUDE.md", "Her picture is near.png");
        Write(parent, "CLAUDE.md", "Her picture is far.png");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(nearer, persona.AvatarSource);
    }

    // A portrait is not a markdown file and is not a candidate, so nothing in
    // Files names it — and the scan compares Signature over what a persona
    // watches to decide whether to read anything again. Leaving the picture out
    // of that set is the whole of the bug this pins: the same filename with new
    // bytes changes no markdown file at all, so every signature over Files
    // alone is identical and the orb keeps the old face until the app restarts.
    [Fact]
    public void APortraitIsPartOfWhatAPersonaWatches()
    {
        var project = Dir("tree", "project");
        var picture = Path.Combine(project, "leota.png");
        File.WriteAllBytes(picture, Png());
        Write(project, "CLAUDE.md", "Her name is Leota", "Her picture is leota.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(picture, persona.AvatarPath);
        Assert.Equal(persona.Files.Append(picture), persona.Watched);

        var watchedBefore = LocalPersona.Signature(persona.Watched);
        var filesBefore = LocalPersona.Signature(persona.Files);

        // Overwritten in place: same name, new bytes, and not one markdown
        // file touched.
        File.WriteAllBytes(picture, Png().Concat(new byte[] { 0 }).ToArray());

        Assert.NotEqual(watchedBefore, LocalPersona.Signature(persona.Watched));
        Assert.Equal(filesBefore, LocalPersona.Signature(persona.Files));
    }

    // Only a length or an mtime, whichever the filesystem gives: both are in
    // the signature precisely so a picture edited either way is noticed, and a
    // same-length rewrite is the case a length alone would miss.
    [Fact]
    public void APortraitTouchedWithoutChangingItsLengthStillMoves()
    {
        var project = Dir("tree", "project");
        var picture = Path.Combine(project, "leota.png");
        File.WriteAllBytes(picture, Png());
        Write(project, "CLAUDE.md", "Her picture is leota.png");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());
        var before = LocalPersona.Signature(persona.Watched);

        // Set rather than slept for: the assertion is about the signature
        // reading the timestamp, not about how fine this filesystem's clock is.
        File.SetLastWriteTimeUtc(picture, File.GetLastWriteTimeUtc(picture).AddSeconds(5));

        Assert.NotEqual(before, LocalPersona.Signature(persona.Watched));
    }

    [Fact]
    public void APersonaWithNoPortraitWatchesOnlyWhatItRead()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Null(persona.AvatarPath);
        Assert.Equal(persona.Files, persona.Watched);
        Assert.Null(LocalPersona.Empty.AvatarPath);
        Assert.Empty(LocalPersona.Empty.Watched);
    }

    [Fact]
    public void EveryFileActuallyReadIsReported()
    {
        var parent = Dir("tree");
        var child = Dir("tree", "project");
        var near = Write(child, "CLAUDE.md", "@extra.md");
        var extra = Write(child, "extra.md", "Her name is Leota");
        var far = Write(parent, "AGENTS.md", "nothing");

        var persona = LocalPersona.Resolve(child, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(new[] { near, extra, far }, persona.Files);
    }

    [Fact]
    public void AGatewaySessionResolvesToNothingWithoutTouchingTheDisk()
    {
        var persona = LocalPersona.Resolve(
            Dir("tree"), SessionSource.OpenClaw, Array.Empty<string>());

        Assert.Same(LocalPersona.Empty, persona);
        Assert.True(persona.IsEmpty);
        Assert.Empty(persona.Files);
    }

    [Fact]
    public void AFileWithNothingInItLeavesAnEmptyPersona()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "# Just a heading", "and some prose about the project.");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.True(persona.IsEmpty);
        Assert.Single(persona.Files);
    }

    // --- Signature: what the scan compares ---------------------------------

    [Fact]
    public void AnEditChangesTheSignature()
    {
        var project = Dir("tree", "project");
        var file = Write(project, "CLAUDE.md", "Her name is Leota");
        var before = LocalPersona.Signature(new[] { file });

        // The stat has to differ in something the signature reads, and a same-
        // length rewrite within one filesystem timestamp tick would not. Both
        // fields are in there precisely so either one changing is enough.
        File.WriteAllLines(file, new[] { "Her name is Leota", "Her voice is Bella" });

        Assert.NotEqual(before, LocalPersona.Signature(new[] { file }));
    }

    [Fact]
    public void ASignatureSaysWhetherEachFileIsThereAtAll()
    {
        var project = Dir("tree", "project");
        var file = Path.Combine(project, "CLAUDE.md");
        var missing = LocalPersona.Signature(new[] { file });

        Write(project, "CLAUDE.md", "Her name is Leota");

        Assert.NotEqual(missing, LocalPersona.Signature(new[] { file }));
    }

    [Fact]
    public void APathThePlatformRefusesIsAStateLikeAnyOther()
    {
        var signature = LocalPersona.Signature(new[] { "a\0b" });

        Assert.Contains("?", signature);
    }

    [Fact]
    public void TwoFilesWithTheSameStateDoNotCollideIntoOneSignature()
    {
        var project = Dir("tree", "project");
        var a = Write(project, "CLAUDE.md", "Her name is Leota");
        var b = Write(project, "AGENTS.md", "Her name is Leota");

        Assert.NotEqual(LocalPersona.Signature(new[] { a }), LocalPersona.Signature(new[] { b }));
        Assert.NotEqual(
            LocalPersona.Signature(new[] { a, b }), LocalPersona.Signature(new[] { b, a }));
    }

    // --- ResolveFrom: the fold over a list somebody else built --------------
    //
    // What used to be here was ResolveForSession, a wrapper that asked
    // UserConfigDirs for itself and applied the two guards
    // SessionManager.ApplyPersona had already applied a moment earlier. CB-143
    // removed it: the scan needs the candidate list twice in one pass and now
    // builds it once, so the fold takes the list rather than the question that
    // produces it, and the guards live in the one place that was always
    // checking them first. Both of them are covered where they now are —
    // LocalPersonaScanTests' AGatewaySessionIsNotGivenAPersona and
    // ASessionWithNoWorkingDirectoryIsNotGivenAPersona.

    [Fact]
    public void TheFoldTakesTheCandidateListItIsGiven()
    {
        var project = Dir("tree", "project");
        Write(project, "CLAUDE.md", "Her name is Leota", "Her voice is Bella");

        var persona = LocalPersona.ResolveFrom(
            LocalPersona.CandidateFiles(project, Array.Empty<string>(), SessionSource.ClaudeCode));

        Assert.Equal("Leota", persona.Name);
        Assert.Equal("Bella", persona.Voice);
    }

    // An empty list is the answer for a session there is nowhere to look for —
    // a gateway conversation, whose CandidateFiles is empty by the rule at the
    // top of that method. Empty by reference, because the registry's own change
    // check is a reference check and a fresh record every pass would drop a
    // decoded portrait every pass.
    [Fact]
    public void AnEmptyCandidateListIsTheEmptyPersonaItself()
    {
        Assert.Same(LocalPersona.Empty, LocalPersona.ResolveFrom(Array.Empty<string>()));
    }

    // --- UserConfigDirs -----------------------------------------------------

    [Fact]
    public void TheProcessesOwnConfigDirectoryIsAskedFirst()
    {
        var dirs = LocalPersona.UserConfigDirs("/custom/claude", "/home/w");

        Assert.Equal("/custom/claude", dirs[0]);
        Assert.Equal(Path.Combine("/home/w", ".claude"), dirs[1]);
    }

    [Fact]
    public void AProcessConfigDirectoryThatIsTheDefaultIsNotAskedTwice()
    {
        var home = "/home/w";

        var dirs = LocalPersona.UserConfigDirs(Path.Combine(home, ".claude"), home);

        Assert.Equal(Path.Combine(home, ".claude"), Assert.Single(dirs));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NoProcessConfigDirectoryLeavesTheAccountsAsTheWholeAnswer(string unset)
    {
        var dirs = LocalPersona.UserConfigDirs(unset, "/home/w");

        Assert.Equal(Path.Combine("/home/w", ".claude"), Assert.Single(dirs));
    }

    // The default argument is the process environment, which is what production
    // gets. Asserted only on the account list, because another test in another
    // class may legitimately be holding CLAUDE_CONFIG_DIR at a sentinel while
    // this runs — see ConfigDirEnvCollection.cs — and what that variable
    // happens to say is not this test's claim.
    [Fact]
    public void TheEnvironmentIsWhereTheProcessConfigDirectoryComesFrom()
    {
        var dirs = LocalPersona.UserConfigDirs(home: "/home/w");

        Assert.Contains(Path.Combine("/home/w", ".claude"), dirs);
    }

    // --- OrbLabel -----------------------------------------------------------

    [Theory]
    // An agent's own name beats everything: every member of a team inherits the
    // team session's title, so without it a team of four draws one letter four
    // times.
    [InlineData("MenuUX", "Leota", "Fixing the flyout", "claude-buddy", "MenuUX")]
    // The persona is what the project says its assistant is called — more
    // specific than a generated chat title, less specific than an assigned role.
    [InlineData("", "Leota", "Fixing the flyout", "claude-buddy", "Leota")]
    [InlineData("", "", "Fixing the flyout", "claude-buddy", "Fixing the flyout")]
    [InlineData("", "", "", "claude-buddy", "claude-buddy")]
    [InlineData(null, null, null, null, "")]
    [InlineData(null, "Leota", null, "claude-buddy", "Leota")]
    public void TheOrbWearsTheMostSpecificNameItHas(
        string? agent, string? persona, string? title, string? folder, string expected)
    {
        Assert.Equal(expected, LocalPersona.OrbLabel(agent, persona, title, folder));
    }

    // --- LocalPersonas: the live registry -----------------------------------

    private static LocalPersona.Persona Named(string name, string? voice = null, double? rate = null) =>
        new(name, voice, rate, null, null, Array.Empty<string>());

    [Fact]
    public void APersonaIsRememberedAgainstItsSession()
    {
        var id = "sess-" + Guid.NewGuid();
        var persona = Named("Leota");

        LocalPersonas.Set(id, persona);

        Assert.Same(persona, LocalPersonas.For(id));
        Assert.Equal("local:" + id, LocalPersonas.AvatarKey(id));
    }

    [Fact]
    public void SettingTheSamePersonaObjectAgainLeavesEverythingAlone()
    {
        var id = "sess-" + Guid.NewGuid();
        var persona = Named("Leota");

        LocalPersonas.Set(id, persona);
        // The early return this exercises is what stops the scan dropping the
        // decoded avatar twice a second: the scan hands back one persona object
        // per session until the files under it change.
        LocalPersonas.Set(id, persona);

        Assert.Same(persona, LocalPersonas.For(id));
    }

    [Fact]
    public void ADifferentPersonaReplacesTheOneThatWasThere()
    {
        var id = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(id, Named("Leota"));

        var replacement = Named("Aurora");
        LocalPersonas.Set(id, replacement);

        Assert.Same(replacement, LocalPersonas.For(id));
    }

    [Fact]
    public void AForgottenSessionHasNoPersona()
    {
        var id = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(id, Named("Leota"));

        LocalPersonas.Forget(id);

        Assert.Null(LocalPersonas.For(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-set")]
    public void ASessionNobodyHasSetHasNoPersona(string? id)
    {
        Assert.Null(LocalPersonas.For(id));
    }

    [Fact]
    public void TheTestSeamReplacesTheWholeTable()
    {
        var stale = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(stale, Named("Stale"));

        var fresh = "sess-" + Guid.NewGuid();
        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
        {
            [fresh] = Named("Leota"),
        });

        Assert.Null(LocalPersonas.For(stale));
        Assert.Equal("Leota", LocalPersonas.For(fresh)!.Name);

        LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
        Assert.Null(LocalPersonas.For(fresh));
    }

    // --- LocalPersonas: speech ---------------------------------------------

    private static readonly TextToSpeech.VoiceOption Bella =
        new(TextToSpeech.SpeakEngine.Neural, "af_bella", "af_bella (Kokoro)");
    private static readonly TextToSpeech.VoiceOption Samantha =
        new(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha (system)");

    [Fact]
    public void APersonaVoiceResolvesOverWhateverThisMachineCanSpeakWith()
    {
        var id = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(id, Named("Leota", "Bella", 1.2));

        Assert.Equal(Bella, LocalPersonas.VoiceForSession(id, new[] { Bella, Samantha }));
        Assert.Equal(1.2, LocalPersonas.RateForSession(id));
    }

    // Three different ways of having no voice, all of which mean "use the
    // user's own": no persona at all, a persona that names none, and a name
    // that matches nothing installed here.
    [Fact]
    public void EveryWayOfHavingNoVoiceResolvesToNothing()
    {
        var options = new[] { Bella, Samantha };

        Assert.Null(LocalPersonas.VoiceForSession("never-set", options));
        Assert.Null(LocalPersonas.RateForSession("never-set"));

        var silent = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(silent, Named("Leota"));
        Assert.Null(LocalPersonas.VoiceForSession(silent, options));
        Assert.Null(LocalPersonas.RateForSession(silent));

        var unmatched = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(unmatched, Named("Leota", "Not Installed Here"));
        Assert.Null(LocalPersonas.VoiceForSession(unmatched, options));

        // The engine being switched off is the same answer arriving a different
        // way: with no neural options in the list there is nothing for a Kokoro
        // name to resolve to.
        var neuralOff = "sess-" + Guid.NewGuid();
        LocalPersonas.Set(neuralOff, Named("Leota", "Bella"));
        Assert.Null(LocalPersonas.VoiceForSession(neuralOff, new[] { Samantha }));
    }
}
