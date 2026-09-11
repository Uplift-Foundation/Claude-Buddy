using Xunit;

namespace ClaudeBuddy.Tests;

// CB-141 at the seam: profile-gen's `claude-md` output mode, in a real
// CLAUDE.md, on a real disk, through the real resolver.
//
// The distinction this file exists for is the one PersonaRealFileTests states
// and CB-133 paid for: **every test CB-133 shipped was green while this
// repository's own persona file resolved to nothing**, because each of them
// wrote a markdown file designed to make something happen and then asserted
// that it had. A grammar table can only ever say the parser does what the
// parser was written to do. It cannot say a file a real tool really writes is
// read, and that is the claim CB-141 is actually making.
//
// So the fixture below is not written from memory, and it is not a tidied
// version of the shape either. It is the **verbatim output of profile-gen's
// own renderer** — `scripts/profilegen/render.py`'s `render_embedded`, over
// `templates/profile.embedded.md.j2` — transcribed line for line, including
// the three runs of blank lines its optional-field blocks leave behind and the
// unquoted `negative_prompt: blurry` sitting next to quoted neighbours. Those
// are exactly the details a fixture written from memory loses, and CLAUDE.md is
// explicit about what that costs: the permission-dialog parser was first
// written against an invented fixture and failed on every real dialog.
//
// What *is* invented is every value in it — the name, the slug, the paths, the
// seed. This repository is public and the shape is what matters, the same split
// PersonaFrontMatterTests makes. The renderer produced the shape; the values
// were handed to it.
[Collection("Settings")]
public class PersonaEmbeddedBlockFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-141-embedded-" + Guid.NewGuid());

    public PersonaEmbeddedBlockFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    // profile-gen's `claude-md` mode, exactly as its renderer emits it.
    private static readonly string[] EmbeddedBlock =
    {
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
        "image_animated: \"profiles/aurora-vance/aurora-vance.gif\"",
        "",
        "",
        "voice: \"50% sky and 50% nicole\"",
        "",
        "nsfw: false",
        "generation:",
        "  backend: \"comfyui\"",
        "  model: \"mock-v1\"",
        "  prompt: \"portrait of a person\"",
        "  negative_prompt: blurry",
        "  seed: 42",
        "  gif_mode: synthetic",
        "  created_at: \"2026-09-10T00:00:00Z\"",
        "```",
        "",
        "",
        "**Personality:** Warm, precise, and a little wry.",
        "",
        "<!-- profile-gen:end slug=aurora-vance -->",
    };

    // The tree the mode actually produces: the block appended to a CLAUDE.md
    // that already had prose in it, with the picture in the `profiles/<slug>/`
    // subdirectory the template's own relative path names. A subdirectory
    // rather than a sibling because that is where the skill puts it, and
    // because "the picture is beside the markdown" is a weaker claim than the
    // one the guards actually make.
    private string WriteTree(bool writePicture = true)
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);

        File.WriteAllLines(
            Path.Combine(project, "CLAUDE.md"),
            new[]
            {
                "# Working in this repository",
                "",
                "Notes for Claude Code.",
                "",
            }.Concat(EmbeddedBlock));

        if (writePicture)
        {
            var pictures = Path.Combine(project, "profiles", "aurora-vance");
            Directory.CreateDirectory(pictures);
            File.WriteAllBytes(Path.Combine(pictures, "aurora-vance.png"), Png());
        }

        return project;
    }

    private static LocalPersona.Persona Resolve(string project) =>
        LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>());

    // --- acceptance criterion 1 -------------------------------------------

    // **The ticket's own criterion, in one test.** A CLAUDE.md carrying
    // profile-gen's embedded block resolves to the persona's name, voice and
    // picture. Before CB-141 all three were null and the mode produced no
    // persona at all — measured by running this parser over this renderer's
    // real output, not inferred from reading the fence handling.
    [Fact]
    public void ACLaudeMdCarryingProfileGensEmbeddedBlockResolvesNameVoiceAndPicture()
    {
        var project = WriteTree();

        var persona = Resolve(project);

        Assert.Equal("Aurora Vance", persona.Name);
        Assert.Equal("50% sky and 50% nicole", persona.Voice);
        Assert.Equal(
            Path.Combine(project, "profiles", "aurora-vance", "aurora-vance.png"),
            persona.AvatarPath);

        // A path being set and a picture being readable are two different
        // claims, and the second is the one a user cares about — so the bytes
        // are read back through the same guard the decode uses.
        var bytes = PersonaFiles.ReadAvatarFile(persona.AvatarPath!);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes!);

        // The file that carried it, named: a persona resolved from the wrong
        // file in the walk would satisfy every assertion above.
        Assert.Contains(Path.Combine(project, "CLAUDE.md"), persona.Files);
        Assert.Equal(Path.Combine(project, "CLAUDE.md"), persona.AvatarSource);
    }

    // The still wins over the animation on a real disk too, which is worth its
    // own case here rather than only in the grammar table: both files' paths
    // are relative and both resolve, so nothing but the write order decides it.
    [Fact]
    public void TheStillIsThePictureEvenWhenTheAnimationIsAlsoOnDisk()
    {
        var project = WriteTree();
        File.WriteAllBytes(
            Path.Combine(project, "profiles", "aurora-vance", "aurora-vance.gif"), Png());

        var persona = Resolve(project);

        Assert.Equal(
            Path.Combine(project, "profiles", "aurora-vance", "aurora-vance.png"),
            persona.AvatarPath);
    }

    // The name and the voice do not depend on the picture being there. A
    // generated persona whose assets are gitignored — which is a documented
    // profile-gen option — still names its agent on a machine that has only the
    // markdown.
    [Fact]
    public void TheNameAndVoiceSurviveAPictureThatIsNotOnThisMachine()
    {
        var project = WriteTree(writePicture: false);

        var persona = Resolve(project);

        Assert.Equal("Aurora Vance", persona.Name);
        Assert.Equal("50% sky and 50% nicole", persona.Voice);
        Assert.Null(persona.AvatarPath);
    }

    // --- acceptance criterion 2, at the seam -------------------------------

    // **The false positive this must not introduce, on a real disk.** The same
    // CLAUDE.md with the two marker lines taken out and nothing else changed —
    // so the ```yaml block, its `name:`, its `voice:` and its `image:` are all
    // still there, and the picture is still on the disk beside it.
    //
    // Removing exactly the two lines under test is what makes this a
    // measurement rather than a differently-shaped file that happens to resolve
    // to nothing: everything the parser could have keyed on except the marker is
    // held constant.
    [Fact]
    public void TheSameBlockWithoutItsMarkersResolvesToNoPersona()
    {
        var project = Path.Combine(_root, "unmarked");
        Directory.CreateDirectory(project);

        var pictures = Path.Combine(project, "profiles", "aurora-vance");
        Directory.CreateDirectory(pictures);
        File.WriteAllBytes(Path.Combine(pictures, "aurora-vance.png"), Png());

        File.WriteAllLines(
            Path.Combine(project, "CLAUDE.md"),
            EmbeddedBlock.Where(line => !line.StartsWith("<!--", StringComparison.Ordinal)));

        var persona = Resolve(project);

        Assert.Null(persona.Name);
        Assert.Null(persona.Voice);
        Assert.Null(persona.AvatarPath);
    }

    // The ordinary CLAUDE.md this repository is full of: a ```yaml example
    // under a heading, in a file with no persona in it. This is the shape that
    // would have been renamed by "read every yaml fence", and it is written as
    // a whole file on disk rather than a line list because that is how it would
    // arrive.
    [Fact]
    public void AnOrdinaryClaudeMdWithAYamlExampleInItStillHasNoPersona()
    {
        var project = Path.Combine(_root, "documentation");
        Directory.CreateDirectory(project);

        File.WriteAllLines(
            Path.Combine(project, "CLAUDE.md"),
            new[]
            {
                "# Working in this repository",
                "",
                "## Configuration",
                "",
                "A service is declared like this:",
                "",
                "```yaml",
                "name: \"billing-worker\"",
                "slug: \"billing\"",
                "image: \"docs/architecture.png\"",
                "voice: \"af_bella\"",
                "```",
                "",
                "Restart the pod after editing it.",
            });

        var persona = Resolve(project);

        Assert.Null(persona.Name);
        Assert.Null(persona.Voice);
        Assert.Null(persona.AvatarPath);
    }

    // --- the security bounds still hold inside a marked block ---------------

    // A marked block is a wider door into the grammar, and the point of this
    // case is that it is not a door around the *filesystem* guards. The picture
    // path is a string somebody wrote in a markdown file whichever shape it
    // arrived in, and a marker declaring a persona is not a marker declaring
    // that the path is safe to read.
    [Fact]
    public void AMarkedBlockDoesNotLetAPicturePathEscapeItsOwnDirectory()
    {
        var project = Path.Combine(_root, "escape");
        Directory.CreateDirectory(project);

        // A real file that really exists at the far end — a refusal proved
        // against a path that was never there proves nothing.
        var outside = Path.Combine(_root, "outside.png");
        File.WriteAllBytes(outside, Png());

        File.WriteAllLines(
            Path.Combine(project, "CLAUDE.md"),
            new[]
            {
                "<!-- profile-gen:start slug=escape -->",
                "```yaml",
                "name: \"Escape Artist\"",
                "image: \"../outside.png\"",
                "```",
                "<!-- profile-gen:end slug=escape -->",
            });

        var persona = Resolve(project);

        // The name is read — the grammar worked — and the picture is refused.
        Assert.Equal("Escape Artist", persona.Name);
        Assert.Null(persona.AvatarPath);
    }

    // The `@` import path, which is how this repository's own persona is
    // reached and the seam CB-135 fell through: the block is in an imported
    // file rather than in CLAUDE.md itself, and the picture is relative to the
    // *imported* file's directory rather than to the project root.
    [Fact]
    public void AnEmbeddedBlockInsideAnImportedFileIsReadThroughTheImport()
    {
        var project = Path.Combine(_root, "imported");
        var dotClaude = Path.Combine(project, ".claude");
        Directory.CreateDirectory(dotClaude);

        File.WriteAllText(
            Path.Combine(project, "CLAUDE.md"),
            "# Working in this repository\n\n@.claude/PERSONA.MD\n");

        File.WriteAllLines(
            Path.Combine(dotClaude, "PERSONA.MD"),
            new[]
            {
                "<!-- profile-gen:start slug=aurora-vance -->",
                "### Aurora Vance",
                "",
                "```yaml",
                "name: \"Aurora Vance\"",
                "image: \"aurora-vance.png\"",
                "voice: \"af_bella\"",
                "```",
                "<!-- profile-gen:end slug=aurora-vance -->",
            });

        File.WriteAllBytes(Path.Combine(dotClaude, "aurora-vance.png"), Png());

        var persona = Resolve(project);

        Assert.Equal("Aurora Vance", persona.Name);
        Assert.Equal("af_bella", persona.Voice);
        Assert.Equal(Path.Combine(dotClaude, "aurora-vance.png"), persona.AvatarPath);
        Assert.Contains(Path.Combine(dotClaude, "PERSONA.MD"), persona.Files);
    }
}
