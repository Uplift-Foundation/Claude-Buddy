using Xunit;

namespace ClaudeBuddy.Tests;

// A name written as a standalone bold field or a two-cell table row, and the
// persona heading that is the only place either is read (CB-142).
//
// The defect this covers is the drift `PersonaMarkdown`'s own header comment
// names: four grammars that disagreed about which fields they carry. A name
// was the field they disagreed about, and `**Name:** Leota` — the spelling the
// README used as its *example* of a bold field — named nobody at all.
//
// What makes this suite worth its length is that the obvious fix does not
// work. Adding `NameLabel` and CB-140's `NameValue` to both arms and stopping
// there accepts `| Name | string |` out of a schema-documentation table,
// because "string" is one short word of ordinary characters and that is the
// whole of what the bound asks. The measurement is asserted below rather than
// described, because CLAUDE.md is explicit that paraphrasing the rule you are
// measuring is how this project has been wrong before — and a paraphrase here
// ("the bound would surely refuse a type name") is exactly the paraphrase that
// would have shipped the defect.
//
// So the suite is organised around the two guards being *independent*: there
// is a line each one alone lets through, and neither guard catches both.
public class PersonaScopedNameTests
{
    // --- the measurement that decided the design --------------------------

    // Run, not read. `NameValue` is pure and cheap to call for precisely this
    // reason, and this is the single fact the scoping rule rests on: the bound
    // accepts a schema table's type name, so the bound cannot be the whole
    // answer.
    [Fact]
    public void TheBoundAloneAcceptsASchemaTablesTypeName()
    {
        Assert.Equal("string", PersonaMarkdown.NameValue("string"));
    }

    // The other half of the same pair, and the reason scope is not the whole
    // answer either: a sentence long enough to be prose is refused on word
    // count wherever it is written.
    [Fact]
    public void TheBoundRefusesASentenceWhereverItIsWritten()
    {
        Assert.Null(PersonaMarkdown.NameValue("the value passed to the constructor"));
    }

    // --- the positive controls --------------------------------------------

    // Both bold spellings, because `BoldField` accepts both and a rule that
    // reads only one of them is a rule people have to learn.
    [Theory]
    [InlineData("**Name**: Aurora")]
    [InlineData("**Name:** Aurora")]
    [InlineData("**name**: Aurora")]
    public void ABoldNameUnderAPersonaHeadingNamesThePersona(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { "## Persona", "", line });

        Assert.Equal("Aurora", fields.Name);
    }

    [Theory]
    [InlineData("| Name | Aurora |")]
    [InlineData("| name | Aurora |")]
    [InlineData("|Name|Aurora|")]
    public void ATableNameUnderAPersonaHeadingNamesThePersona(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { "## Persona", "", line });

        Assert.Equal("Aurora", fields.Name);
    }

    // `Slug` is a name label everywhere else in this grammar, so it is one
    // here — the arms share `NameLabel` rather than keeping a list each,
    // which is the whole point of there being one list.
    [Fact]
    public void ASlugUnderAPersonaHeadingNamesThePersonaToo()
    {
        Assert.Equal("aurora", PersonaMarkdown.Parse(new[]
        {
            "## Identity",
            "| Slug | aurora |",
        }).Name);
    }

    // Every heading word the section rule knows, since a persona file written
    // under `## About me` is the same declaration as one under `## Persona`
    // and this arm has no reason to recognise fewer of them than the
    // colon-less arm does.
    [Theory]
    [InlineData("## Persona")]
    [InlineData("## Attributes")]
    [InlineData("# Identity")]
    [InlineData("### Her character")]
    [InlineData("## Agent profile")]
    [InlineData("# About me")]
    [InlineData("## Who I am")]
    public void AnyPersonaHeadingOpensTheScopeForABoldName(string heading)
    {
        Assert.Equal("Aurora", PersonaMarkdown.Parse(new[] { heading, "**Name:** Aurora" }).Name);
    }

    // The shape somebody actually writes when they write a persona as a
    // table: a header row, a separator, and then the fields. The header row
    // is a `| Field | Value |` two-cell row like any other and its value
    // passes the bound easily — the separator underneath it is the only thing
    // that stops "Value" being read as the persona's name, which is the guard
    // this ticket had to preserve rather than invent.
    [Fact]
    public void ARealisticPersonaAttributeTableIsReadPastItsHeaderRow()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Aurora",
            "",
            "## Attributes",
            "",
            "| Field | Value |",
            "| --- | --- |",
            "| Name | Aurora |",
            "| Voice | af_bella (Kokoro TTS, rate 1.3) |",
            "| Profile picture | aurora.png |",
        });

        Assert.Equal("Aurora", fields.Name);
        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
        Assert.Equal("aurora.png", fields.Avatar);
    }

    // --- the required negative controls -----------------------------------

    // (a) The line the bound alone would have accepted. Every part of this
    // fixture is ordinary technical documentation — a schema table in a
    // design note, with no persona anywhere near it — and the word "string"
    // is what would have landed on an orb.
    [Fact]
    public void ASchemaTablesNameRowOutsideAPersonaSectionNamesNobody()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## The session record",
            "",
            "| Field | Type |",
            "| --- | --- |",
            "| Name | string |",
            "| Started | timestamp |",
        });

        Assert.Null(fields.Name);
    }

    // (b) The line scope alone would have accepted: it is *inside* a persona
    // section, and it is refused anyway, on the same word count that refuses
    // it as prose. This is what makes the two guards complementary rather
    // than one of them being decoration.
    [Fact]
    public void AProseSentenceInABoldFieldIsRefusedEvenInsideAPersonaSection()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "",
            "**Name**: the value passed to the constructor",
        });

        Assert.Null(fields.Name);
    }

    // ...and the same sentence in a table cell, since the two arms share one
    // function and a regression would have to break both at once for only one
    // of these to fail.
    [Fact]
    public void AProseSentenceInATableCellIsRefusedEvenInsideAPersonaSection()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "| Name | the value passed to the constructor |",
        }).Name);
    }

    // (c) The line the bound alone would have accepted for the opposite
    // reason: "Aurora" passes every bound there is, and it is refused because
    // it is written in an ordinary paragraph of an ordinary document. Between
    // this case and (b), each guard has a line the other one misses.
    [Theory]
    [InlineData("**Name**: Aurora")]
    [InlineData("**Name:** Aurora")]
    [InlineData("| Name | Aurora |")]
    public void AWellFormedNameOutsideAnyPersonaSectionIsStillRefused(string line)
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Working in this repository",
            "",
            line,
        });

        Assert.Null(fields.Name);
    }

    // A section that has ended is not a section. `## Persona` opens one and
    // the next heading of its own level closes it, so a bold name three
    // paragraphs later is in the build instructions rather than the profile.
    [Fact]
    public void ABoldNameAfterThePersonaSectionHasClosedIsNotRead()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "",
            "**Name:** Aurora",
            "",
            "## Build and run",
            "",
            "**Name:** Whatever",
        });

        Assert.Equal("Aurora", fields.Name);
    }

    [Fact]
    public void ATableNameInASectionThatNeverOpenedIsNotRead()
    {
        Assert.Null(PersonaMarkdown.Parse(new[]
        {
            "## Build and run",
            "| Name | Aurora |",
        }).Name);
    }

    // A heading inside a fenced block is a comment in somebody's shell
    // example, so it opens nothing — and the bold field underneath it is
    // being *shown*, not asserted.
    [Fact]
    public void APersonaHeadingInsideAFenceOpensNoScope()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "```markdown",
            "## Persona",
            "",
            "**Name:** Aurora",
            "```",
        });

        Assert.Null(fields.Name);
    }

    // --- first statement wins ---------------------------------------------

    // The `??=` skip, per arm. Until a file states a name twice this half of
    // each assignment never runs, and it is the precedence rule — the thing
    // most likely to be got wrong by a later edit and least likely to be
    // noticed, because the symptom is an orb wearing the wrong one of two
    // names both of which are in the file.
    [Fact]
    public void ASecondBoldNameUnderAHeadingDoesNotOverrideTheFirst()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "**Name:** Aurora",
            "**Name:** Someone Else",
        });

        Assert.Equal("Aurora", fields.Name);
    }

    [Fact]
    public void ASecondTableNameUnderAHeadingDoesNotOverrideTheFirst()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "| Name | Aurora |",
            "| Name | Someone Else |",
        });

        Assert.Equal("Aurora", fields.Name);
    }

    // A bullet is unscoped and a bold field is not, so a file with both has
    // already been named by the time the scoped arm runs. The bullet wins
    // because it is first, not because it is a bullet.
    [Fact]
    public void ABulletNameStatedFirstIsNotOverriddenByABoldOneInASection()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Name: Leota",
            "",
            "## Persona",
            "**Name:** Aurora",
        });

        Assert.Equal("Leota", fields.Name);
    }

    // ...and the other way round, which is the case that says the scoped arm
    // assigns rather than merely claiming the line.
    [Fact]
    public void ABoldNameInASectionIsNotOverriddenByALaterBullet()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "**Name:** Aurora",
            "",
            "- Name: Leota",
        });

        Assert.Equal("Aurora", fields.Name);
    }

    // --- what this ticket deliberately did not move ------------------------

    // The out-of-scope guarantee, asserted rather than assumed. A voice and a
    // picture are read from a bold field and a table row **outside** any
    // heading, exactly as they were before CB-142, because real profiles
    // write them that way — a redacted OpenClaw `IDENTITY.md` in this suite's
    // own fixtures is a bare `**Voice:** af_bella (Kokoro TTS)` under a title
    // that is not a persona heading at all.
    [Theory]
    [InlineData("**Voice:** af_bella (Kokoro TTS)")]
    [InlineData("| Voice | af_bella (Kokoro TTS) |")]
    public void AVoiceOutsideAnyPersonaSectionIsStillRead(string line)
    {
        Assert.Equal("af_bella", PersonaMarkdown.Parse(new[] { line }).Voice);
    }

    [Theory]
    [InlineData("**Portrait:** leota.png")]
    [InlineData("| Profile picture | leota.png |")]
    public void APictureOutsideAnyPersonaSectionIsStillRead(string line)
    {
        Assert.Equal("leota.png", PersonaMarkdown.Parse(new[] { line }).Avatar);
    }

    // ...and inside one, which is the arm where a label that is not a name
    // label falls through the new check to the voice and picture arms below
    // it. Without this case the scoped arm could refuse every line it saw and
    // the suite would not notice.
    [Fact]
    public void AVoiceAndAPictureInsideAPersonaSectionAreStillRead()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "**Voice:** af_bella (Kokoro TTS)",
            "| Portrait | leota.png |",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal("leota.png", fields.Avatar);
    }

    // A recognised name label whose value fails the bound is not claimed — it
    // falls through to the arms below, which recognise `Name` as neither a
    // voice nor a picture, so the line ends up read by nobody and the fields
    // a later line states are unaffected. The name being null is the refusal;
    // the voice being read is what says the refusal did not swallow the rest
    // of the file.
    [Fact]
    public void ARefusedNameDoesNotStopTheRestOfTheSectionBeingRead()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "| Name | the value passed to the constructor |",
            "| Voice | af_bella (Kokoro TTS) |",
        });

        Assert.Null(fields.Name);
        Assert.Equal("af_bella", fields.Voice);
    }

    // The README's own example, which is the sentence CB-142 exists to make
    // true again — under a heading, as the README now says it must be.
    [Fact]
    public void TheReadmesOwnBoldFieldExampleNamesLeota()
    {
        Assert.Equal("Leota", PersonaMarkdown.Parse(new[]
        {
            "## Persona",
            "",
            "**Name:** Leota",
            "**Profile picture:** leota.png",
            "**Voice:** Bella",
        }).Name);
    }
}
