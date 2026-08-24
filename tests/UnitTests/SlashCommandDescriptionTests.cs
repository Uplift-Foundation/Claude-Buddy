using Xunit;

namespace ClaudeBuddy.UnitTests;

// The description a slash command shows in the popup, parsed from the file that
// defines it.
//
// Both CLIs let a command describe itself two different ways — a YAML frontmatter
// block, or just its first line of prose — and this has to take whichever is
// there. It is the whole of what is interesting in that file, and it is now
// separate from reading the file so it can be exercised with strings rather than
// fixtures.
public class SlashCommandDescriptionTests
{
    // ---- frontmatter -------------------------------------------------------

    [Fact]
    public void AFrontmatterDescriptionIsPreferred()
    {
        var text = "---\ndescription: Deploy to staging\n---\n\nLong prose nobody wants in a menu.";

        Assert.Equal("Deploy to staging", SlashCommandCatalog.DescriptionIn(text));
    }

    // Case-insensitive, because the key is typed by hand in a file the user owns.
    [Fact]
    public void TheFrontmatterKeyIsCaseInsensitive()
    {
        var text = "---\nDescription: Deploy to staging\n---\nbody";

        Assert.Equal("Deploy to staging", SlashCommandCatalog.DescriptionIn(text));
    }

    // Quotes come off, since YAML permits them and a menu should not show them.
    [Fact]
    public void QuotesAroundTheDescriptionAreStripped()
    {
        var text = "---\ndescription: \"Deploy to staging\"\n---\nbody";

        Assert.Equal("Deploy to staging", SlashCommandCatalog.DescriptionIn(text));
    }

    // Other frontmatter keys are ignored rather than shown.
    [Fact]
    public void OtherFrontmatterKeysAreNotUsed()
    {
        var text = "---\nname: deploy\nmodel: opus\ndescription: Ship it\n---\nbody";

        Assert.Equal("Ship it", SlashCommandCatalog.DescriptionIn(text));
    }

    // ---- the first-line fallback -------------------------------------------

    [Fact]
    public void WithNoFrontmatterTheFirstRealLineIsUsed()
    {
        Assert.Equal("Run the tests",
            SlashCommandCatalog.DescriptionIn("Run the tests\n\nand then report."));
    }

    [Fact]
    public void BlankLeadingLinesAreSkipped()
    {
        Assert.Equal("Run the tests",
            SlashCommandCatalog.DescriptionIn("\n\n   \nRun the tests\n"));
    }

    // A frontmatter block with no description key falls through to the prose
    // after it — and the closing --- is not mistaken for the description.
    [Fact]
    public void FrontmatterWithoutADescriptionFallsThroughPastItsFence()
    {
        var text = "---\nname: deploy\n---\nRun the tests\n";

        Assert.Equal("name: deploy", SlashCommandCatalog.DescriptionIn(text));
    }

    // A file that is only a fence describes nothing rather than describing "---".
    [Fact]
    public void AFileThatIsOnlyFencesDescribesNothing()
    {
        Assert.Equal("", SlashCommandCatalog.DescriptionIn("---\n---\n"));
    }

    [Fact]
    public void AnEmptyFileDescribesNothing()
    {
        Assert.Equal("", SlashCommandCatalog.DescriptionIn(""));
        Assert.Equal("", SlashCommandCatalog.DescriptionIn("   \n\n  "));
    }

    // An unterminated frontmatter block — someone opened one and never closed it
    // — must not swallow the file. It falls back to the first line.
    [Fact]
    public void AnUnterminatedFrontmatterBlockDoesNotSwallowTheFile()
    {
        var text = "---\ndescription: Deploy to staging\n";

        Assert.NotEqual("", SlashCommandCatalog.DescriptionIn(text));
    }

    // ---- length -------------------------------------------------------------

    // Truncated, because this becomes one line of an autocomplete popup and a
    // paragraph would push the input box off the screen — the same reason the
    // command list itself is capped.
    [Fact]
    public void AVeryLongDescriptionIsShortened()
    {
        var text = "---\ndescription: " + new string('x', 500) + "\n---\n";

        var description = SlashCommandCatalog.DescriptionIn(text);

        Assert.True(description.Length < 500,
            $"expected a truncated description, got {description.Length} characters");
    }

    [Fact]
    public void AVeryLongFirstLineIsShortenedToo()
    {
        var description = SlashCommandCatalog.DescriptionIn(new string('y', 500));

        Assert.True(description.Length < 500);
    }
}
