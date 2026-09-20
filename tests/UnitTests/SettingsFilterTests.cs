using Xunit;

namespace ClaudeBuddy.Tests
{
    // The pure half of CB-166's filter box. No Avalonia type appears in any
    // signature under test here — SettingsFilter is a static class of plain
    // strings and bools precisely so this file can be ordinary [Fact]s, with
    // no AvaloniaFact, no app lifetime, and no [Collection]. SettingsWindow's
    // SettingsRow and SettingsCard are the glue that hands these functions
    // real text and applies the answers back to real controls; that glue is
    // covered separately, in tests/UiTests/SettingsWindowFilterTests.cs.
    public class SettingsFilterTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void InactiveQueriesShowEverything(string? query)
        {
            Assert.False(SettingsFilter.IsActive(query));
            Assert.True(SettingsFilter.Matches(query, "anything"));
            Assert.True(SettingsFilter.Matches(query, null));
        }

        [Theory]
        [InlineData("voice")]
        [InlineData(" voice ")]
        public void ActiveQueriesAreOrdinalCaseInsensitiveSubstrings(string query)
        {
            Assert.True(SettingsFilter.IsActive(query));
            Assert.True(SettingsFilter.Matches(query, "High-quality VOICE switch"));
            Assert.False(SettingsFilter.Matches(query, "Orb colours"));
        }

        [Fact]
        public void EveryTermMustMatchAndOrderDoesNotMatter()
        {
            Assert.True(SettingsFilter.Matches("orb colour", "Reset the orb colour"));
            Assert.True(SettingsFilter.Matches("colour orb", "Reset the orb colour"));
            Assert.False(SettingsFilter.Matches("orb missing", "Reset the orb colour"));
        }

        [Fact]
        public void TextOfDropsNulls()
        {
            Assert.Equal("Show orbs", SettingsFilter.TextOf("Show orbs", null));
            Assert.Equal("Show orbs help text", SettingsFilter.TextOf("Show orbs", "help text"));
            Assert.Equal("", SettingsFilter.TextOf(null, null));
        }

        [Fact]
        public void AWordlessRowNeverMatchesAnActiveQuery()
        {
            Assert.False(SettingsFilter.Matches("voice", null));
            Assert.True(SettingsFilter.Matches(null, null));
        }

        // Chrome rows carry no text of their own and follow whichever
        // non-chrome sibling in the same card matched — a column heading or a
        // status line shown without the row it explains would read as an
        // orphan rather than as a search result.
        [Fact]
        public void ChromeRowsFollowTheirSiblings()
        {
            var texts = new string?[] { null, "High-quality voice switch", null };
            var chrome = new[] { true, false, true };

            var visible = SettingsFilter.VisibleRows("voice", texts, chrome, titleMatches: false);

            Assert.True(visible[0]);
            Assert.True(visible[1]);
            Assert.True(visible[2]);
        }

        [Fact]
        public void ChromeRowsStayHiddenWhenNoSiblingMatches()
        {
            var texts = new string?[] { null, "Reset colours", null };
            var chrome = new[] { true, false, true };

            var visible = SettingsFilter.VisibleRows("voice", texts, chrome, titleMatches: false);

            Assert.False(visible[0]);
            Assert.False(visible[1]);
            Assert.False(visible[2]);
        }

        // A section title match means the user asked for the topic, not for a
        // row inside it — so every row shows, chrome included, whatever its
        // own text says.
        [Fact]
        public void ATitleMatchShowsTheWholeSectionRegardlessOfRowText()
        {
            var texts = new string?[] { null, "Reset colours", null };
            var chrome = new[] { true, false, true };

            var visible = SettingsFilter.VisibleRows("anything at all", texts, chrome, titleMatches: true);

            Assert.All(visible, Assert.True);
        }

        [Fact]
        public void InactiveQueryShowsEveryRowInACard()
        {
            var texts = new string?[] { null, "Reset colours", null };
            var chrome = new[] { true, false, true };

            var visible = SettingsFilter.VisibleRows(null, texts, chrome, titleMatches: false);

            Assert.All(visible, Assert.True);
        }

        // The stacked-hairline test: the first visible row in a card never
        // gets a line above it, and every visible row after that does —
        // whatever got hidden in between.
        [Theory]
        [MemberData(nameof(SeparatorCases))]
        public void SeparatorsBeforeSkipsTheFirstVisibleRow(bool[] visible, int[] expected)
        {
            Assert.Equal(expected, SettingsFilter.SeparatorsBefore(visible));
        }

        public static TheoryData<bool[], int[]> SeparatorCases => new()
        {
            { new[] { true, true, true }, new[] { 1, 2 } },
            { new[] { false, true, false, true }, new[] { 3 } },
            { new[] { true, false, false }, System.Array.Empty<int>() },
            { new[] { false, false, false }, System.Array.Empty<int>() },
            { new[] { true }, System.Array.Empty<int>() },
        };

        [Fact]
        public void SectionVisibleIsTitleOrAnyRow()
        {
            Assert.True(SettingsFilter.SectionVisible(anyRowVisible: true, titleMatches: false));
            Assert.True(SettingsFilter.SectionVisible(anyRowVisible: false, titleMatches: true));
            Assert.False(SettingsFilter.SectionVisible(anyRowVisible: false, titleMatches: false));
        }

        // Asserted exactly, not just non-empty, so a future edit to the
        // wording gets reviewed here instead of drifting unnoticed.
        [Fact]
        public void EmptyStateTextQuotesWhatWasTyped()
        {
            Assert.Equal("No settings match \"voice\".", SettingsFilter.EmptyStateText("voice"));
            Assert.Equal("No settings match \"voice\".", SettingsFilter.EmptyStateText("  voice  "));
        }
    }
}
