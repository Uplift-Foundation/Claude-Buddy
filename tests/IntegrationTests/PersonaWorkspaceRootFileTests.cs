using Xunit;

namespace ClaudeBuddy.Tests;

// CB-147, against real trees: a persona picture may now be written relative to
// two different places — the directory of the markdown file that named it, or
// the session's workspace root — and this is the seam that proves both work
// together rather than only in isolation. LocalPersonaFilesTests already
// covers the single-root guard chain exhaustively; what is new here is the
// second root, the order the two are tried in, and what happens when they
// disagree.
//
// CB-133's own persona is the motivating case, restated exactly: a picture
// named `.claude/persona/margo.gif` from `.claude/persona/persona.md`, which
// is relative to the *workspace* root and doubles to a path that is not there
// when resolved (as it used to be, exclusively) against the markdown's own
// directory.
[Collection("LogDir")]
public class PersonaWorkspaceRootFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-workspace-root-" + Guid.NewGuid());

    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-workspace-root-log-" + Guid.NewGuid());

    private readonly string? _logWas;

    public PersonaWorkspaceRootFileTests()
    {
        Directory.CreateDirectory(_root);

        _logWas = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logDir);

        // Process-wide and deliberately never expiring — see
        // PersonaAvatarValueFileTests for why this is reset per test.
        PersonaLog.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logWas);
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

    // --- case 1: the motivating fixture, end to end through the resolver ---

    // The real bug, reproduced the way the app actually reaches it: through
    // an `@`-import (this repository's own CLAUDE.md carries exactly this
    // shape, `@.claude/persona/persona.md`), not by handing ResolveFrom a
    // hand-picked root directly. `.claude/persona/persona.md` is not itself a
    // CandidateFiles entry — CandidateFiles only ever offers CLAUDE.md,
    // CLAUDE.local.md, .claude/CLAUDE.md and AGENTS.md per directory — so the
    // only way the resolver ever reaches it is by importing it out of a
    // CLAUDE.md, and that import hop is exactly where the real defect lived:
    // the importing file's own directory (the workspace root) is not the
    // directory of the file that actually names the picture.
    [Fact]
    public void APictureNamedRelativeToTheWorkspaceRootResolvesThroughTheRealImportShape()
    {
        var workspace = Dir();
        var personaDir = Dir(".claude", "persona");

        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "@.claude/persona/persona.md\n");
        File.WriteAllText(
            Path.Combine(personaDir, "persona.md"),
            "- Name: Margo\n- Profile picture: .claude/persona/margo.gif\n");
        File.WriteAllBytes(Path.Combine(personaDir, "margo.gif"), Png());

        var persona = LocalPersona.Resolve(workspace, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal("Margo", persona.Name);
        Assert.Equal(Path.Combine(personaDir, "margo.gif"), persona.AvatarPath);
        Assert.Equal(Png(), File.ReadAllBytes(persona.AvatarPath!));
        Assert.Equal(Path.Combine(personaDir, "persona.md"), persona.AvatarSource);

        // Persona.Watched already appends AvatarPath (CB-135) — confirmed
        // rather than assumed, since a workspace-relative picture is exactly
        // the shape that would go unwatched if that had quietly regressed.
        Assert.Contains(persona.AvatarPath!, persona.Watched);

        // Nothing was actually wrong with this file, so nothing should have
        // been written to persona.log about it.
        Assert.Empty(LinesAbout("margo.gif"));
    }

    // The same fixture, one layer down: a direct call proves PersonaFiles'
    // own resolution rather than the import machinery around it. Kept
    // alongside the test above rather than instead of it — CLAUDE.md's own
    // rule is that the seam and the decision fail differently, and a direct
    // call here cannot prove the import hop hands the resolver the right
    // root in the first place.
    [Fact]
    public void TheSameFixtureResolvesDirectlyThroughAvatarAtToo()
    {
        var personaDir = Dir(".claude", "persona");
        var workspace = _root;
        File.WriteAllBytes(Path.Combine(personaDir, "margo.gif"), Png());

        var bytes = PersonaFiles.AvatarAt(personaDir, workspace, ".claude/persona/margo.gif", out var path);

        Assert.Equal(Png(), bytes);
        Assert.Equal(Path.Combine(personaDir, "margo.gif"), path);
    }

    // --- case 2: the common case must not regress ---------------------------

    // A plain filename, with no workspace-relative prefix, resolves off the
    // first (file-directory) root exactly as it always has — even with a
    // genuinely distinct workspace root in play, and even though the picture
    // is not sitting anywhere near that workspace root at all. The second
    // root is not merely unnecessary here, it is never consulted (D2).
    [Fact]
    public void APlainFilenameStillResolvesOffTheFileDirectoryRootAlone()
    {
        var workspace = Dir();
        var personaDir = Dir(".claude", "persona");

        File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "@.claude/persona/persona.md\n");
        File.WriteAllText(
            Path.Combine(personaDir, "persona.md"), "- Profile picture: leota.png\n");
        File.WriteAllBytes(Path.Combine(personaDir, "leota.png"), Png());

        var persona = LocalPersona.Resolve(workspace, SessionSource.ClaudeCode, Array.Empty<string>());

        Assert.Equal(Path.Combine(personaDir, "leota.png"), persona.AvatarPath);
    }

    // --- case 3 & 9: escapes both roots, refused, one log line, both named -

    // fileDir and workspaceRoot are siblings here rather than nested, chosen
    // so that one `../`-shaped value resolves to the exact same real file
    // from either anchor — which is what lets one fixture prove both halves
    // of "escapes both roots" at once, with a real file at the far end so the
    // refusal is provably about *where the value points*, not about absence.
    [Fact]
    public void AValueThatEscapesBothRootsIsRefusedWithOneLogLineNamingBothAnchors()
    {
        var fileDir = Dir("filedir");
        var workspaceRoot = Dir("ws");
        var outside = Dir("outside");
        File.WriteAllBytes(Path.Combine(outside, "escape-3321.png"), Png());

        var bytes = PersonaFiles.AvatarAt(
            fileDir, workspaceRoot, Path.Combine("..", "outside", "escape-3321.png"), out var path);

        Assert.Null(bytes);
        Assert.Null(path);

        // One line, not two (D5) — and it names both anchors (D6) rather than
        // saying which root failed twice.
        var line = Assert.Single(LinesAbout("escape-3321.png"));
        Assert.Contains("escapes root", line);
        Assert.Contains("directory of the markdown", line);
        Assert.Contains("workspace", line);
    }

    // --- case 4: the ambiguous case — file directory wins, unconditionally -

    [Fact]
    public void WhenBothRootsHaveARealAndDifferentFileTheFileDirectoryWins()
    {
        var workspaceRoot = Dir("project");
        var fileDir = Dir("project", "sub");

        var fileDirBytes = new byte[] { 11, 22, 33, 44 };
        var workspaceBytes = new byte[] { 99, 88, 77 };
        File.WriteAllBytes(Path.Combine(fileDir, "x.png"), fileDirBytes);
        File.WriteAllBytes(Path.Combine(workspaceRoot, "x.png"), workspaceBytes);

        var bytes = PersonaFiles.AvatarAt(fileDir, workspaceRoot, "x.png", out var path);

        // Byte-compared, not merely non-null — D2 requires the file
        // directory's own bytes, not merely "a" resolved file.
        Assert.Equal(fileDirBytes, bytes);
        Assert.Equal(Path.Combine(fileDir, "x.png"), path);
    }

    // --- case 5: symlink containment holds under the second root too -------

    [SymlinkFact]
    public void ASymlinkEscapeUnderTheWorkspaceRootIsRefusedTheSameWayItIsUnderTheFileDirectory()
    {
        var fileDir = Dir("filedir");
        var workspaceRoot = Dir("ws");
        var elsewhere = Dir("elsewhere");
        File.WriteAllBytes(Path.Combine(elsewhere, "leota.png"), Png());
        Directory.CreateSymbolicLink(Path.Combine(workspaceRoot, "pictures"), elsewhere);

        // Nothing under fileDir at all, so the first root is a plain miss;
        // the interesting refusal is the second root's, where the string
        // resolves to something real that is reached only through a link out
        // of the workspace.
        Assert.True(File.Exists(Path.Combine(workspaceRoot, "pictures", "leota.png")));

        var bytes = PersonaFiles.AvatarAt(
            fileDir, workspaceRoot, Path.Combine("pictures", "leota.png"), out var path);

        Assert.Null(bytes);
        Assert.Null(path);
    }

    // --- case 6: a null or missing workspace root degrades to develop's ----
    // --- single-root behaviour, byte for byte -------------------------------

    [Fact]
    public void ANullWorkspaceRootIsExactlyTheSingleRootBehaviour()
    {
        var fileDir = Dir("project");
        File.WriteAllBytes(Path.Combine(fileDir, "leota.png"), Png());

        var singleRootBytes = PersonaFiles.AvatarAt(fileDir, "leota.png", out var singleRootPath);
        var twoRootBytes = PersonaFiles.AvatarAt(fileDir, null, "leota.png", out var twoRootPath);

        Assert.Equal(singleRootBytes, twoRootBytes);
        Assert.Equal(singleRootPath, twoRootPath);
        Assert.Equal(Path.Combine(fileDir, "leota.png"), twoRootPath);
    }

    // The same degenerate case one layer up: LocalPersona.ResolveFrom given a
    // cwd that does not canonicalise (blank, or simply not there) behaves
    // exactly as ResolveFrom(candidates) alone always has — CanonicalDirectory
    // answering null is what CandidateRoots treats as "one candidate root",
    // and this is that path exercised end to end rather than assumed from
    // PersonaFiles' own unit coverage of CanonicalDirectory.
    [Fact]
    public void AWorkspaceCwdThatDoesNotExistDegradesToSingleRootResolveFromBehaviour()
    {
        var project = Dir("project");
        File.WriteAllLines(Path.Combine(project, "CLAUDE.md"), new[] { "Her name is Leota" });
        var candidates = LocalPersona.CandidateFiles(project, Array.Empty<string>(), SessionSource.ClaudeCode);

        var withoutWorkspace = LocalPersona.ResolveFrom(candidates);
        var withMissingWorkspace = LocalPersona.ResolveFrom(
            candidates, Path.Combine(_root, "never-made-" + Guid.NewGuid()));

        Assert.Equal("Leota", withoutWorkspace.Name);
        Assert.Equal(withoutWorkspace.AvatarPath, withMissingWorkspace.AvatarPath);
        Assert.Equal(withoutWorkspace.Name, withMissingWorkspace.Name);
    }
}
