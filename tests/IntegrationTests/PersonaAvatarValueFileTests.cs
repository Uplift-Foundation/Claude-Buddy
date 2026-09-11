using Xunit;

namespace ClaudeBuddy.Tests;

// CB-139 against real trees, at both ends of the feature — an OpenClaw
// workspace's IDENTITY.md and a local session's CLAUDE.md — because the two
// reach the same grammar down two different paths and the bug was an
// asymmetry.
//
// The grammar table in PersonaAvatarValueTests is necessary and it is not
// sufficient: it can only say the parser does what the parser was written to
// do. What is asserted here is that a file somebody would actually write
// produces a portrait on disk, and that the value that still cannot produce
// one says so in `persona.log` under a reason a reader can act on.
//
// The log half is the part worth the file. Before this ticket every one of the
// twenty-four refusals on one Mac mini read "unreadable — it is missing", about
// values that were never files: three code-spanned paths that were perfectly
// good and one `data:` URI that never could be. A wrong reason costs more than
// no reason, because somebody goes and looks for the file.
[Collection("LogDir")]
public class PersonaAvatarValueFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-avatar-" + Guid.NewGuid());

    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-avatar-log-" + Guid.NewGuid());

    private readonly string? _logWas;

    public PersonaAvatarValueFileTests()
    {
        Directory.CreateDirectory(_root);

        _logWas = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logDir);

        // Process-wide and deliberately never expiring, so a message another
        // case already wrote would be silently skipped here — which would turn
        // "exactly one line" into a test that passes for the wrong reason.
        PersonaLog.ResetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logWas);
        PersonaLog.ResetForTests();

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Png() =>
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    // A short one, so the whole value is quoted rather than truncated: the
    // point of the assertion is that a reader recognises what they wrote.
    private const string DataUri = "data:image/webp;base64,UklGRhYAAABXRUJQVlA4TAoAAAAv";

    // The shapes off the Mac mini, as the log had them. `avatars/` is a real
    // subdirectory in those profiles, not a flourish — a picture one level down
    // is what makes the last-token rule worth having.
    private string WriteWorkspace(string value, string? picture = "avatars/annabel-lee.gif")
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);

        File.WriteAllText(
            Path.Combine(workspace, "IDENTITY.md"),
            "- Name: Annabel Lee\n- Voice: `af_bella` (Kokoro TTS)\n- Profile picture: " + value + "\n");

        if (picture is not null)
        {
            var file = Path.Combine(workspace, picture.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Png());
        }

        return workspace;
    }

    private string WriteProject(string value, string? picture = "avatars/jessica.png")
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Notes\n\n- Name: Jessica\n- Profile picture: " + value + "\n");

        if (picture is not null)
        {
            var file = Path.Combine(project, picture.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Png());
        }

        return project;
    }

    private static LocalPersona.Persona Resolve(string project) =>
        LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

    // CLAUDE_BUDDY_LOG_DIR is one process-wide variable and the classes that
    // are not in this collection go on resolving personas of their own while
    // these run, so "the log is empty" is not a claim this suite can make and
    // "the log says this about my value" is. Every case names something
    // recognisable of its own for exactly that reason.
    private string[] LinesAbout(string fragment) =>
        (File.Exists(PersonaLog.Path_) ? File.ReadAllText(PersonaLog.Path_) : "")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(fragment, StringComparison.Ordinal))
            .ToArray();

    // --- the three portraits that never drew -------------------------------

    [Fact]
    public void AWorkspacePictureInACodeSpanWithANoteIsResolvedToRealBytes()
    {
        var workspace = WriteWorkspace("`avatars/annabel-lee.gif` (animated, updated 2026-09-09)");

        var metadata = OpenClawWorkspaceIdentity.Read(workspace);

        Assert.Equal(Png(), metadata.Avatar);
        Assert.Equal("Annabel Lee", metadata.Name);
        Assert.Empty(LinesAbout("annabel-lee"));
    }

    [Fact]
    public void ALocalPictureInACodeSpanWithANoteIsResolvedToARealPath()
    {
        var project = WriteProject("`avatars/jessica.png` (AI-generated, cherry blossom portrait)");

        var persona = Resolve(project);

        Assert.NotNull(persona.AvatarPath);
        Assert.EndsWith("jessica.png", persona.AvatarPath!);
        Assert.True(File.Exists(persona.AvatarPath));
        Assert.Empty(LinesAbout("jessica.png"));
    }

    // The third shape, which wears only the span. Asserted separately from the
    // two above because it is the one that proves the *span* alone was enough
    // to lose a portrait — a reader could otherwise conclude the note was the
    // whole problem.
    [Fact]
    public void APictureInACodeSpanWithNoNoteAtAllIsResolvedToo()
    {
        var project = WriteProject("`avatars/jessica.png`");

        Assert.NotNull(Resolve(project).AvatarPath);
        Assert.Empty(LinesAbout("jessica.png"));
    }

    // --- the fourth shape, which stays refused and now says why ------------

    // The assertion the real-machine check hangs off. On the mini each launch
    // re-logs the same batch, so the per-launch count is the measurement: two
    // `data:` lines and nothing else after this ships. One line, this category,
    // and never the old one — zero would mean the value stopped being attempted
    // at all, which would hide it again by a different route.
    [Fact]
    public void ALocalDataUriIsRefusedUnderItsOwnCategoryAndNotAsUnreadable()
    {
        var project = WriteProject(DataUri, picture: null);

        Assert.Null(Resolve(project).AvatarPath);

        var line = Assert.Single(LinesAbout(DataUri));
        Assert.Contains("not a picture path", line);
        Assert.DoesNotContain("unreadable", line);
        Assert.DoesNotContain("it is missing", line);
    }

    [Fact]
    public void AWorkspaceDataUriIsRefusedUnderItsOwnCategoryToo()
    {
        var workspace = WriteWorkspace(DataUri, picture: null);

        Assert.Null(OpenClawWorkspaceIdentity.Read(workspace).Avatar);

        var line = Assert.Single(LinesAbout(DataUri));
        Assert.Contains("not a picture path", line);
        Assert.DoesNotContain("unreadable", line);
    }

    // A URL and a value with no image extension arrive at the same category:
    // both are somebody naming a thing that is not a picture at all, and
    // splitting them into two messages would be two ways of saying the same
    // sentence. An absolute path used to be a third row here — CB-139's grammar
    // refused one on sight, the same as a URL — but CB-140 made a well-formed
    // absolute path legal, so it no longer lands in this category at all; see
    // PersonaRealFileTests for what it resolves to now, and the "escapes root"
    // tests for the one way an absolute path still gets refused.
    [Theory]
    [InlineData("https://example.invalid/portrait-8412.png")]
    [InlineData("no-extension-8412")]
    public void AUrlOrAValueThatIsNoPathAtAllLandsInTheSameCategory(string value)
    {
        var project = WriteProject(value, picture: null);

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Contains("not a picture path", Assert.Single(LinesAbout("8412")));
    }

    // --- the honest "unreadable" that must survive -------------------------

    // The category this change must not swallow. A code-spanned path that
    // normalises perfectly and names a file nobody put there is *missing*, and
    // saying so is the whole point of the log: the reader has a path to go and
    // look at. Reporting it under the new category instead would be the same
    // wrong-reason failure in the other direction.
    [Fact]
    public void ACodeSpannedPathNamingAFileThatIsNotThereIsStillUnreadable()
    {
        var project = WriteProject("`avatars/absent-7213.png` (never exported)", picture: null);

        Assert.Null(Resolve(project).AvatarPath);

        var line = Assert.Single(LinesAbout("absent-7213.png"));
        Assert.Contains("unreadable", line);
        Assert.DoesNotContain("not a picture path", line);
        // The normalised path is what gets quoted, not the raw value — the
        // reader is being sent to a file, so the line has to name the file.
        Assert.DoesNotContain("never exported", line);
    }

    // --- and the silence a scan depends on ---------------------------------

    // A persona scan reads every CLAUDE.md up a session's directory tree, most
    // of which say nothing about a picture. If a file that named none wrote a
    // line, the log would be a list of ordinary markdown rather than a list of
    // problems, and the dedupe would not save it because every path differs.
    [Theory]
    [InlineData("- Name: nobody-5561")]
    [InlineData("- Profile picture: <your picture-5561>")]
    public void AFileThatNamedNoUsablePictureWritesNothingAtAll(string line)
    {
        var project = Path.Combine(_root, "quiet");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "# Notes\n\n" + line + "\n");

        Assert.Null(Resolve(project).AvatarPath);
        Assert.Empty(LinesAbout("5561"));
    }

    // --- the seam itself ---------------------------------------------------

    // Both entry points take the parsed Fields rather than the one string they
    // need out of it, so that "named and unusable" cannot be dropped on the
    // floor at one of the two call sites. Asserted directly as well as through
    // the resolvers, because the two overloads are the contract and the
    // resolvers are only two of its callers.
    [Fact]
    public void TheBytesOverloadRefusesAndReportsAValueThatNamedNoPath()
    {
        var fields = PersonaMarkdown.Parse(new[] { "- Avatar: " + DataUri });

        Assert.Null(PersonaFiles.AvatarAt(_root, fields));
        Assert.Contains("not a picture path", Assert.Single(LinesAbout(DataUri)));
    }

    [Fact]
    public void ThePathOverloadRefusesAndReportsAValueThatNamedNoPath()
    {
        var fields = PersonaMarkdown.Parse(new[] { "- Avatar: https://example.invalid/x-3390.png" });

        Assert.Null(PersonaFiles.AvatarPathAt(_root, fields));
        Assert.Contains("not a picture path", Assert.Single(LinesAbout("3390")));
    }

    [Fact]
    public void NeitherOverloadSaysAnythingAboutFieldsThatNamedNoPicture()
    {
        var fields = PersonaMarkdown.Parse(new[] { "- Name: quiet-4470" });

        Assert.Null(PersonaFiles.AvatarAt(_root, fields));
        Assert.Null(PersonaFiles.AvatarPathAt(_root, fields));
        Assert.Empty(LinesAbout("4470"));
    }

    [Fact]
    public void BothOverloadsStillResolveAPictureThatIsActuallyThere()
    {
        File.WriteAllBytes(Path.Combine(_root, "here-9902.png"), Png());
        var fields = PersonaMarkdown.Parse(new[] { "- Avatar: `here-9902.png` (exported today)" });

        Assert.Equal(Png(), PersonaFiles.AvatarAt(_root, fields));
        Assert.EndsWith("here-9902.png", PersonaFiles.AvatarPathAt(_root, fields)!);
        Assert.Empty(LinesAbout("here-9902.png"));
    }
}
