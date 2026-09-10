using Xunit;

namespace ClaudeBuddy.Tests;

// The colon-less `Label Value` form, and the persona section that is the only
// place it is read.
//
// This exists because CB-133 shipped a grammar whose every test was green while
// **this repository's own persona file produced nothing at all**. That file is
// not exotic: it names a name, a profile photo and a voice, under a `##
// Attributes` heading, in a file `CLAUDE.md` imports on line 12 and the
// resolver provably reads. It failed because none of its three lines has a verb
// in it, and the prose arm requires one.
//
// So the first fixture below is that file, verbatim, and it is the point of the
// suite rather than one case in it. Everything after it is the scope that makes
// reading such a line safe — a persona section and nothing else — because the
// same words outside one are ordinary English that a CLAUDE.md is full of.
public class PersonaSectionGrammarTests
{
    // The repository's own `.claude/PERSONA.MD`, line for line. If this ever
    // stops naming Jennifer, an orb somewhere has stopped wearing her name.
    private static string[] TheRealFile() => new[]
    {
        "# Claude Buddy Persona",
        "",
        "I'm a female AI Architect who built this cute little Claudy Buddy Agentic AI harness.  ",
        "",
        "I'm the CTO in charge of the project and I give the orders.",
        "",
        "## Attributes",
        "",
        "Name Jennifer",
        "Profile Photo cto.png",
        "Voice is 50% sky and 50% nicole",
    };

    [Fact]
    public void TheRepositorysOwnPersonaFileNamesJenniferAndHerPhoto()
    {
        var fields = PersonaMarkdown.Parse(TheRealFile());

        Assert.Equal("Jennifer", fields.Name);
        Assert.Equal("cto.png", fields.Avatar);
    }

    // **This assertion is CB-135's, changed on purpose by CB-136.**
    //
    // It used to read `Assert.Null(...Voice)`, with a comment saying the voice
    // line was deliberately not that ticket's to read: `%` is not in the
    // character whitelist and five words is over the word cap, and both of
    // those bounds — along with what a blend of two voices would even mean —
    // belonged to CB-136. It was written as an assertion rather than left
    // silent precisely so that whichever ticket landed second had to come here
    // and change it deliberately. This is that change, and the line the file
    // has always contained is now read.
    //
    // What the value *is* at this level is the raw text. Resolving `sky` to
    // `af_sky` needs to know what this machine has installed, which a grammar
    // test must not, so that half is VoiceBlendTests' and the end-to-end half
    // is PersonaRealFileTests'.
    [Fact]
    public void TheBlendedVoiceLineIsReadNowThatCB136GivesABlendSomewhereToGo()
    {
        Assert.Equal("50% sky and 50% nicole", PersonaMarkdown.Parse(TheRealFile()).Voice);
    }

    // ...and it really is a blend rather than a string that happens to have
    // survived the bounds. Running the grammar rather than restating it, which
    // is what CLAUDE.md asks for by name: the parser is pure and cheap to
    // call, so nobody has to paraphrase what it accepts.
    [Fact]
    public void TheVoiceTheRealFileNamesParsesAsAFiftyFiftyMixture()
    {
        var blend = VoiceBlend.Parse(PersonaMarkdown.Parse(TheRealFile()).Voice);

        Assert.NotNull(blend);
        Assert.Equal(new[] { "sky", "nicole" }, blend!.Parts.Select(part => part.Voice));
        Assert.Equal(new[] { 50, 50 }, blend.Parts.Select(part => part.Percent));
    }

    // The bound that makes the widening safe, at the level it was widened.
    // Eleven words and a hundred and twenty characters is a large allowance
    // on paper, and two conditions guard it: the value carries a `%`, and
    // VoiceBlend can read it as a mixture. A sentence of the same length is
    // still refused by one or the other.
    [Theory]
    [InlineData("Voice is 50% sky and 50% nicole", "50% sky and 50% nicole")]
    [InlineData("Voice is 50% sky", "50% sky")]
    [InlineData("Voice is 34% sky, 33% nicole and 33% bella", "34% sky, 33% nicole and 33% bella")]
    [InlineData("Voice is af_bella", "af_bella")]
    // Two weightless parts fit the *narrow* bound unchanged — three words —
    // and are a blend downstream all the same.
    [InlineData("Voice is sky and nicole", "sky and nicole")]
    public void AVoiceValueMayBeAMixtureNowAndIsStillReadWordForWord(string line, string expected) =>
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { "## Attributes", line }).Voice);

    [Theory]
    [InlineData("Voice is 50% a matter of taste, plus tone")]
    [InlineData("Voice is 60% sky and 60% nicole")]
    [InlineData("Voice is the one the user picked in settings unless overridden")]
    [InlineData("Voice is https://example.invalid/voices and more")]
    // A slash with no colon in front of it, so the path half of that guard is
    // reached on its own rather than short-circuited by the colon.
    [InlineData("Voice is 50% voices/af_sky and 50% nicole")]
    // The sentence that made the percentage mandatory. Five single tokens
    // joined by "and" is structurally a three-part equal blend and nothing
    // can tell it from one — see BlendShaped.
    [InlineData("Voice is lovely and warm and low")]
    // ...and the rest of the widened bound's own refusals, each one the
    // narrow bound already had and the wide one keeps.
    [InlineData("Voice is 20% one and 20% two and 20% three and 20% four and 20% five")]  // 12 words
    [InlineData("Voice is 25% aaaaaaaaaaaaaaaaaaaaaaaaa and 25% bbbbbbbbbbbbbbbbbbbbbbbbb "
                + "and 25% ccccccccccccccccccccccccc and 25% ddddddddddddddddddddddddd")]  // 130 characters
    [InlineData("Voice is 50% http and 50% sky")]        // "http" with no colon or slash
    [InlineData("Voice is 50% sky! and 50% nicole")]     // a mark no identifier carries
    public void AValueThatIsNotAVoiceAndNotAMixtureIsStillRefused(string line) =>
        Assert.Null(PersonaMarkdown.Parse(new[] { "## Attributes", line }).Voice);

    // The colon-less form gets the same allowance as the sentence, because
    // `Voice 50% sky and 50% nicole` under a heading is the same statement as
    // the sentence with "is" in it.
    [Fact]
    public void TheColonLessFormReadsAMixtureToo() =>
        Assert.Equal(
            "50% sky and 50% nicole",
            PersonaMarkdown.Parse(new[] { "## Attributes", "Voice 50% sky and 50% nicole" }).Voice);

    // ...and a *name* did not move an inch. The widening is the voice value's
    // and the voice value's only, which is what "this ticket owns the voice
    // value's bounds" has to mean if CB-135's own refusals are to keep
    // holding.
    [Theory]
    [InlineData("Name 50% Jennifer and 50% Leota")]
    [InlineData("Name resolution is handled by the folder")]
    public void ANameValueKeepsTheNarrowBoundItAlwaysHad(string line) =>
        Assert.Null(PersonaMarkdown.Parse(new[] { "## Attributes", line }).Name);

    // The same three lines with the heading taken away. This is the whole of
    // the safety argument: the form is not a new grammar for markdown, it is a
    // grammar for the inside of a section that says it describes an agent.
    [Fact]
    public void TheSameLinesOutsideAPersonaSectionNameNothing()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Claude Buddy",
            "",
            "Name Jennifer",
            "Profile Photo cto.png",
        });

        Assert.Null(fields.Name);
        Assert.Null(fields.Avatar);
    }

    // ...and inside one, the bounds still do the work the prose arm's bounds
    // do. A sentence about naming is not a name however encouraging its
    // heading is.
    [Fact]
    public void ASentenceAboutNamingInsideASectionIsStillNotAName()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "Name resolution is handled by the folder",
        });

        Assert.Null(fields.Name);
    }

    // --- which headings open a section -----------------------------------

    [Theory]
    [InlineData("## Persona")]
    [InlineData("## Attributes")]
    [InlineData("## Identity")]
    [InlineData("## Character")]
    [InlineData("## Profile")]
    [InlineData("## About me")]
    [InlineData("## Who I am")]
    // Contains rather than equals: the word is what matters, not the sentence
    // it is written into.
    [InlineData("## Her persona, in short")]
    [InlineData("# Agent profile")]
    [InlineData("###### attributes")]
    // A closing run of hashes is decoration.
    [InlineData("## Attributes ##")]
    public void APersonaHeadingOpensTheSection(string heading)
    {
        Assert.Equal("Jennifer", PersonaMarkdown.Parse(new[] { heading, "Name Jennifer" }).Name);
    }

    [Theory]
    [InlineData("## Installation")]
    [InlineData("## Working in this repository")]
    [InlineData("# Claude Buddy")]
    // Seven hashes is not a heading, so this is an ordinary paragraph and the
    // section never opens.
    [InlineData("####### Persona")]
    // Nor is a hashtag: CommonMark wants whitespace after the run.
    [InlineData("#Persona")]
    public void AnyOtherHeadingDoesNot(string heading)
    {
        Assert.Null(PersonaMarkdown.Parse(new[] { heading, "Name Jennifer" }).Name);
    }

    // --- where the section ends -------------------------------------------

    [Fact]
    public void AHeadingOfTheSameLevelClosesTheSection()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "## Installation",
            "Name Jennifer",
        });

        Assert.Null(fields.Name);
    }

    [Fact]
    public void AHigherHeadingClosesItToo()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "### Persona",
            "# Something else entirely",
            "Name Jennifer",
        });

        Assert.Null(fields.Name);
    }

    // A subsection is still inside the section it is under, which is what
    // makes a `### Voice` beneath `## Attributes` mean what it looks like.
    [Fact]
    public void ADeeperHeadingLeavesTheSectionOpen()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "### Voice",
            "Name Jennifer",
        });

        Assert.Equal("Jennifer", fields.Name);
    }

    // Two persona sections in one file, with ordinary prose between them: the
    // second opens again rather than being swallowed by the first having
    // closed.
    [Fact]
    public void ASecondPersonaSectionOpensAgain()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "Name Jennifer",
            "## Build and run",
            "Voice af_bella",
            "## Identity",
            "Voice af_nicole",
        });

        Assert.Equal("Jennifer", fields.Name);
        Assert.Equal("af_nicole", fields.Voice);
    }

    // --- what a section still refuses -------------------------------------

    // A fenced block is where a document *shows* you what to write, and it is
    // no more assertive inside a persona section than anywhere else —
    // otherwise the README's own example would rename the orb of anyone whose
    // repository contains it.
    [Fact]
    public void AFencedBlockInsideASectionIsStillShownRatherThanAsserted()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "```markdown",
            "Name Example",
            "```",
            "Name Jennifer",
        });

        Assert.Equal("Jennifer", fields.Name);
    }

    // A heading inside a fence is a comment in somebody's shell example, so it
    // opens nothing.
    [Fact]
    public void AHeadingInsideAFenceOpensNoSection()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "```bash",
            "# Persona",
            "```",
            "Name Jennifer",
        });

        Assert.Null(fields.Name);
    }

    [Fact]
    public void FrontMatterIsUntouchedByTheSectionRule()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "# Persona",
            "Name Jennifer",
            "---",
        });

        Assert.Null(fields.Name);
    }

    // The rule that has been asserted since before prose was read at all, and
    // which the colon-less form must not disturb: a colon after the noun is
    // metadata's shape and is only read on a bullet. "Name:" is not the label
    // "Name", so this never reaches the new arm.
    [Fact]
    public void AColonAfterTheLabelIsStillNotReadOffABareLine()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "Name: prose is not metadata",
        });

        Assert.Null(fields.Name);
    }

    [Theory]
    // Over the word cap.
    [InlineData("Name a b c d")]
    // Over the length cap.
    [InlineData("Voice aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    // A placeholder somebody has not filled in.
    [InlineData("Name <your name>")]
    // Not a picture at all.
    [InlineData("Profile Photo notes.txt")]
    // Rooted, and a URL.
    [InlineData("Photo /etc/passwd.png")]
    [InlineData("Picture https://example.test/x.png")]
    // A label with nothing after it.
    [InlineData("Name")]
    // Not a label this grammar knows.
    [InlineData("Nickname Jennifer")]
    [InlineData("Profile cto.png")]
    public void AValueThatFailsTheBoundsNamesNothingEvenInsideASection(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { "## Attributes", line });

        Assert.Null(fields.Name);
        Assert.Null(fields.Voice);
        Assert.Null(fields.Avatar);
    }

    // --- what a section reads ---------------------------------------------

    [Theory]
    [InlineData("Name Jennifer", "Jennifer")]
    [InlineData("name jennifer", "jennifer")]
    [InlineData("Name Madame Leota", "Madame Leota")]
    // Whatever spacing the writer used collapses, the way the prose arm's own
    // two-word nouns do.
    [InlineData("Name    Jennifer", "Jennifer")]
    public void ANameIsReadOffAColonLessLine(string line, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { "## Attributes", line }).Name);
    }

    [Theory]
    [InlineData("Voice af_bella", "af_bella")]
    [InlineData("TTS Voice af_nicole", "af_nicole")]
    [InlineData("Speech Voice Samantha", "Samantha")]
    [InlineData("Voice Name Samantha", "Samantha")]
    [InlineData("Voice Ava (Premium)", "Ava (Premium)")]
    // The shared voice grammar still strips an engine annotation, because the
    // colon-less arm hands its value to the same function every other arm
    // does.
    [InlineData("Voice af_bella (Kokoro TTS)", "af_bella")]
    public void AVoiceIsReadOffAColonLessLine(string line, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { "## Attributes", line }).Voice);
    }

    [Theory]
    [InlineData("Profile Photo cto.png", "cto.png")]
    [InlineData("Photo cto.png", "cto.png")]
    [InlineData("Avatar cto.jpg", "cto.jpg")]
    [InlineData("Portrait pictures/cto.webp", "pictures/cto.webp")]
    [InlineData("Profile Picture cto.gif", "cto.gif")]
    // The last token, the same rule the prose arm uses.
    [InlineData("Picture the file cto.jpeg", "cto.jpeg")]
    public void APictureIsReadOffAColonLessLine(string line, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.Parse(new[] { "## Attributes", line }).Avatar);
    }

    // --- precedence -------------------------------------------------------

    [Fact]
    public void TheFirstStatementStillWinsAcrossTheTwoArms()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "Her name is Leota",
            "## Attributes",
            "Name Jennifer",
        });

        Assert.Equal("Leota", fields.Name);
    }

    [Fact]
    public void AColonLessFieldDoesNotOutrankABulletStatedEarlier()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Voice: af_bella (Kokoro TTS, rate 1.3)",
            "## Attributes",
            "Voice Samantha",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
    }

    // Bullets go on meaning what they meant inside a persona section — the new
    // arm runs only for lines no other arm claimed.
    [Fact]
    public void ABulletInsideASectionIsStillABullet()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "- Name: Jennifer",
            "- Profile photo: cto.png",
        });

        Assert.Equal("Jennifer", fields.Name);
        Assert.Equal("cto.png", fields.Avatar);
    }

    // The file the prose arm was built from, unchanged by any of this. It has
    // no persona heading in it at all, which is exactly the point: the two
    // shapes are independent and a file may use either.
    [Fact]
    public void TheLeotaProseFileStillParses()
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
    }

    // --- the pieces, directly ---------------------------------------------

    [Theory]
    [InlineData("# One", 1, "One")]
    [InlineData("###### Six", 6, "Six")]
    [InlineData("## Attributes ##", 2, "Attributes")]
    [InlineData("##", 2, "")]
    public void AHeadingReportsItsLevelAndItsText(string line, int level, string text)
    {
        Assert.True(PersonaMarkdown.Heading(line, out var readLevel, out var readText));
        Assert.Equal(level, readLevel);
        Assert.Equal(text, readText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name Jennifer")]
    [InlineData("#Persona")]
    [InlineData("####### Persona")]
    public void AnythingElseIsNotAHeading(string line)
    {
        Assert.False(PersonaMarkdown.Heading(line, out var level, out var text));
        Assert.Equal(0, level);
        Assert.Equal("", text);
    }

    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("voice", "Voice")]
    [InlineData("TTS Voice", "Voice")]
    [InlineData("Profile Photo", "Avatar")]
    [InlineData("photo", "Avatar")]
    public void ARecognisedLabelReportsWhichFieldItIs(string label, string expectedKind)
    {
        Assert.True(PersonaMarkdown.SectionLabel(label, out var kind));
        Assert.Equal(expectedKind, kind.ToString());
    }

    [Theory]
    [InlineData("Nickname")]
    [InlineData("Profile")]
    [InlineData("Other")]
    public void AnythingElseIsNotALabel(string label)
    {
        Assert.False(PersonaMarkdown.SectionLabel(label, out var kind));
        Assert.Equal(PersonaMarkdown.ProseKind.None, kind);
    }

    [Fact]
    public void SectionFieldReportsNothingForALineWithNoValue()
    {
        Assert.False(PersonaMarkdown.SectionField("Name", out var kind, out var value));
        Assert.Equal(PersonaMarkdown.ProseKind.None, kind);
        Assert.Equal("", value);
    }

    // --- the photo labels, which are new everywhere -----------------------

    [Theory]
    [InlineData("Photo")]
    [InlineData("photo")]
    [InlineData("Profile Photo")]
    [InlineData("profile photo")]
    public void PhotoIsAPictureLabel(string label)
    {
        Assert.True(PersonaMarkdown.AvatarLabel(label));
    }

    // Every shape that reads a picture label at all: prose, a bullet, a
    // bulleted bold field, and the colon-less form. The bare bold, table and
    // front-matter arms read a *voice* and nothing else — for every label, not
    // just this one — which is CB-133's grammar unchanged and not this
    // ticket's to widen.
    [Theory]
    [InlineData("Her photo is cto.png")]
    [InlineData("Her profile photo is cto.png")]
    [InlineData("- Photo: cto.png")]
    [InlineData("- Profile photo: cto.png")]
    [InlineData("- **Profile photo:** cto.png")]
    public void PhotoNamesAPictureInEveryShapeThatReadsOne(string line)
    {
        Assert.Equal("cto.png", PersonaMarkdown.Parse(new[] { line }).Avatar);
    }
}
