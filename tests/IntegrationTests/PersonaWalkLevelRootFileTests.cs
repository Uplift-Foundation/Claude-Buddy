using Xunit;

namespace ClaudeBuddy.Tests;

// CB-187: the walk-level directory as a root for a persona picture, against
// real trees.
//
// profile-gen writes a persona as its own file and imports it from a CLAUDE.md
// inside a marker block (or, for an agent-team hire, writes it at
// profiles/<name>/<name>.md), and the file's `image:` is relative to the
// project directory the walk found it at. CB-147 gave the resolver a workspace
// root, which is that directory only while the session sits in it — so from a
// subdirectory the orb kept the persona's name and lost its picture. This is
// the seam that proves the level is now tried in its own right, through
// Resolve and the real walk, one test per shape the walk offers, and that
// every guard CB-147 kept per root still holds for the third one.
[Collection("LogDir")]
public class PersonaWalkLevelRootFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-walk-level-" + Guid.NewGuid());

    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-walk-level-log-" + Guid.NewGuid());

    private readonly IDisposable _logScope;

    public PersonaWalkLevelRootFileTests()
    {
        Directory.CreateDirectory(_root);

        // AsyncLocal rather than CLAUDE_BUDDY_LOG_DIR, for the reason
        // PersonaWorkspaceRootFileTests gives: _logDir is asserted about.
        _logScope = CrashLog.ScopeForTests(_logDir);
        PersonaLog.ResetForTests();
    }

    public void Dispose()
    {
        _logScope.Dispose();
        PersonaLog.ResetForTests();

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string[] LinesAbout(string fragment) =>
        (File.Exists(PersonaLog.Path_) ? File.ReadAllText(PersonaLog.Path_) : "")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(fragment, StringComparison.Ordinal))
            .ToArray();

    // What profile-gen writes, byte for byte in shape: the marker block in the
    // CLAUDE.md, and YAML front matter in the persona file with a quoted image
    // path relative to the CLAUDE.md.
    private string WriteProfileGenPersona(string repo, string picture)
    {
        var personaDir = Dir(Path.GetRelativePath(_root, repo), ".claude", "persona");
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"),
            "# Project\n\n<!-- profile-gen:start slug=persona -->\n@.claude/persona/persona.md\n"
            + "<!-- profile-gen:end slug=persona -->\n");
        File.WriteAllText(Path.Combine(personaDir, "persona.md"),
            "---\nschema_version: 1\nname: \"Jennifer Voss\"\nslug: \"jennifer-voss\"\n"
            + "image: \"" + picture + "\"\n---\n\n# Jennifer Voss\n");
        return personaDir;
    }

    // --- the defect, end to end --------------------------------------------

    [Fact]
    public void AProfileGenPictureResolvesFromTwoLevelsBelowTheRepository()
    {
        var repo = Dir("repo");
        var personaDir = WriteProfileGenPersona(repo, ".claude/persona/level-4410.gif");
        File.WriteAllBytes(Path.Combine(personaDir, "level-4410.gif"), Png());
        var cwd = Dir("repo", "a", "b");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Jennifer Voss", persona.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "level-4410.gif")), persona.AvatarPath);
        Assert.Equal(Png(), File.ReadAllBytes(persona.AvatarPath!));

        // Watched, so an edited portrait is noticed from a subdirectory too —
        // the scan's half of CB-135's invariant, confirmed rather than assumed.
        Assert.Contains(persona.AvatarPath!, persona.Watched);

        // The file directory missed before the level found it; a miss
        // followed by a hit is not a refusal, so nothing is logged.
        Assert.Empty(LinesAbout("level-4410.gif"));
    }

    // The same from the repository root, where CB-147's workspace root
    // already found it — kept so the fix is shown not to have moved the one
    // case that worked.
    [Fact]
    public void TheSamePictureStillResolvesFromTheRepositoryRoot()
    {
        var repo = Dir("repo");
        var personaDir = WriteProfileGenPersona(repo, ".claude/persona/root-4411.gif");
        File.WriteAllBytes(Path.Combine(personaDir, "root-4411.gif"), Png());

        var persona = LocalPersona.Resolve(repo, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "root-4411.gif")), persona.AvatarPath);
    }

    // A user-level CLAUDE.md is a candidate that is not an ancestor of the
    // cwd at all, so the workspace root can never stand in for it: before
    // this, a persona imported from ~/.claude/CLAUDE.md with a picture
    // relative to ~/.claude resolved from no cwd whatsoever.
    [Fact]
    public void APersonaImportedFromTheUserLevelFileResolvesFromAnyProject()
    {
        var configDir = Dir("config");
        var personaDir = Dir("config", "persona");
        File.WriteAllText(Path.Combine(configDir, "CLAUDE.md"), "@persona/persona.md\n");
        File.WriteAllText(Path.Combine(personaDir, "persona.md"),
            "---\nname: \"Pixel\"\nimage: \"persona/user-4412.gif\"\n---\n");
        File.WriteAllBytes(Path.Combine(personaDir, "user-4412.gif"), Png());
        var project = Dir("somewhere", "else");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });

        Assert.Equal("Pixel", persona.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "user-4412.gif")), persona.AvatarPath);
    }

    // An import of an import is anchored at the candidate's level, not at the
    // file in the middle: a picture written relative to the intermediate
    // file's directory is not found (the file-directory root is the naming
    // file's, and the middle one is nobody's), and one written relative to
    // the project directory is.
    [Fact]
    public void ANestedImportIsAnchoredAtTheCandidateNotTheFileBetween()
    {
        var repo = Dir("repo");
        var docs = Dir("repo", "docs");
        var personaDir = Dir("repo", "docs", "persona");
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "@docs/index.md\n");
        File.WriteAllText(Path.Combine(docs, "index.md"), "@persona/persona.md\n");
        File.WriteAllText(Path.Combine(personaDir, "persona.md"),
            "- Name: Madame\n- Profile picture: docs/persona/nested-4413.png\n");
        File.WriteAllBytes(Path.Combine(personaDir, "nested-4413.png"), Png());
        var cwd = Dir("repo", "src");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "nested-4413.png")), persona.AvatarPath);

        // The paired negative: relative to docs/ — the file between — it is
        // not a root, so the same picture written that way is not found.
        File.WriteAllText(Path.Combine(personaDir, "persona.md"),
            "- Name: Madame\n- Profile picture: persona/nested-4413.png\n");

        Assert.Null(LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>()).AvatarPath);
    }

    // --- one test per shape the walk offers ----------------------------------

    // <dir>/.claude/CLAUDE.md is found at level <dir>, not <dir>/.claude:
    // profile-gen's path is relative to the project directory, and resolving
    // it against .claude would look in .claude/.claude/persona.
    [Fact]
    public void APersonaImportedFromDotClaudeClaudeMdResolvesAgainstTheProjectDirectory()
    {
        var repo = Dir("repo");
        var dotClaude = Dir("repo", ".claude");
        var personaDir = Dir("repo", ".claude", "persona");
        File.WriteAllText(Path.Combine(dotClaude, "CLAUDE.md"), "@persona/persona.md\n");
        File.WriteAllText(Path.Combine(personaDir, "persona.md"),
            "---\nname: \"Margo\"\nimage: \".claude/persona/dot-4420.gif\"\n---\n");
        File.WriteAllBytes(Path.Combine(personaDir, "dot-4420.gif"), Png());
        var cwd = Dir("repo", "src");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "dot-4420.gif")), persona.AvatarPath);
    }

    // CB-154's two layouts: no import at all, and the picture is relative to
    // the directory the profiles folder sits in (profile-gen e79f2f8) — not
    // the persona file's own folder, and not the cwd.
    [Theory]
    [InlineData("profiles")]
    [InlineData(".profiles-assets")]
    public void ATeamMembersProfileResolvesAgainstTheDirectoryItsFolderSitsIn(string layout)
    {
        var repo = Dir("repo");
        var memberDir = Dir("repo", layout, "ines-harrow");
        File.WriteAllText(Path.Combine(memberDir, "ines-harrow.md"),
            "---\nname: \"Ines Harrow\"\nimage: \"" + layout + "/ines-harrow/member-4421.png\"\n---\n");
        File.WriteAllBytes(Path.Combine(memberDir, "member-4421.png"), Png());
        var cwd = Dir("repo", "a", "b");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>(), "ines-harrow");

        Assert.Equal("Ines Harrow", persona.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(memberDir, "member-4421.png")), persona.AvatarPath);
    }

    // Two candidates, each importing a persona: the nearer one's picture is
    // measured against its own level and never the farther candidate's. The
    // fixture puts the nearer persona's picture *only* where the farther
    // level would find it, so if the levels were crossed the nearer persona
    // would win with the wrong file; instead it finds nothing and the farther
    // persona's own picture is drawn.
    [Fact]
    public void EachCandidatesImportResolvesAgainstItsOwnLevel()
    {
        var repo = Dir("repo");
        var repoPersona = Dir("repo", ".claude", "persona");
        var configDir = Dir("config");
        var configPersona = Dir("config", ".claude", "persona");
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "@.claude/persona/near.md\n");
        File.WriteAllText(Path.Combine(repoPersona, "near.md"),
            "---\nname: \"Near\"\nimage: \".claude/persona/near-4422.png\"\n---\n");
        File.WriteAllText(Path.Combine(configDir, "CLAUDE.md"), "@.claude/persona/far.md\n");
        File.WriteAllText(Path.Combine(configPersona, "far.md"),
            "---\nname: \"Far\"\nimage: \".claude/persona/far-4422.png\"\n---\n");
        File.WriteAllBytes(Path.Combine(configPersona, "far-4422.png"), Png());
        File.WriteAllBytes(Path.Combine(configPersona, "near-4422.png"), Png());
        var cwd = Dir("repo", "a");

        var crossed = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, new[] { configDir });

        Assert.Equal("Near", crossed.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(configPersona, "far-4422.png")), crossed.AvatarPath);

        // And once the nearer picture is where its own level says, it wins.
        File.WriteAllBytes(Path.Combine(repoPersona, "near-4422.png"), Png());

        var own = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, new[] { configDir });

        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(repoPersona, "near-4422.png")), own.AvatarPath);
    }

    // A picture only the cwd has still resolves — CB-147's convention, kept.
    [Fact]
    public void APictureOnlyTheCwdHasStillResolves()
    {
        var repo = Dir("repo");
        WriteProfileGenPersona(repo, "cwd-4423.png");
        var cwd = Dir("repo", "a");
        File.WriteAllBytes(Path.Combine(cwd, "cwd-4423.png"), Png());

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(cwd, "cwd-4423.png")), persona.AvatarPath);
    }

    // From a subdirectory, an edit to the persona file and an edit to the
    // picture each move the signature the scan compares — the picture only
    // because it resolved at all, which is what this ticket fixes.
    [Fact]
    public void FromASubdirectoryEditingThePersonaOrThePictureMovesTheSignature()
    {
        var repo = Dir("repo");
        var personaDir = WriteProfileGenPersona(repo, ".claude/persona/sig-4424.gif");
        var picture = Path.Combine(personaDir, "sig-4424.gif");
        File.WriteAllBytes(picture, Png());
        var cwd = Dir("repo", "a", "b");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());
        var before = LocalPersona.Signature(persona.Watched);

        File.AppendAllText(Path.Combine(personaDir, "persona.md"), "\nMore about her.\n");
        var afterMarkdown = LocalPersona.Signature(persona.Watched);
        Assert.NotEqual(before, afterMarkdown);

        File.WriteAllBytes(picture, Png().Concat(new byte[] { 0 }).ToArray());
        Assert.NotEqual(afterMarkdown, LocalPersona.Signature(persona.Watched));
    }

    // --- the guards, run against the third root alone ----------------------

    // A `../` out of the project directory is refused under the level exactly
    // as under the other two roots, end to end through the walk, with a real
    // file at the far end so the refusal is about where it points.
    [Fact]
    public void AClimbOutOfTheProjectDirectoryIsRefused()
    {
        var repo = Dir("repo");
        var outside = Dir("outside");
        File.WriteAllBytes(Path.Combine(outside, "climb-4425.png"), Png());
        WriteProfileGenPersona(repo, "../outside/climb-4425.png");
        var cwd = Dir("repo", "a");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Null(persona.AvatarPath);
        Assert.Contains("escapes root", Assert.Single(LinesAbout("climb-4425.png")));
    }

    [Fact]
    public void AValueThatEscapesAllThreeRootsIsRefusedWithOneLineNamingAllThree()
    {
        var fileDir = Dir("repo", "persona");
        var level = Dir("repo");
        var workspace = Dir("ws");
        var outside = Dir("outside");
        File.WriteAllBytes(Path.Combine(outside, "escape-4414.png"), Png());

        // Absolute, so it names the same real file under every root and is
        // contained in none of them.
        var bytes = PersonaFiles.AvatarAt(
            fileDir, level, workspace, Path.Combine(outside, "escape-4414.png"), out var path);

        Assert.Null(bytes);
        Assert.Null(path);

        var line = Assert.Single(LinesAbout("escape-4414.png"));
        Assert.Contains(
            "escapes root — it resolves outside the directory of the markdown that named it, "
            + "the project directory it was found from, and the workspace", line);
    }

    [Fact]
    public void APictureMissingUnderEveryRootIsOneUnreadableLine()
    {
        var repo = Dir("repo");
        WriteProfileGenPersona(repo, ".claude/persona/missing-4415.gif");
        var cwd = Dir("repo", "a");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Jennifer Voss", persona.Name);
        Assert.Null(persona.AvatarPath);
        var line = Assert.Single(LinesAbout("missing-4415.gif"));
        Assert.Contains("the project directory it was found from, and the workspace", line);
    }

    // From the repository root the workspace is the level, so
    // there are two roots and CB-147's exact wording, not a three-root line
    // naming one directory twice.
    [Fact]
    public void FromTheRepositoryRootTheRefusalKeepsCb147sWording()
    {
        var repo = Dir("repo");
        WriteProfileGenPersona(repo, ".claude/persona/missing-4416.gif");

        LocalPersona.Resolve(repo, SessionSource.ClaudeCode, Array.Empty<string>());

        var line = Assert.Single(LinesAbout("missing-4416.gif"));
        Assert.Contains(
            "unreadable — it is missing under both the directory of the markdown that named it and the "
            + "workspace, empty", line);
    }

    // With no workspace, the level alone is the second root and is named.
    [Fact]
    public void WithNoWorkspaceTheLevelIsNamedAsTheSecondRoot()
    {
        var fileDir = Dir("repo", "persona");
        var level = Dir("repo");

        PersonaFiles.AvatarAt(fileDir, level, null, "missing-4417.png", out var path);

        Assert.Null(path);
        var line = Assert.Single(LinesAbout("missing-4417.png"));
        Assert.Contains(
            "missing under both the directory of the markdown that named it and the project directory it "
            + "was found from", line);
    }

    [SymlinkFact]
    public void ASymlinkEscapeUnderTheLevelIsRefused()
    {
        var fileDir = Dir("repo", "persona");
        var level = Dir("repo");
        var elsewhere = Dir("elsewhere");
        File.WriteAllBytes(Path.Combine(elsewhere, "leota.png"), Png());
        Directory.CreateSymbolicLink(Path.Combine(level, "pictures"), elsewhere);

        // Real through the link, so the refusal is about where the value
        // leads, not about absence.
        Assert.True(File.Exists(Path.Combine(level, "pictures", "leota.png")));

        var bytes = PersonaFiles.AvatarAt(
            fileDir, level, null, Path.Combine("pictures", "leota.png"), out var path);

        Assert.Null(bytes);
        Assert.Null(path);
    }
}
