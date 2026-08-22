using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.Tests;

// A handful of assertions on ClaudeDesktopColors.cs's public surface. For(),
// NameFor() and HexFor() all read through ClaudeBuddySettings.For(folderName)
// first, which is safe here only because TestBootstrap has already pointed
// CLAUDE_BUDDY_SETTINGS_DIR at an isolated temp directory.
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
}
