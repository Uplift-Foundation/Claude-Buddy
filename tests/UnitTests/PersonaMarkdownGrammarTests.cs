using Xunit;

namespace ClaudeBuddy.Tests;

// The explicit half of PersonaMarkdown — the bullets, bold fields, table rows
// and front-matter keys that came out of OpenClawWorkspaceIdentity unchanged.
//
// OpenClawWorkspaceIdentityTests already covers what each of those shapes
// means, and still does; this file covers the arms nobody had reached, which
// moving the code into its own file turned from invisible into a number. Two
// kinds of gap, and they are different kinds:
//
//   * "the field was already set" — every arm here assigns with ??=, and until
//     now every test set each field exactly once, so the *skip* half of each
//     of those had never run. That half is the precedence rule, which is the
//     thing most likely to be got wrong by a later edit and the thing least
//     likely to be noticed: a file states a voice twice and the wrong one
//     speaks.
//   * malformed input the shapes have to survive — an unclosed bold marker, a
//     table row whose neighbour is prose, a separator with a stray character
//     in it, a parenthesis where the engine annotation should be.
public class PersonaMarkdownGrammarTests
{
    // --- first statement wins, per shape ---------------------------------

    [Fact]
    public void AFrontMatterVoiceStatedTwiceKeepsTheFirst()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "voice: af_bella (Kokoro TTS, rate 1.3)",
            "voice: Samantha",
            "---",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
    }

    [Fact]
    public void ATableRowDoesNotOverrideAVoiceAlreadyStated()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Voice: af_bella (Kokoro TTS, rate 1.3)",
            "| Voice | Samantha |",
            "| TTS Voice | Ava |",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
    }

    [Fact]
    public void ABoldFieldDoesNotOverrideAVoiceAlreadyStated()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Voice: af_bella (Kokoro TTS, rate 1.3)",
            "**Voice:** Samantha",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
    }

    [Fact]
    public void ProseDoesNotOverrideAVoiceAlreadyStatedAsAField()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Voice: af_bella (Kokoro TTS, rate 1.3)",
            "Her voice is Samantha",
        });

        Assert.Equal("af_bella", fields.Voice);
        Assert.Equal(1.3, fields.Rate);
    }

    [Fact]
    public void TheFirstPictureNamedInProseIsTheOne()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "Her profile picture is leota.png",
            "Her portrait is somebody-else.png",
        });

        Assert.Equal("leota.png", fields.Avatar);
    }

    // --- shapes that are nearly one of the above ---------------------------

    // An opening bold marker with no closing one is emphasis somebody started
    // and did not finish, or a line of asterisks. Either way there is no label
    // in it.
    [Fact]
    public void AnUnclosedBoldMarkerIsNotABoldField()
    {
        Assert.Null(PersonaMarkdown.Parse(new[] { "**Voice: Samantha" }).Voice);
    }

    // A table row is recognised by having exactly two cells, and the row after
    // it decides whether it is a *header* rather than a value — but the row
    // after it is often not a row at all.
    [Fact]
    public void ATableRowFollowedByOrdinaryProseIsStillARow()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "| Voice | Ava |",
            "and then some ordinary prose",
        });

        Assert.Equal("Ava", fields.Voice);
    }

    // A separator is dashes and colons and nothing else. One stray character
    // and the line below is a value row, which makes the line above a value
    // row too rather than a header to be skipped.
    [Fact]
    public void ARowWhoseNeighbourIsNotQuiteASeparatorIsStillARow()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "| Voice | Ava |",
            "|---x|---|",
        });

        Assert.Equal("Ava", fields.Voice);
    }

    [Fact]
    public void ARealSeparatorMakesTheRowAboveItAHeader()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "| Voice | Value |",
            "|-----|-----|",
            "| Voice | Ava |",
        });

        Assert.Equal("Ava", fields.Voice);
    }

    // --- the engine annotation --------------------------------------------

    // Every spelling of an engine that the annotation stripper knows, because
    // a profile writing one it does not know keeps the parenthesis as part of
    // the voice name — and "Ava (Premium)" proves that is the right default
    // rather than an oversight.
    [Theory]
    [InlineData("kokoro")]
    [InlineData("Kokoro TTS")]
    [InlineData("neural")]
    [InlineData("Neural TTS")]
    [InlineData("system")]
    [InlineData("System Voice")]
    [InlineData("custom")]
    [InlineData("Custom Voice")]
    [InlineData("tts")]
    public void AnEngineAnnotationIsNotPartOfTheVoiceName(string engine)
    {
        Assert.Equal("Ava", PersonaMarkdown.Parse(new[] { $"- Voice: Ava ({engine})" }).Voice);
    }

    [Fact]
    public void AParenthesisThatIsNotAnEngineStaysInTheName()
    {
        Assert.Equal("Ava (Premium)", PersonaMarkdown.Parse(new[] { "- Voice: Ava (Premium)" }).Voice);
    }

    // A value that is *nothing but* a parenthesised engine has no name in
    // front of it to strip back to, so there is nothing to strip and the value
    // stands as written. Refusing it instead would be inventing a rule: this
    // parser's job is to read what is there, and what is there is a voice
    // nothing on this machine will match.
    [Fact]
    public void AValueThatIsOnlyAnAnnotationIsLeftAlone()
    {
        Assert.Equal("(Kokoro TTS)", PersonaMarkdown.Parse(new[] { "- Voice: (Kokoro TTS)" }).Voice);
    }

    // --- values that are not values ----------------------------------------

    // The bullet arm checks a value before handing it to the voice grammar, so
    // a placeholder reaching VoiceValue at all takes a different route: front
    // matter, which has no such pre-check.
    [Fact]
    public void APlaceholderVoiceInFrontMatterIsNotAVoice()
    {
        Assert.Null(PersonaMarkdown.Parse(new[] { "---", "voice: <your voice>", "---" }).Voice);
    }

    [Fact]
    public void AFrontMatterKeyWithNothingAfterItIsNotAVoice()
    {
        Assert.Null(PersonaMarkdown.Parse(new[] { "---", "voice:", "---" }).Voice);
    }

    // --- the bullet Name arm stays unscoped, on purpose (CB-140 §C) --------
    //
    // NameLabel/NameValue could have been restricted to run only inside a
    // persona heading, the way SectionField's own arms already are — that
    // would have made an ordinary `- **Name**: ...` bullet in a bare
    // CLAUDE.md stop naming an orb, which reads like exactly the fix this
    // ticket was about. It was rejected: OpenClaw's own IDENTITY.md is a
    // bare bulleted list with no heading at all
    // (OpenClawWorkspaceIdentityTests' own fixtures are exactly that shape),
    // so section-scoping the bullet arm would break every shipped profile
    // rather than fix the one that misbehaved. What actually misbehaved was
    // that this was the only name-producing arm with no bound at all — see
    // AFiveWordNameCarryingASlashAndACommaNamesNothing for the front-matter
    // half of the same fix. The bound is what does the work; the arm stays
    // reachable from a bare bullet exactly as it always was.
    //
    // These two sit together deliberately, the same value shape CB-140 was
    // filed over on each side of the bound: short and clean still wins,
    // long and sentence-shaped still loses, and both are read straight off a
    // bullet with no heading above it.
    [Fact]
    public void AShortBoundedBulletNameWithNoHeadingAboveItStillNamesTheOrb()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Notes",
            "- **Name**: Jane Doe",
        });

        Assert.Equal("Jane Doe", fields.Name);
    }

    [Fact]
    public void ABulletNameThatReadsAsASentenceRatherThanANameNamesNothing()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "# Notes",
            "- **Name**: Jordan Casey, MBA / MSc",
        });

        Assert.Null(fields.Name);
    }
}
