using Xunit;

namespace ClaudeBuddy.Tests;

// CB-140's own reason to exist: a persona file written by profile-gen, a
// separate skill that is currently shipping and writes YAML front matter with
// a `name:`, a `slug:`, an absolute `image:`, an absolute `image_animated:`
// and a quoted `voice:` — a shape PersonaMarkdown's own header comment already
// claimed to read ("a bullet, a bold field, a table row, a front-matter key")
// and did not, for `name:`, at all.
//
// The fixture below is that shape, synthetic rather than a real user's
// persona: this repository is public, and the shape is what matters — a
// front-matter key, a quoted scalar, an absolute path with a space in it —
// not whose face or name it names. Every absolute path is built from
// Path.GetTempPath()/Path.DirectorySeparatorChar rather than a literal
// `/abs`, so a fixture that is rooted on this machine is rooted on the
// Windows runner too, which a literal leading `/` is not.
public class PersonaFrontMatterTests
{
    private static string Abs(params string[] parts) =>
        Path.Combine(new[] { Path.GetTempPath(), "cb-140 persona" }.Concat(parts).ToArray());

    // The headline fixture: every field profile-gen writes, unquoted where
    // YAML quotes it, with the still (`image:`) winning over the animation
    // because it is written first — see TheStillWinsOverTheAnimationWrittenAfterIt
    // for that half asserted on its own.
    [Fact]
    public void ProfileGensExactStandaloneShapeResolvesNameVoiceAndAvatar()
    {
        var image = Abs(".claude", "persona", "avatar.png");
        var animated = Abs(".claude", "persona", "avatar.gif");

        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "schema_version: 1",
            "name: \"Test Persona\"",
            "slug: \"test-persona\"",
            "image: \"" + image + "\"",
            "image_animated: \"" + animated + "\"",
            "voice: \"af_bella\"",
            "---",
        });

        Assert.Equal("Test Persona", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(image, fields.Avatar);
    }

    // --- name: and slug:, and which wins --------------------------------

    [Fact]
    public void SlugAloneNamesTheAgentWhenThereIsNoNameField()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "slug: \"test-persona\"", "---" });

        Assert.Equal("test-persona", fields.Name);
    }

    [Fact]
    public void NameWinsOverSlugWhenBothArePresent()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "name: \"Test Persona\"",
            "slug: \"test-persona\"",
            "---",
        });

        Assert.Equal("Test Persona", fields.Name);
    }

    // --- YAML's own quoting -----------------------------------------------

    [Fact]
    public void SingleQuotesAreStrippedTheSameAsDoubleOnes()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "voice: 'af_bella'", "---" });

        Assert.Equal("af_bella", fields.Voice);
    }

    [Fact]
    public void AnEmptyQuotedValueNamesNoField()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "voice: \"\"", "---" });

        Assert.Null(fields.Voice);
    }

    // A lone, unmatched quote is left exactly as written — Unquoted requires
    // a matched pair — and a bare `"` then fails BoundedWords the same as any
    // other punctuation mark not on its whitelist, so this comes out null for
    // the same reason a comma or a slash would, not through any special
    // handling of quotes.
    [Fact]
    public void ALoneUnmatchedQuoteNamesNoName()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "name: \"", "---" });

        Assert.Null(fields.Name);
    }

    // A pair that opens with one mark and closes with the other is not a
    // matched pair — Unquoted requires both ends to agree — so it is left
    // exactly as written, quotes and all, and then refused by the ordinary
    // character bound the same as any other stray punctuation would be.
    [Fact]
    public void MismatchedOpeningAndClosingQuotesAreNotStripped()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "voice: \"af_bella'", "---" });

        Assert.Equal("\"af_bella'", fields.Voice);
    }

    // The mismatch the other way round: opens with a single quote, closes
    // with a double one. Unquoted asks the same two questions regardless of
    // which mark opens the value, and this is the one input shape that
    // reaches the second question (a single-quote opener) and answers no.
    [Fact]
    public void AValueOpeningWithASingleQuoteAndClosingWithADoubleOneIsNotStripped()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "voice: 'af_bella\"", "---" });

        Assert.Equal("'af_bella\"", fields.Voice);
    }

    // A bullet, a bold field and a table cell are Markdown a person typed —
    // the unquoting strip is a YAML affordance and runs nowhere else, so a
    // quote written there is part of the value.
    [Fact]
    public void ABulletsQuotesAreNotStrippedTheWayFrontMatterSIs()
    {
        var fields = PersonaMarkdown.Parse(new[] { "- Voice: \"af_bella\"" });

        Assert.Equal("\"af_bella\"", fields.Voice);
    }

    // --- image_animated: (§D) ----------------------------------------------

    [Fact]
    public void ImageAnimatedAloneNamesThePicture()
    {
        var animated = Abs("avatar.gif");
        var fields = PersonaMarkdown.Parse(new[] { "---", "image_animated: \"" + animated + "\"", "---" });

        Assert.Equal(animated, fields.Avatar);
    }

    // §D's consequence, asserted so it reads as a decision and not an
    // oversight: image: is written first by every real generator, so
    // first-wins means the still — not the animation — is what a persona's
    // Avatar ends up naming.
    [Fact]
    public void TheStillWinsOverTheAnimationWrittenAfterIt()
    {
        var still = Abs("avatar.png");
        var animated = Abs("avatar.gif");

        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "image: \"" + still + "\"",
            "image_animated: \"" + animated + "\"",
            "---",
        });

        Assert.Equal(still, fields.Avatar);
    }

    // --- the negative controls ----------------------------------------------

    // Every nested key under generation: in a real profile-gen file, measured
    // off it rather than imagined — none of them is a name, a voice or a
    // picture label, so none of them may set a field, however plausible a
    // paraphrase of "prompt" or "model" might sound as one.
    [Theory]
    [InlineData("schema_version")]
    [InlineData("nsfw")]
    [InlineData("generation")]
    [InlineData("backend")]
    [InlineData("model")]
    [InlineData("prompt")]
    [InlineData("negative_prompt")]
    [InlineData("seed")]
    [InlineData("gif_mode")]
    [InlineData("created_at")]
    public void EveryNestedGenerationKeyNamesNoField(string key)
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            key + ": some-value-that-is-not-a-field",
            "---",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
    }

    // The defect this ticket's own bullet-arm fix targets, restated at the
    // front-matter arm: NameValue runs the same BoundedWords a prose or
    // colon-less name passes, so a five-word value carrying a `/` and a `,`
    // is refused here exactly as it would be anywhere else in this grammar.
    [Fact]
    public void AFiveWordNameCarryingASlashAndACommaNamesNothing()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "name: \"Jordan Casey, MBA / MSc\"",
            "---",
        });

        Assert.Null(fields.Name);
    }
}
