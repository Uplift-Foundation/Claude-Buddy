using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.Tests;

// A handful of assertions on ClaudeDesktopColors.cs's public surface. For(),
// NameFor() and HexFor() all read through ClaudeBuddySettings.For(folderName)
// first, which is safe here only because TestBootstrap has already pointed
// CLAUDE_BUDDY_SETTINGS_DIR at an isolated temp directory.
[Collection("Settings")]
public class ClaudeDesktopColorsTests
{
    [Fact]
    public void ByName_IsCaseInsensitiveAndFallsBackToDefaultSlate()
    {
        Assert.Equal(Color.Parse("#00AF5F"), ClaudeDesktopColors.ByName("green"));
        Assert.Equal(Color.Parse("#00AF5F"), ClaudeDesktopColors.ByName("GREEN"));

        // Unrecognised name -> the reserved "Default profile" slate, not a
        // thrown exception or a random palette pick.
        Assert.Equal(Color.Parse("#5B7A94"), ClaudeDesktopColors.ByName("not-a-real-colour"));
    }

    [Fact]
    public void Names_ListsEveryNamedColourExceptNoneAreDuplicated()
    {
        var names = ClaudeDesktopColors.Names;

        Assert.Contains("green", names);
        Assert.Contains("slate", names);
        Assert.Equal(names.Count, new System.Collections.Generic.HashSet<string>(names).Count);
    }

    [Fact]
    public void HexFor_DefaultProfileIsTheReservedSlateWithoutAHashPrefix()
    {
        // Unlike OrbColors.ToHex and AgentPalette.HexFor (both "#RRGGBB"),
        // this one omits the leading '#' — `$"{c.R:X2}{c.G:X2}{c.B:X2}"` has
        // no literal '#' in front of it. Worth calling out because the other
        // two hex helpers in this codebase disagree with it.
        var hex = ClaudeDesktopColors.HexFor("Default", isDefault: true);

        Assert.Equal("5B7A94", hex);
        Assert.DoesNotContain("#", hex);
    }

    // Folder names are settings keys, so every test below works on one nobody
    // else could be using — the assembly shares one settings.json (see
    // TestBootstrap) and a name left set is a name the next test reads.
    private static string UniqueFolder() => "Claude-cbtest-" + Guid.NewGuid().ToString("N");

    // The derived colour is the feature's "profiles are whatever is on disk"
    // property: nothing is stored, so the same folder name has to hash to the
    // same palette entry every time. FNV-1a rather than string.GetHashCode is
    // what makes that true, and the reason is in the source — .NET randomises
    // string hashing per process, so the alternative gives a profile a
    // different colour on every launch.
    //
    // A second process is what actually proves that, and one test cannot be
    // one, so this pins the *values* instead: each expectation was computed
    // outside this codebase (FNV-1a over the UTF-16 code units, modulo the
    // eight-entry palette) rather than read off a run, so it is a check on the
    // implementation and not a copy of it. A change to Fnv1a or to the palette
    // order breaks these, which is the point — either one silently recolours
    // every profile on somebody's machine.
    [Theory]
    [InlineData("Claude-work", "#00AFAF")]
    [InlineData("Claude-personal", "#D7AF5F")]
    [InlineData("Claude-Profile-1", "#5F87D7")]
    public void ADerivedColourIsFixedByTheFolderName(string folder, string expected)
    {
        Assert.Equal(Color.Parse(expected), ClaudeDesktopColors.For(folder, isDefault: false));
    }

    [Fact]
    public void ADerivedColourIsAlwaysOneOfTheNamedOnes()
    {
        // Not the reserved slate, either: that one belongs to the Default
        // profile, and a created profile landing on it would make the two
        // indistinguishable in the menu.
        for (var i = 0; i < 200; i++)
        {
            var name = ClaudeDesktopColors.NameFor("Claude-" + i, isDefault: false);

            Assert.Contains(name, ClaudeDesktopColors.Names);
            Assert.NotEqual("slate", name);
        }
    }

    // An explicit choice in settings beats both the derived colour and the
    // Default profile's reserved slate — including for Default itself, which is
    // how "Default is the one I tinted green" is possible at all.
    [Fact]
    public void AnExplicitColourBeatsBothTheDerivedOneAndTheDefaultSlate()
    {
        var folder = UniqueFolder();
        try
        {
            var derived = ClaudeDesktopColors.For(folder, isDefault: false);
            ClaudeBuddySettings.Update(folder, p => p.Color = "magenta");

            Assert.Equal(Color.Parse("#D787AF"), ClaudeDesktopColors.For(folder, isDefault: false));
            Assert.NotEqual(derived, ClaudeDesktopColors.For(folder, isDefault: false));

            // isDefault: true would otherwise return the slate.
            Assert.Equal(Color.Parse("#D787AF"), ClaudeDesktopColors.For(folder, isDefault: true));
            Assert.Equal("magenta", ClaudeDesktopColors.NameFor(folder, isDefault: true));
            Assert.Equal("D787AF", ClaudeDesktopColors.HexFor(folder, isDefault: true));
        }
        finally
        {
            ClaudeBuddySettings.RemoveProfile(folder);
        }
    }

    // Names are what settings.json stores, and it is matched case-insensitively
    // — the same tolerance ByName gives — so a file hand-edited to "Teal"
    // still resolves.
    [Fact]
    public void AStoredColourNameIsMatchedCaseInsensitively()
    {
        var folder = UniqueFolder();
        try
        {
            ClaudeBuddySettings.Update(folder, p => p.Color = "TEAL");

            Assert.Equal(Color.Parse("#00AFAF"), ClaudeDesktopColors.For(folder, isDefault: false));
        }
        finally
        {
            ClaudeBuddySettings.RemoveProfile(folder);
        }
    }

    // A name that is no longer in the palette — a hand-edited file, or a
    // palette retune that dropped one — falls through to the derived colour
    // rather than to slate. That is the right answer: slate is Default's, and
    // handing it to a created profile would make two rows look like one.
    [Fact]
    public void AnUnknownStoredColourFallsBackToTheDerivedOne()
    {
        var folder = UniqueFolder();
        try
        {
            var derived = ClaudeDesktopColors.For(folder, isDefault: false);
            ClaudeBuddySettings.Update(folder, p => p.Color = "chartreuse");

            Assert.Equal(derived, ClaudeDesktopColors.For(folder, isDefault: false));
        }
        finally
        {
            ClaudeBuddySettings.RemoveProfile(folder);
        }
    }

    // An empty string is what "auto" looks like in the file, and it has to read
    // as "no choice made" rather than as a colour name that failed to match —
    // the difference shows on the Default profile, which keeps its slate only
    // while nothing is chosen.
    [Fact]
    public void AnEmptyStoredColourMeansNoChoiceWasMade()
    {
        var folder = UniqueFolder();
        try
        {
            ClaudeBuddySettings.Update(folder, p => p.Color = "");

            Assert.Equal(Color.Parse("#5B7A94"), ClaudeDesktopColors.For(folder, isDefault: true));
            Assert.Equal("slate", ClaudeDesktopColors.NameFor(folder, isDefault: true));
        }
        finally
        {
            ClaudeBuddySettings.RemoveProfile(folder);
        }
    }

    // The three surfaces that show a profile's colour — the tray swatch, the
    // tinted Dock icon and the window overlay — read it through For, NameFor
    // and HexFor respectively, and the file's opening comment says they have to
    // agree or the colour stops meaning anything. So the three answers are
    // checked against each other rather than each against a literal.
    [Fact]
    public void TheNameAndTheHexBothDescribeTheColourForReturns()
    {
        foreach (var folder in new[] { "Claude", "Claude-work", "Claude-zzz" })
        {
            foreach (var isDefault in new[] { true, false })
            {
                var colour = ClaudeDesktopColors.For(folder, isDefault);

                Assert.Equal(colour, ClaudeDesktopColors.ByName(
                    ClaudeDesktopColors.NameFor(folder, isDefault)));
                Assert.Equal($"{colour.R:X2}{colour.G:X2}{colour.B:X2}",
                    ClaudeDesktopColors.HexFor(folder, isDefault));
            }
        }
    }

    [Fact]
    public void EveryNamedColourRoundTripsThroughByName()
    {
        // Nine names, one per palette entry plus the reserved slate, all
        // distinct colours — a duplicate would make two profiles that look the
        // same claim different names.
        var colours = ClaudeDesktopColors.Names.Select(ClaudeDesktopColors.ByName).ToArray();

        Assert.Equal(9, colours.Length);
        Assert.Equal(colours.Length, new System.Collections.Generic.HashSet<Color>(colours).Count);
    }
}
