using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// The glue half of CB-166's filter box: SettingsRow, SettingsCard and
// SettingsSection.ApplyFilter applying SettingsFilter's pure answers to a
// real tree. SettingsFilterTests.cs next door carries the actual filtering
// logic as plain facts; this file is structural only — no synthesized
// keystrokes, because there's no gesture here that a click or a key press
// would exercise that ApplyFilter() itself doesn't already cover, and driving
// a real TextBox through headless input is what the other UI test files in
// this suite already avoid for exactly the same reason a click on an orb is
// avoided: it would exercise something no test needs to.
[Collection("Settings")]
public class SettingsWindowFilterTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static TextBox FilterBox(SettingsWindow window) =>
        window.GetLogicalDescendants().OfType<TextBox>()
            .Single(t => t.Watermark == "Search settings");

    private static void SetFilter(SettingsWindow window, string query)
    {
        FilterBox(window).Text = query;
    }

    [AvaloniaFact]
    public void NonMatchingRowsHide()
    {
        var window = NewWindow();

        SetFilter(window, "voice");

        var orbsRow = window.Sections["orbs"].GetLogicalDescendants()
            .OfType<TextBlock>().Single(t => t.Text == "Show orbs");

        Assert.False(RowOf(orbsRow).IsVisible);
    }

    [AvaloniaFact]
    public void ASectionWithNoMatchHidesEntirely()
    {
        var window = NewWindow();

        SetFilter(window, "there is no such setting anywhere");

        Assert.All(window.Sections.Values, section => Assert.False(section.IsVisible));
    }

    [AvaloniaFact]
    public void ATitleMatchShowsItsWholeSection()
    {
        var window = NewWindow();

        SetFilter(window, "voice");

        Assert.True(window.Sections["voice"].IsVisible);

        var everyRow = window.Sections["voice"].GetLogicalDescendants()
            .OfType<SettingsWindow.SettingsRow>().ToList();
        Assert.NotEmpty(everyRow);
        Assert.All(everyRow, row => Assert.True(row.IsVisible));
    }

    [AvaloniaFact]
    public void ClearingTheFilterRestoresEverything()
    {
        var window = NewWindow();

        SetFilter(window, "there is no such setting anywhere");
        Assert.False(window.Sections["orbs"].IsVisible);

        SetFilter(window, "");

        Assert.All(window.Sections.Values, section => Assert.True(section.IsVisible));
    }

    // The stacked-hairline requirement, read back off a real card: whatever
    // survives the filter gets exactly one separator before each visible row
    // after the first, and none before the first.
    [AvaloniaFact]
    public void OneHairlinePerVisibleRowAfterTheFirstNoneLeading()
    {
        var window = NewWindow();

        SetFilter(window, "orbs");

        var card = window.Sections["orbs"].GetLogicalDescendants()
            .OfType<SettingsWindow.SettingsCard>().First();

        var visibleRows = card.Rows.Where(r => r.IsVisible).ToList();
        Assert.NotEmpty(visibleRows);

        var visibleSeparators = card.Separators.Count(s => s.IsVisible);
        Assert.Equal(visibleRows.Count - 1, visibleSeparators);

        // And specifically: the separator immediately before the first
        // visible row in the whole card is never one of the visible ones.
        var firstVisibleIndex = card.Rows.ToList().IndexOf(visibleRows[0]);
        if (firstVisibleIndex > 0)
        {
            Assert.False(card.Separators[firstVisibleIndex - 1].IsVisible);
        }
    }

    // Several toggle handlers call Rebuild() outright rather than mutating
    // the tree in place — ResetColorsButton is one real, reachable example —
    // so a query typed before one of those has to still be filtering the page
    // Rebuild() hands back, not silently reset to showing everything.
    [AvaloniaFact]
    public void TheFilterSurvivesARebuildDrivenByARealToggleHandler()
    {
        var window = NewWindow();

        SetFilter(window, "restore");

        Assert.True(window.Sections["orb-colours"].IsVisible);
        Assert.False(window.Sections["orbs"].IsVisible);

        var reset = (Button)window.ResetColorsButton();
        reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // Rebuild() replaced the whole page, including window.Sections'
        // contents (a fresh SettingsSection per id) and the filter box's
        // TextBlock in the new tree, but the filter box control itself is
        // chrome and was never replaced — its Text survived the rebuild, and
        // ApplyFilter() ran again against the new page as Rebuild()'s last
        // step.
        Assert.Equal("restore", FilterBox(window).Text);
        Assert.True(window.Sections["orb-colours"].IsVisible);
        Assert.False(window.Sections["orbs"].IsVisible);
    }

    // Nothing outside a SettingsSection is ever touched by filtering — the
    // Windows-only Done button sits directly under the page root, not inside
    // any group, and this has to hold on every platform since the fact under
    // test is "ApplyFilter only walks _sections.Values", not anything about
    // Windows itself.
    [AvaloniaFact]
    public void NothingOutsideASectionIsEverFiltered()
    {
        var window = NewWindow();

        SetFilter(window, "there is no such setting anywhere");

        var doneButtons = window.GetLogicalDescendants()
            .OfType<Button>().Where(b => Equals(b.Content, "Done")).ToList();

        Assert.All(doneButtons, b => Assert.True(b.IsVisible));
    }

    [AvaloniaFact]
    public void TheEmptyStateAppearsOnlyWhenNothingMatches()
    {
        var window = NewWindow();

        var emptyState = window.GetLogicalDescendants()
            .OfType<TextBlock>().Single(t => t.Text is not null && t.Text.StartsWith("No settings match"));

        Assert.False(emptyState.IsVisible);

        SetFilter(window, "there is no such setting anywhere");
        Assert.True(emptyState.IsVisible);
        Assert.Equal("No settings match \"there is no such setting anywhere\".", emptyState.Text);

        SetFilter(window, "");
        Assert.False(emptyState.IsVisible);
    }

    // The joint test: a filter force-opens a collapsed section to show a
    // match, and does it without persisting the expansion — clearing the
    // filter has to bring the fold back. This is the only test in the suite
    // that exercises both halves of CB-166 together, and the one that fails
    // loudly if persistence ever migrates into IsOpen's setter instead of
    // staying on the header's click handler.
    [AvaloniaFact]
    public void AFilterForceOpensACollapsedSectionWithoutPersistingIt()
    {
        var window = NewWindow();
        var voice = window.Sections["voice"];

        Assert.False(ClaudeBuddySettings.IsSettingsSectionCollapsed("voice"));
        voice.IsOpen = false;
        ClaudeBuddySettings.SetSettingsSectionCollapsed("voice", true);

        try
        {
            SetFilter(window, "voice");

            Assert.True(window.Sections["voice"].IsOpen);
            Assert.True(ClaudeBuddySettings.IsSettingsSectionCollapsed("voice"));

            SetFilter(window, "");

            Assert.False(window.Sections["voice"].IsOpen);
        }
        finally
        {
            ClaudeBuddySettings.SetSettingsSectionCollapsed("voice", false);
        }
    }

    private static SettingsWindow.SettingsRow RowOf(TextBlock label) =>
        label.GetLogicalAncestors().OfType<SettingsWindow.SettingsRow>().First();
}
