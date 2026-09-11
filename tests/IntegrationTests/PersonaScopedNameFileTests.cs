using Xunit;

namespace ClaudeBuddy.Tests;

// CB-142 against a real tree, for the reason PersonaRealFileTests gives at
// length: a grammar suite can only ever say the parser does what the parser
// was written to do, and CB-133 shipped with every one of those green while
// this repository's own persona file resolved to nothing.
//
// So these fixtures are written to disk, imported the way a real profile is
// imported, and read back through `LocalPersona.Resolve` rather than through
// `PersonaMarkdown.Parse`. The two halves that only this level can say:
//
//   * a persona written entirely as a bold-field block, or entirely as an
//     attribute table, reaches an orb with a name on it — through the `@`
//     import, the file walk and the picture containment check, none of which
//     a grammar test exercises;
//   * a schema table in an ordinary `CLAUDE.md` — the file every session in
//     every repository on this machine reads — leaves that orb unnamed.
//
// The second is the one worth writing at this level. `| Name | string |` is
// not a fixture somebody invented for a test: it is what a design document
// looks like, and the resolver reads design documents by default.
public class PersonaScopedNameFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-scoped-" + Guid.NewGuid());

    public PersonaScopedNameFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // A real PNG, small — the picture is not what these cases are about, but a
    // persona whose portrait silently fails to read would make a name
    // assertion pass for the wrong reason.
    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    // The shape this repository really uses: a `CLAUDE.md` that imports a
    // persona file out of `.claude/`, rather than one that states the fields
    // inline. `body` is the whole of the persona file.
    private string WriteProfile(string body, string picture = "aurora.png")
    {
        var project = Path.Combine(_root, "project-" + Guid.NewGuid().ToString("N")[..8]);
        var dotClaude = Path.Combine(project, ".claude");
        Directory.CreateDirectory(dotClaude);

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n\n@.claude/PERSONA.MD\n");

        File.WriteAllText(Path.Combine(dotClaude, "PERSONA.MD"), body);
        File.WriteAllBytes(Path.Combine(dotClaude, picture), Png());

        return project;
    }

    private static LocalPersona.Persona Resolve(string project) =>
        LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

    // A profile written entirely in bold fields, which is how somebody who has
    // read the README's own example would write one. Every field, not just the
    // name, because the claim being made is that this shape carries a whole
    // persona rather than that one arm was patched.
    [Fact]
    public void AProfileWrittenAsBoldFieldsUnderAHeadingResolvesWholly()
    {
        var project = WriteProfile(
            "# Aurora\n" +
            "\n" +
            "## Persona\n" +
            "\n" +
            "**Name:** Aurora\n" +
            "**Voice:** af_bella (Kokoro TTS, rate 1.3)\n" +
            "**Profile picture:** aurora.png\n");

        var persona = Resolve(project);

        Assert.Equal("Aurora", persona.Name);
        Assert.Equal("af_bella", persona.Voice);
        Assert.Equal(1.3, persona.Rate);
        Assert.Equal(Path.Combine(project, ".claude", "aurora.png"), persona.AvatarPath);

        // The import is what carried it — nothing walks into
        // `.claude/PERSONA.MD` by name.
        Assert.Contains(Path.Combine(project, ".claude", "PERSONA.MD"), persona.Files);
    }

    // The same persona as a table, header row and separator included, which is
    // the other half of the acceptance criterion and the shape most likely to
    // be confused with a schema table.
    [Fact]
    public void AProfileWrittenAsAnAttributeTableUnderAHeadingResolvesWholly()
    {
        var project = WriteProfile(
            "# Aurora\n" +
            "\n" +
            "## Attributes\n" +
            "\n" +
            "| Field | Value |\n" +
            "| --- | --- |\n" +
            "| Name | Aurora |\n" +
            "| Voice | af_bella (Kokoro TTS, rate 1.3) |\n" +
            "| Profile picture | aurora.png |\n");

        var persona = Resolve(project);

        Assert.Equal("Aurora", persona.Name);
        Assert.Equal("af_bella", persona.Voice);
        Assert.Equal(1.3, persona.Rate);
        Assert.Equal(Path.Combine(project, ".claude", "aurora.png"), persona.AvatarPath);
    }

    // **The case that decided the design, at the level where it would have
    // hurt.** This is an ordinary `CLAUDE.md` in an ordinary repository: a
    // design note with a schema table in it and no persona anywhere. Every
    // session in that repository reads this file, and a bound-only fix would
    // have put the word "string" on every one of their orbs — `NameValue`
    // accepts it, which PersonaScopedNameTests asserts by running it rather
    // than by reading the regex.
    [Fact]
    public void ASchemaTableInAnOrdinaryClaudeMdNamesNoPersona()
    {
        var project = Path.Combine(_root, "ordinary-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(project);

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n" +
            "\n" +
            "## The session record\n" +
            "\n" +
            "| Field | Type |\n" +
            "| --- | --- |\n" +
            "| Name | string |\n" +
            "| Started | timestamp |\n" +
            "\n" +
            "**Name**: the value passed to the constructor, before defaulting\n");

        var persona = Resolve(project);

        Assert.Null(persona.Name);
        Assert.Null(persona.AvatarPath);
    }

    // The two guards, shown to be independent against real files rather than
    // against strings. One fixture is refused for its scope and would pass any
    // bound; the other is refused for its bound and sits under `## Persona`.
    // A regression that removed either guard leaves exactly one of these
    // failing, which is what makes them two tests and not one.
    [Fact]
    public void AWellFormedNameOutsideAPersonaSectionIsRefusedOnScopeAlone()
    {
        var project = WriteProfile(
            "# Aurora\n" +
            "\n" +
            "## Notes for the reader\n" +
            "\n" +
            "**Name:** Aurora\n" +
            "**Profile picture:** aurora.png\n");

        var persona = Resolve(project);

        Assert.Null(persona.Name);

        // The picture is still read, which is what says the file was parsed at
        // all and that this ticket left the unscoped fields alone.
        Assert.Equal(Path.Combine(project, ".claude", "aurora.png"), persona.AvatarPath);
    }

    // A CLAUDE.md that documents its own persona format, which is the shape
    // this repository's own README is. The heading is real, the section is
    // open, and the fenced block is an *example* — before `ScopedName` grew
    // its fence guard, both spellings in it named the orb. At this level the
    // point is that such a file is ordinary: it is what a maintainer writes to
    // tell the next person how to write a persona, and reading it as one is
    // the "shown is not asserted" rule broken.
    [Fact]
    public void AClaudeMdDocumentingThePersonaFormatNamesNoPersona()
    {
        var project = Path.Combine(_root, "docs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(project);

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n" +
            "\n" +
            "## Persona\n" +
            "\n" +
            "Write the agent's attributes as a table:\n" +
            "\n" +
            "```markdown\n" +
            "| Name | Aurora |\n" +
            "| Voice | af_bella |\n" +
            "```\n" +
            "\n" +
            "...or as bold fields:\n" +
            "\n" +
            "```markdown\n" +
            "**Name**: Aurora\n" +
            "```\n");

        Assert.Null(Resolve(project).Name);
    }

    // CB-144 at the file level: a workflow pasted into a real `CLAUDE.md`, on
    // disk, through the real resolver. This is the event the ticket is about —
    // not a fixture shaped to trip a parser, but the ordinary act of pasting a
    // build step into the file every session in the repository reads. Before
    // the fence gate this orb was named "Build the thing".
    //
    // The picture is written beside the file so that a leak would have
    // something to resolve *to*; asserting `AvatarPath` is null against a
    // missing file would pass for the wrong reason.
    [Fact]
    public void AWorkflowPastedIntoAClaudeMdNamesNoPersona()
    {
        var project = Path.Combine(_root, "workflow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(project);
        File.WriteAllBytes(Path.Combine(project, "leota.png"), Png());

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n" +
            "\n" +
            "## Build and run\n" +
            "\n" +
            "```yaml\n" +
            "steps:\n" +
            "  - name: Build the thing\n" +
            "    run: dotnet build\n" +
            "  - **Voice:** af_bella\n" +
            "  - Avatar: leota.png\n" +
            "```\n");

        var persona = Resolve(project);

        Assert.Null(persona.Name);
        Assert.Null(persona.Voice);
        Assert.Null(persona.AvatarPath);
    }

    // The positive control for the case above, at the same level and in the
    // same file shape: unfenced, every one of those fields resolves. A gate
    // that refused everything would pass the test above and fail this one.
    [Fact]
    public void TheSameFieldsUnfencedInAClaudeMdStillResolve()
    {
        var project = Path.Combine(_root, "unfenced-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(project);
        File.WriteAllBytes(Path.Combine(project, "leota.png"), Png());

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n" +
            "\n" +
            "- Name: Aurora\n" +
            "- **Voice:** af_bella\n" +
            "- Avatar: leota.png\n");

        var persona = Resolve(project);

        Assert.Equal("Aurora", persona.Name);
        Assert.Equal("af_bella", persona.Voice);
        Assert.Equal(Path.Combine(project, "leota.png"), persona.AvatarPath);
    }

    // CB-141's marked block, on disk, still read through its fence — the
    // hazard assertion at the file level. If a later change adds a fence guard
    // to the front-matter arm, this is one of the few tests that goes red.
    [Fact]
    public void AProfileGenMarkedBlockOnDiskStillResolvesThroughItsFence()
    {
        var project = Path.Combine(_root, "marked-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(project);
        File.WriteAllBytes(Path.Combine(project, "aurora.png"), Png());

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n" +
            "\n" +
            "<!-- profile-gen:start slug=aurora -->\n" +
            "### Aurora\n" +
            "\n" +
            "```yaml\n" +
            "schema_version: 1\n" +
            "name: \"Aurora\"\n" +
            "slug: \"aurora\"\n" +
            "image: \"aurora.png\"\n" +
            "voice: \"af_bella\"\n" +
            "```\n" +
            "\n" +
            "<!-- profile-gen:end slug=aurora -->\n");

        var persona = Resolve(project);

        Assert.Equal("Aurora", persona.Name);
        Assert.Equal("af_bella", persona.Voice);
        Assert.Equal(Path.Combine(project, "aurora.png"), persona.AvatarPath);
    }

    [Fact]
    public void ASentenceShapedNameInsideAPersonaSectionIsRefusedOnTheBoundAlone()
    {
        var project = WriteProfile(
            "# Aurora\n" +
            "\n" +
            "## Persona\n" +
            "\n" +
            "**Name**: the value passed to the constructor, before defaulting\n" +
            "**Profile picture:** aurora.png\n");

        var persona = Resolve(project);

        Assert.Null(persona.Name);
        Assert.Equal(Path.Combine(project, ".claude", "aurora.png"), persona.AvatarPath);
    }
}
