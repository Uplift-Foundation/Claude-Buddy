using Xunit;

namespace ClaudeBuddy.Tests;

// The importing file's directory as a root for a persona picture, against
// real trees.
//
// profile-gen writes a persona as its own file and imports it from a CLAUDE.md
// inside a marker block, and its schema says the file's `image:` is relative to
// the directory of that CLAUDE.md. CB-147 gave the resolver a workspace root,
// which is the same directory only while the session sits in it — so from a
// subdirectory the orb kept the persona's name and lost its picture. This is
// the seam that proves the importer's directory is now tried in its own right,
// through Resolve and the real import walk, and that every guard CB-147 kept
// per root still holds for the third one.
[Collection("LogDir")]
public class PersonaImportingFileRootFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-importer-root-" + Guid.NewGuid());

    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-importer-root-log-" + Guid.NewGuid());

    private readonly IDisposable _logScope;

    public PersonaImportingFileRootFileTests()
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
        var personaDir = WriteProfileGenPersona(repo, ".claude/persona/importer-4410.gif");
        File.WriteAllBytes(Path.Combine(personaDir, "importer-4410.gif"), Png());
        var cwd = Dir("repo", "a", "b");

        var persona = LocalPersona.Resolve(cwd, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Jennifer Voss", persona.Name);
        Assert.Equal(PersonaFiles.CanonicalFile(Path.Combine(personaDir, "importer-4410.gif")), persona.AvatarPath);
        Assert.Equal(Png(), File.ReadAllBytes(persona.AvatarPath!));

        // Watched, so an edited portrait is noticed from a subdirectory too —
        // the scan's half of CB-135's invariant, confirmed rather than assumed.
        Assert.Contains(persona.AvatarPath!, persona.Watched);

        // The file directory and the workspace both missed before the
        // importer's directory found it; a miss followed by a hit is not a
        // refusal, so nothing is logged.
        Assert.Empty(LinesAbout("importer-4410.gif"));
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

    // An import of an import is anchored at the candidate, not at the file in
    // the middle: a picture written relative to the intermediate file's
    // directory is not found by this root (the file-directory root is the
    // naming file's, and the middle one is nobody's), and one written relative
    // to the CLAUDE.md is.
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

    // --- the guards, run against the third root alone ----------------------

    [Fact]
    public void AValueThatEscapesAllThreeRootsIsRefusedWithOneLineNamingAllThree()
    {
        var fileDir = Dir("repo", "persona");
        var importer = Dir("repo");
        var workspace = Dir("ws");
        var outside = Dir("outside");
        File.WriteAllBytes(Path.Combine(outside, "escape-4414.png"), Png());

        // Absolute, so it names the same real file under every root and is
        // contained in none of them.
        var bytes = PersonaFiles.AvatarAt(
            fileDir, importer, workspace, Path.Combine(outside, "escape-4414.png"), out var path);

        Assert.Null(bytes);
        Assert.Null(path);

        var line = Assert.Single(LinesAbout("escape-4414.png"));
        Assert.Contains(
            "escapes root — it resolves outside the directory of the markdown that named it, "
            + "the directory of the file that imported it, and the workspace", line);
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
        Assert.Contains("the directory of the file that imported it, and the workspace", line);
    }

    // From the repository root the workspace is the importer's directory, so
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

    // With no workspace, the importer alone is the second root and is named.
    [Fact]
    public void WithNoWorkspaceTheImporterIsNamedAsTheSecondRoot()
    {
        var fileDir = Dir("repo", "persona");
        var importer = Dir("repo");

        PersonaFiles.AvatarAt(fileDir, importer, null, "missing-4417.png", out var path);

        Assert.Null(path);
        var line = Assert.Single(LinesAbout("missing-4417.png"));
        Assert.Contains(
            "missing under both the directory of the markdown that named it and the directory of the file "
            + "that imported it", line);
    }

    [SymlinkFact]
    public void ASymlinkEscapeUnderTheImportersDirectoryIsRefused()
    {
        var fileDir = Dir("repo", "persona");
        var importer = Dir("repo");
        var elsewhere = Dir("elsewhere");
        File.WriteAllBytes(Path.Combine(elsewhere, "leota.png"), Png());
        Directory.CreateSymbolicLink(Path.Combine(importer, "pictures"), elsewhere);

        // Real through the link, so the refusal is about where the value
        // leads, not about absence.
        Assert.True(File.Exists(Path.Combine(importer, "pictures", "leota.png")));

        var bytes = PersonaFiles.AvatarAt(
            fileDir, importer, null, Path.Combine("pictures", "leota.png"), out var path);

        Assert.Null(bytes);
        Assert.Null(path);
    }
}
