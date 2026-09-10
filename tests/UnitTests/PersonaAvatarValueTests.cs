using Xunit;

namespace ClaudeBuddy.Tests;

// The one rule for reading a picture out of markdown, and the four spellings
// that now share it.
//
// This file exists because the rule used to be three rules. The explicit
// bullet arm assigned whatever followed the label; the voice arm two lines
// above it stripped code spans and parenthesised annotations; the prose arm
// took the last token and checked its extension. Each was defensible alone and
// together they meant that `- Profile picture: `avatars/jessica.png`` — the
// shape real profiles are written in — kept its backticks, failed every path
// guard downstream and drew no portrait, in silence, from the day CB-133
// shipped. Twenty-four refusals were sitting in one Mac mini's persona.log when
// CB-135 finally put that log in front of real profiles — eighteen of them
// pictures of exactly this shape, six of them `data:` URIs — and every one was
// filed under a reason that was not the reason.
//
// So the table below is not a list of cases somebody imagined. Its middle group
// is the four distinct shapes as `persona.log` on that machine actually quoted
// them, character for character, and they are the reason for every strip above
// them.
public class PersonaAvatarValueTests
{
    // --- what a picture value may be dressed in ----------------------------

    [Theory]
    // A bare relative path, which is what the README documents and what
    // almost nobody writes.
    [InlineData("avatars/jessica.png", "avatars/jessica.png")]
    [InlineData("cto.png", "cto.png")]
    // A code span, which is what they write instead.
    [InlineData("`avatars/jessica.png`", "avatars/jessica.png")]
    [InlineData("` cto.png `", "cto.png")]
    // A note after it, inside or outside the span — the two orders real files
    // use, which is the whole reason the strip is a loop rather than a
    // sequence.
    [InlineData("avatars/x.png (animated)", "avatars/x.png")]
    [InlineData("`avatars/x.png` (animated)", "avatars/x.png")]
    [InlineData("`avatars/x.png (animated)`", "avatars/x.png")]
    // Words in front of it: a path is one token and the rest is somebody
    // being polite about it.
    [InlineData("the file leota.png", "leota.png")]
    [InlineData("see `avatars/leota.png`", "avatars/leota.png")]
    // A full stop is how a sentence ends and is not part of a filename.
    [InlineData("leota.png.", "leota.png")]
    // Case is the filesystem's business, not the grammar's.
    [InlineData("LEOTA.PNG", "LEOTA.PNG")]
    [InlineData("Portrait.JpEg", "Portrait.JpEg")]
    // Every extension the app can actually decode, and the two CB-139 added a
    // case for because real profiles on the mini use them.
    [InlineData("a.png", "a.png")]
    [InlineData("a.jpg", "a.jpg")]
    [InlineData("a.jpeg", "a.jpeg")]
    [InlineData("a.gif", "a.gif")]
    [InlineData("a.webp", "a.webp")]
    public void AValueDressedAnyWayRealProfilesDressItReadsAsItsPath(string written, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.AvatarValue(written));
    }

    // --- and what it may not be -------------------------------------------

    [Theory]
    // Nothing at all.
    [InlineData("")]
    [InlineData("   ")]
    // A lone backtick, or an empty span: stripping either would leave nothing,
    // so neither is stripped. The same rule VoiceValue applies, for the same
    // reason.
    [InlineData("`")]
    [InlineData("``")]
    // A parenthetical with no path in front of it is a note about a picture
    // rather than a picture.
    [InlineData("(animated, updated 2026-09-09)")]
    // A file with no extension this app can decode, and a word that is not a
    // file at all.
    [InlineData("portrait")]
    [InlineData("leota.bmp")]
    [InlineData("lovely")]
    // A token that is nothing but punctuation, which the trailing-stop strip
    // empties.
    [InlineData("....")]
    // Rooted, on both runners: a leading separator is rooted on Windows too,
    // and a drive letter is refused by the colon before it is asked.
    [InlineData("/etc/passwd.png")]
    [InlineData("/Users/someone/Pictures/portrait.png")]
    [InlineData("C:\\Users\\someone\\portrait.png")]
    // A URL, in a code span or out of one, is not a file beside the markdown.
    [InlineData("https://example.invalid/y.png")]
    [InlineData("`https://example.invalid/y.png`")]
    // And a data: URI, refused deliberately rather than incidentally — see
    // AvatarValue's own comment, and the README sentence it points at.
    [InlineData("data:image/webp;base64,UklGRhYAAABXRUJQVlA4TAoAAAAvAAAAAAfQ//73v/+BiOh/AAA=")]
    [InlineData("`data:image/png;base64,iVBORw0KGgo=`")]
    public void AValueThatIsNotARelativePicturePathReadsAsNothing(string written)
    {
        Assert.Null(PersonaMarkdown.AvatarValue(written));
    }

    // --- markdown that is not quite markdown -------------------------------

    // An unclosed code span. Nothing is stripped, because a span needs both
    // ticks and the rule here is character for character VoiceValue's — read
    // what is there rather than guess at what was meant. So the tick stays on
    // the front of the filename, no file of that name exists, and the value is
    // refused at the filesystem and logged as unreadable with the stray tick
    // visible in the quoted value. That is the outcome somebody can act on:
    // the line shows them their own typo.
    [Fact]
    public void AnUnclosedCodeSpanLeavesItsTickOnTheFilename()
    {
        Assert.Equal("`avatars/x.png", PersonaMarkdown.AvatarValue("`avatars/x.png"));
    }

    // The other half of the same malformation, which lands the other way up:
    // a closing tick with no opening one is not a span either, and this time
    // the leftover tick is on the *end*, so the extension test refuses it and
    // the line says the value was never a path at all. Two spellings of one
    // typo, two different — and each correct — diagnoses.
    [Fact]
    public void AClosingTickWithNoOpeningOneIsNotAPathAtAll()
    {
        Assert.Null(PersonaMarkdown.AvatarValue("avatars/x.png`"));
    }

    // --- the four shapes off the Mac mini, verbatim ------------------------

    // Quoted exactly as `~/Library/Logs/ClaudeBuddy/persona.log` had them when
    // CB-139 was filed: three portraits that had never drawn and one value
    // that never could. A fixture written from memory would have lost the
    // comma in the first note and the space in the second, and both of those
    // are what the last-token rule has to survive.
    [Theory]
    [InlineData("`avatars/annabel-lee.gif` (animated, updated 2026-09-09)", "avatars/annabel-lee.gif")]
    [InlineData("`avatars/lilibeth.png` (AI-generated, cherry blossom portrait)", "avatars/lilibeth.png")]
    [InlineData("`avatars/jessica.png`", "avatars/jessica.png")]
    public void TheThreeRealPortraitsThatNeverDrewNowResolve(string written, string expected)
    {
        Assert.Equal(expected, PersonaMarkdown.AvatarValue(written));
    }

    // The fourth stays refused, and that is the decision rather than an
    // oversight: a persona picture is a relative local path, and the gateway's
    // `avatarUrl` is the only place this app takes base64, because that
    // arrives over the wire rather than out of a file anybody can commit. What
    // changes for it is only the diagnosis — see PersonaRejectionMessageTests
    // and PersonaAvatarValueFileTests.
    [Fact]
    public void TheRealDataUriStaysRefused()
    {
        Assert.Null(PersonaMarkdown.AvatarValue(
            "data:image/webp;base64,UklGRiQAAABXRUJQVlA4TBcAAAAvAAAAAAfQ//73v/+BiOh/AAAAAAAA"));
    }

    // --- the same value, in each of the four explicit spellings ------------

    // The bug was an asymmetry between arms, so the assertion that matters is
    // that no arm is special. One value, written five ways, read five times
    // the same.
    [Theory]
    [InlineData("- Profile picture: `avatars/jessica.png` (animated, updated 2026-09-09)")]
    [InlineData("- **Profile picture:** `avatars/jessica.png` (animated, updated 2026-09-09)")]
    [InlineData("**Profile picture:** `avatars/jessica.png` (animated, updated 2026-09-09)")]
    [InlineData("| Profile picture | `avatars/jessica.png` (animated, updated 2026-09-09) |")]
    public void EverySpellingOfTheBulletGrammarReadsTheSamePicture(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Equal("avatars/jessica.png", fields.Avatar);
    }

    [Fact]
    public void FrontMatterReadsAPictureTheSameWay()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "avatar: `avatars/jessica.png` (animated, updated 2026-09-09)",
            "---",
        });

        Assert.Equal("avatars/jessica.png", fields.Avatar);
    }

    // Front matter, a table and a bold field read only a voice before CB-139 —
    // survivable while the bullet arm was the only place a picture could be
    // written, and not survivable once it normalised, because then the same
    // string means a portrait in one spelling and nothing in another.
    [Theory]
    [InlineData("**Portrait:** cto.png")]
    [InlineData("| Photo | cto.png |")]
    public void ABoldFieldAndATableRowNameAPictureAsWellAsAVoice(string line)
    {
        Assert.Equal("cto.png", PersonaMarkdown.Parse(new[] { line }).Avatar);
    }

    // A heading row is a header and not a value, which the arm decided before
    // pictures reached it and still decides.
    [Fact]
    public void ATablesHeaderRowIsNotAPicture()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "| Picture | Value |",
            "|-----|-----|",
            "| Picture | cto.png |",
        });

        Assert.Equal("cto.png", fields.Avatar);
    }

    // Precedence is unchanged: the first statement wins, and a second one in a
    // different spelling does not take it.
    [Fact]
    public void TheFirstPictureStatedIsStillTheOne()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Avatar: first.png",
            "**Picture:** second.png",
            "| Photo | third.png |",
        });

        Assert.Equal("first.png", fields.Avatar);
        Assert.Equal("first.png", fields.RawAvatar);
    }

    // A picture label whose value does not normalise does not consume the
    // field either — a later line that does name a path still wins it, which
    // is the same "first *usable* value" rule every other field follows.
    [Fact]
    public void AValueThatReadsAsNothingDoesNotBlockALaterOneThatDoes()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "- Avatar: data:image/webp;base64,UklGRhYAAABXRUJQ",
            "- Avatar: portrait.png",
        });

        Assert.Equal("portrait.png", fields.Avatar);
        Assert.Equal("data:image/webp;base64,UklGRhYAAABXRUJQ", fields.RawAvatar);
    }

    // --- the raw value, and who is allowed to record one -------------------

    // The seam the log hangs off: a picture label named something, and the
    // grammar could not read it as a path, so the value survives on Fields for
    // PersonaFiles to write down. Without this the resolver sees a null avatar
    // and cannot tell "nobody named a picture" from "somebody named one this
    // parser cannot use" — which is exactly why all twenty-four lines on the
    // mini said "unreadable — it is missing" about values that were never
    // files.
    [Theory]
    [InlineData("- Avatar: data:image/webp;base64,UklGRhYAAABXRUJQ", "data:image/webp;base64,UklGRhYAAABXRUJQ")]
    [InlineData("- Profile picture: https://example.invalid/y.png", "https://example.invalid/y.png")]
    [InlineData("- Photo: /Users/someone/portrait.png", "/Users/someone/portrait.png")]
    [InlineData("- Portrait: lovely", "lovely")]
    [InlineData("**Picture:** data:image/png;base64,iVBOR", "data:image/png;base64,iVBOR")]
    [InlineData("| Photo | data:image/png;base64,iVBOR |", "data:image/png;base64,iVBOR")]
    public void ALabelledValueThatIsNotAPathIsKeptSoItCanBeReportedOn(string line, string raw)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Null(fields.Avatar);
        Assert.Equal(raw, fields.RawAvatar);
    }

    [Fact]
    public void FrontMatterKeepsAnUnreadableValueToo()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "---",
            "avatar: data:image/webp;base64,UklGRhYAAABXRUJQ",
            "---",
        });

        Assert.Null(fields.Avatar);
        Assert.Equal("data:image/webp;base64,UklGRhYAAABXRUJQ", fields.RawAvatar);
    }

    // A value that reads fine keeps its raw form as well, because "what was
    // written" is a fact about the file rather than a flag about the outcome —
    // the one place that acts on it decides what the pair means.
    [Fact]
    public void AValueThatReadsFineKeepsItsRawFormBesideIt()
    {
        var fields = PersonaMarkdown.Parse(new[] { "- Avatar: `avatars/jessica.png` (animated)" });

        Assert.Equal("avatars/jessica.png", fields.Avatar);
        Assert.Equal("`avatars/jessica.png` (animated)", fields.RawAvatar);
    }

    // A file that names no picture at all records nothing, which is what keeps
    // the log about pictures somebody wrote rather than about markdown in
    // general — a persona scan reads every CLAUDE.md up a directory tree.
    [Theory]
    [InlineData("- Name: Leota")]
    [InlineData("- Voice: Bella")]
    [InlineData("- Other: ignored")]
    [InlineData("just some ordinary prose")]
    [InlineData("**Mood:** cheerful")]
    [InlineData("| Mood | cheerful |")]
    public void AFileThatNamesNoPictureRecordsNothingToReportOn(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    [Fact]
    public void FrontMatterWithNoPictureInItRecordsNothingEither()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "mood: cheerful", "voice: Ava", "---" });

        Assert.Null(fields.RawAvatar);
    }

    // A placeholder is somebody's template, not somebody's picture, and it is
    // refused before the raw value is kept — otherwise every unfilled profile
    // on the machine would write a line into persona.log saying that
    // `<your picture>` is not a path, which is true and useless.
    [Theory]
    [InlineData("- Avatar: <your picture>")]
    [InlineData("- Profile photo:")]
    public void APlaceholderOrAnEmptyFieldIsNotAPictureAnybodyNamed(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    [Fact]
    public void AFrontMatterPlaceholderIsNotAPictureAnybodyNamedEither()
    {
        var fields = PersonaMarkdown.Parse(new[] { "---", "avatar: <your picture>", "---" });

        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    // Prose is deliberately not allowed to record one, and this is the case
    // that says why. "Her profile picture is lovely" is a sentence about a
    // picture and names no file; it never meant to. An explicit arm has a
    // label in front of the value — somebody wrote `Profile picture:` and then
    // wrote something — so a value that reads as nothing there is a mistake
    // worth naming, and the same value in a sentence is just English.
    [Theory]
    [InlineData("Her profile picture is lovely")]
    [InlineData("Her portrait is https://example.invalid/y.png")]
    [InlineData("The photo is /Users/someone/portrait.png")]
    public void AProseSentenceThatNamesNoFileStaysSilent(string line)
    {
        var fields = PersonaMarkdown.Parse(new[] { line });

        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    // ...and neither does the colon-less form under a persona heading, for the
    // same reason: `Profile Photo is inherited from the team` is a sentence
    // that happens to start with a label, and CB-135's whole defence of that
    // arm is that the value has to pass the same bounds prose does.
    [Fact]
    public void AColonLessSectionLineThatNamesNoFileStaysSilentToo()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "",
            "Profile Photo is inherited from the team",
        });

        Assert.Null(fields.Avatar);
        Assert.Null(fields.RawAvatar);
    }

    // The colon-less arm runs the same helper as everything else, so a code
    // span works there too — which nobody asked for and which would have been
    // a second rule if it did not.
    [Fact]
    public void AColonLessSectionLineReadsACodeSpannedPictureToo()
    {
        var fields = PersonaMarkdown.Parse(new[]
        {
            "## Attributes",
            "",
            "Profile Photo `cto.png` (the good one)",
        });

        Assert.Equal("cto.png", fields.Avatar);
    }
}
