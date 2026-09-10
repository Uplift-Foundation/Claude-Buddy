using Xunit;

namespace ClaudeBuddy.Tests;

// PersonaMarkdown's prose arm — the half of the grammar that exists because a
// CLAUDE.md is not a profile.
//
// The three positive fixtures at the top are copied verbatim from a real
// CLAUDE.md on the machine this was written for, lines 9 to 11 of
// HauntedMansionTerminalTheme's, and they are the whole reason the arm exists:
// nobody writes `- Name: Leota` in a file they are addressing Claude in. Every
// other positive case is a variation someone plausibly writes; every negative
// case is a sentence that mentions a name, a voice or a picture without stating
// one, which is the failure mode the bounds are for.
public class PersonaMarkdownProseTests
{
    // --- the real lines, exactly as they are written ---------------------

    [Fact]
    public void TheThreeRealProseLinesAreReadAsAPersona()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Haunted Mansion Terminal Theme",
            "",
            "Her name is Leota",
            "Her profile picture is leota.png",
            "Her voice is Bella",
        });

        Assert.Equal("Leota", fields.Name);
        Assert.Equal("leota.png", fields.Avatar);
        Assert.Equal("Bella", fields.Voice);
        Assert.Null(fields.Rate);
    }

    // --- the shape, in every spelling it plausibly arrives in ------------

    [Theory]
    [InlineData("Her name is Leota", "Leota")]
    [InlineData("His name is Jarvis", "Jarvis")]
    [InlineData("Their name is Nova", "Nova")]
    [InlineData("Its name is Aurora", "Aurora")]
    [InlineData("The name is Aurora", "Aurora")]
    [InlineData("My name is Zoe", "Zoe")]
    [InlineData("Your name is Zoe", "Zoe")]
    [InlineData("Agent name is Nova", "Nova")]
    [InlineData("The agent's name is Aurora", "Aurora")]
    [InlineData("This agent's name is Aurora", "Aurora")]
    [InlineData("Name is Leota", "Leota")]
    [InlineData("HER NAME IS Leota", "Leota")]
    [InlineData("her name is leota", "leota")]
    [InlineData("Her name should be Leota", "Leota")]
    [InlineData("Her name will be Leota", "Leota")]
    [InlineData("Her name is: Leota", "Leota")]
    [InlineData("Her name is Leota.", "Leota")]
    [InlineData("  Her name is Leota  ", "Leota")]
    [InlineData("Her name is Madame Leota", "Madame Leota")]
    [InlineData("Her name is Madame Leota Ghost", "Madame Leota Ghost")]
    [InlineData("Her name is O'Brien", "O'Brien")]
    [InlineData("Her name is agent-7", "agent-7")]
    public void AStatedNameIsRead(string line, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { line }).Name);
    }

    [Theory]
    [InlineData("Her voice is Bella", "Bella")]
    [InlineData("Her speaking voice is Bella", "Bella")]
    [InlineData("Her TTS voice is af_nicole", "af_nicole")]
    [InlineData("His voice is Ava (Premium)", "Ava (Premium)")]
    [InlineData("Their voice is af_bella", "af_bella")]
    [InlineData("Her voice is af_bella (Kokoro TTS)", "af_bella")]
    public void AStatedVoiceIsRead(string line, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { line }).Voice);
    }

    [Theory]
    [InlineData("Her profile picture is leota.png", "leota.png")]
    [InlineData("Her profile pic is leota.jpg", "leota.jpg")]
    [InlineData("Her profile image is leota.jpeg", "leota.jpeg")]
    [InlineData("Her picture is leota.gif", "leota.gif")]
    [InlineData("Her portrait is leota.webp", "leota.webp")]
    [InlineData("Her avatar is leota.PNG", "leota.PNG")]
    [InlineData("Her image is leota.png", "leota.png")]
    [InlineData("Her portrait is portraits/me.png", "portraits/me.png")]
    // The last token, so a sentence that is polite about its own path still
    // names the file it names.
    [InlineData("Her picture is the file leota.png", "leota.png")]
    public void AStatedPictureIsRead(string line, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { line }).Avatar);
    }

    // --- the negative table ----------------------------------------------

    [Theory]
    // No verb: a colon after the noun is metadata's shape, and the explicit
    // grammar already refuses it unless it is on a bullet. This is the case
    // OnlyExplicitBulletFieldsAreReadAndPlaceholdersDoNotWin has asserted since
    // before prose was read at all, and it must keep meaning the same thing.
    [InlineData("Name: prose is not metadata")]
    // A sentence about naming. Word count is what tells it from a name.
    [InlineData("The name is derived from the folder unless the user renames it")]
    // Four words of description, not a voice.
    [InlineData("Her voice is lovely and warm and low")]
    // A URL is not a relative picture, and has a colon in it besides.
    [InlineData("Her picture is https://x/y.png")]
    // Rooted, so it is not somewhere beside the file that named it.
    [InlineData("Its avatar is /etc/passwd")]
    [InlineData("Her picture is /etc/shadow.png")]
    // A second clause after a colon.
    [InlineData("Her name is Leota: the ghost")]
    // Not a picture at all.
    [InlineData("Her picture is notes.txt")]
    // Punctuation a real identifier does not carry.
    [InlineData("Her name is Leota!")]
    // A slash in a name is a path, and a name is not a path.
    [InlineData("Her name is voices/bella")]
    // Even without a colon or a slash, a name that is a scheme is a URL
    // somebody has half-typed.
    [InlineData("Her name is http")]
    // The verb with nothing after it but whitespace, once the line has been
    // trimmed: there is no sentence left for the shape to match.
    [InlineData("Her name is \t")]
    // The noun has to open the sentence. Mid-sentence, "voice" is a passing
    // mention, and reading one would be exactly the silent change to speech
    // this grammar exists to prevent.
    [InlineData("The picture in the header is leota.png")]
    [InlineData("When you speak, her voice is what you should use")]
    // Deliberate Markdown metadata shapes are read by their own arms, or not
    // at all; neither is the prose arm's business.
    [InlineData("**Name:** Leota")]
    [InlineData("| Name | Leota |")]
    [InlineData("- Her name is Leota")]
    public void ASentenceThatMerelyMentionsAFieldIsNotRead(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
    }

    // --- where prose is not read at all ----------------------------------

    // The bound is on the whole value, not on its words: three words can still
    // be a sentence fragment if they are long enough.
    [Fact]
    public void AValuePastTheLengthBoundIsNotAName()
    {
        Assert.Null(PersonaMarkdown.Parse(new[] { "Her name is " + new string('a', 41) }).Name);
        Assert.Equal(new string('a', 40), PersonaMarkdown.Parse(new[] { "Her name is " + new string('a', 40) }).Name);
    }

    // Reached by calling ProseField rather than Parse, and deliberately so:
    // Parse trims every line before it gets here, and this is the one shape a
    // trimmed line cannot have. The arm still has to exist — ProseField is
    // internal, callable, and the regex is perfectly happy to match a value
    // group that is nothing but a tab.
    [Fact]
    public void AValueThatIsOnlyWhitespaceNamesNothing()
    {
        Assert.False(PersonaMarkdown.ProseField("Her name is \t\t", out var kind, out _));
        Assert.Equal(PersonaMarkdown.ProseKind.None, kind);
    }

    // The noun's own words may be separated by any whitespace, which is what
    // Collapse is for — a tab between "profile" and "picture" is a writer's
    // formatting, not a different field.
    [Fact]
    public void WhitespaceInsideATwoWordNounIsFlattened()
    {
        Assert.Equal("leota.png", PersonaMarkdown.Parse(new[] { "Her profile\tpicture is leota.png" }).Avatar);
    }

    [Fact]
    public void ProseInsideFrontMatterIsNotRead()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "Her name is Leota",
            "---",
        });

        Assert.Null(fields.Name);
    }

    // A fenced block is where a CLAUDE.md shows you what to write. Text being
    // shown is not text being asserted — otherwise this very repository's
    // documentation of the feature would rename every orb that reads it.
    [Fact]
    public void ProseInsideAFencedCodeBlockIsNotRead()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "For example:",
            "```markdown",
            "Her name is Example",
            "```",
        });

        Assert.Null(fields.Name);
    }

    [Fact]
    public void AFenceClosesAndProseAfterItIsReadAgain()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "~~~",
            "Her name is Example",
            "~~~",
            "Her name is Leota",
        });

        Assert.Equal("Leota", fields.Name);
    }

    // --- precedence within one document ----------------------------------

    [Fact]
    public void TheFirstStatementOfAFieldWins()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "Her name is Leota",
            "Her name is Somebody Else",
        });

        Assert.Equal("Leota", fields.Name);
    }

    // An explicit field is not outranked by prose merely for appearing later:
    // whichever states the field first owns it, which is the same rule the
    // bullet grammar has always used against itself.
    [Fact]
    public void ProseAndAnExplicitBulletFillDifferentFieldsInOneFile()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "Her name is Leota",
            "- Voice: Samantha",
        });

        Assert.Equal("Leota", fields.Name);
        Assert.Equal("Samantha", fields.Voice);
    }

    [Fact]
    public void AProseVoiceCarriesItsRateThroughTheSharedVoiceGrammar()
    {
        // Not reachable through the prose bounds, which refuse a comma — the
        // rate rides in on the explicit grammar, and this asserts the two arms
        // still agree about what a voice value means after the extraction.
        var fields = PersonaMarkdown.Parse(new[] { "- Voice: af_bella (Kokoro TTS, rate 1.3)" });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
    }

    // --- ProseField itself, for the kinds it reports ---------------------

    // The kind travels as its name rather than as the enum: ProseKind is
    // internal, an xUnit test method has to be public, and a public method
    // cannot take an internal parameter. Asserting on ToString() costs nothing
    // — a renamed member fails this the same way a changed answer would.
    [Theory]
    [InlineData("Her name is Leota", "Name", "Leota")]
    [InlineData("Her voice is Bella", "Voice", "Bella")]
    [InlineData("Her profile picture is leota.png", "Avatar", "leota.png")]
    public void ProseFieldReportsWhichFieldWasNamed(
        string line, string expectedKind, string expectedValue)
    {
        Assert.True(PersonaMarkdown.ProseField(line, out var kind, out var value));
        Assert.Equal(expectedKind, kind.ToString());
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void ProseFieldReportsNothingForALineThatIsNotOne()
    {
        Assert.False(PersonaMarkdown.ProseField("just a sentence", out var kind, out var value));
        Assert.Equal(PersonaMarkdown.ProseKind.None, kind);
        Assert.Equal("", value);
    }

    // --- the picture labels, shared with the explicit grammar -------------

    [Theory]
    [InlineData("Avatar")]
    [InlineData("avatar")]
    [InlineData("Profile Picture")]
    [InlineData("profile pic")]
    [InlineData("Profile Image")]
    [InlineData("Picture")]
    [InlineData("Portrait")]
    [InlineData("Image")]
    public void EveryWordForAPictureIsAPictureLabel(string label)
    {
        Assert.True(PersonaMarkdown.AvatarLabel(label));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Voice")]
    [InlineData("Other")]
    [InlineData("Profile")]
    public void AnythingElseIsNotAPictureLabel(string label)
    {
        Assert.False(PersonaMarkdown.AvatarLabel(label));
    }
}
