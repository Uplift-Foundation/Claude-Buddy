using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.UiTests;

// One row of the Claude Desktop profiles table: its name box, its colour picker,
// and the three switches beside them.
//
// Two of those handlers cannot be driven here and two can, and the difference is
// worth stating because it is not about the UI. Changing a profile's colour or
// its name calls ClaudeDesktopManager, which rebuilds a tinted clone of Claude.app
// — scanning live processes first to decide whether it is safe to delete a bundle
// out from under a running instance — and rescans every profile directory on a
// background task. That happens whether or not the method carries an exclusion
// attribute: an exclusion stops a line being *counted*, not being *run*. So the
// decisions those handlers make are pure functions, tested here, and the handlers
// that act on them are excluded.
//
// The other two switches only write a setting, so they are driven for real.
[Collection("Settings")]
public class SettingsProfileRowTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    private static ProfileView Profile(string folder = "Claude-Profile-1") =>
        new(DisplayName: folder,
            Directory: "/tmp/" + folder,
            IsDefault: false,
            IsRunning: false,
            Pid: 0,
            Activity: ProfileActivity.None,
            Message: null,
            ThemeMode: "system");

    // ---- the colour a picker index means -----------------------------------

    // Index 0 is "auto", and it maps to a NULL stored colour rather than to a
    // colour named "auto". That is what lets a profile go back to its
    // name-derived colour — without it a colour is a one-way door, including one
    // set by a stray keystroke.
    [AvaloniaFact]
    public void TheFirstChoiceMeansNoStoredColourAtAll()
    {
        var options = new List<string> { "auto", "red", "blue" };

        Assert.Null(SettingsWindow.ChosenProfileColour(options, 0));
    }

    [AvaloniaFact]
    public void AnyOtherChoiceIsStoredByName()
    {
        var options = new List<string> { "auto", "red", "blue" };

        Assert.Equal("red", SettingsWindow.ChosenProfileColour(options, 1));
        Assert.Equal("blue", SettingsWindow.ChosenProfileColour(options, 2));
    }

    // A negative index is what a ComboBox reports with nothing selected, and it
    // must mean the same as "auto" rather than indexing backwards.
    [AvaloniaFact]
    public void NothingSelectedMeansNoStoredColour()
    {
        var options = new List<string> { "auto", "red" };

        Assert.Null(SettingsWindow.ChosenProfileColour(options, -1));
    }

    // ---- the name a typed box means ----------------------------------------

    [AvaloniaFact]
    public void ATypedNameIsStoredTrimmed()
    {
        Assert.Equal("Work", SettingsWindow.ChosenProfileName("  Work  "));
    }

    // Clearing the box restores the folder-derived name rather than leaving the
    // profile with an empty one, which is why blank has to become null and not "".
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsStoredAsNothingRatherThanAnEmptyName(string? typed)
    {
        Assert.Null(SettingsWindow.ChosenProfileName(typed));
    }

    // ---- the two switches that only write a setting ------------------------

    [AvaloniaFact]
    public void TheDockIconSwitchWritesItsSetting()
    {
        ClaudeBuddySettings.ReloadForTests();
        var row = NewWindow().Row(Profile());

        var boxes = row.GetLogicalDescendants().OfType<CheckBox>().ToList();
        Assert.True(boxes.Count >= 3, $"expected three switches, got {boxes.Count}");

        // Column 3 is "Tint the Dock icon" — see Row()'s Add(grid, 3, ...).
        boxes[1].IsChecked = !boxes[1].IsChecked;

        var stored = ClaudeBuddySettings.For("Claude-Profile-1");
        Assert.Equal(boxes[1].IsChecked, stored.TintDockIcon);
    }

    [AvaloniaFact]
    public void TheWindowTintSwitchWritesItsSetting()
    {
        ClaudeBuddySettings.ReloadForTests();
        var row = NewWindow().Row(Profile("Claude-Profile-2"));

        var boxes = row.GetLogicalDescendants().OfType<CheckBox>().ToList();
        Assert.True(boxes.Count >= 3);

        boxes[2].IsChecked = !boxes[2].IsChecked;

        var stored = ClaudeBuddySettings.For("Claude-Profile-2");
        Assert.Equal(boxes[2].IsChecked, stored.TintWindow);
    }

    // ---- the row's own shape -----------------------------------------------

    // A stored colour opens the picker on it rather than on "auto", which is the
    // difference between the window showing what is set and the window quietly
    // offering to change it.
    [AvaloniaFact]
    public void AStoredColourIsPreselectedInThePicker()
    {
        ClaudeBuddySettings.ReloadForTests();
        var name = ClaudeDesktopColors.Names.First();
        ClaudeBuddySettings.Update("Claude-Profile-3", p => p.Color = name);

        var row = NewWindow().Row(Profile("Claude-Profile-3"));
        var combo = row.GetLogicalDescendants().OfType<ComboBox>().First();

        Assert.True(combo.SelectedIndex > 0,
            "a stored colour should not leave the picker on auto");
    }

    [AvaloniaFact]
    public void NoStoredColourOpensOnAuto()
    {
        ClaudeBuddySettings.ReloadForTests();

        var row = NewWindow().Row(Profile("Claude-Profile-4"));
        var combo = row.GetLogicalDescendants().OfType<ComboBox>().First();

        Assert.Equal(0, combo.SelectedIndex);
    }

    // A stored colour this version no longer offers falls back to auto rather
    // than to whichever colour happens to sit at that index now.
    [AvaloniaFact]
    public void AColourThisVersionNoLongerOffersFallsBackToAuto()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.Update("Claude-Profile-5", p => p.Color = "vantablack");

        var row = NewWindow().Row(Profile("Claude-Profile-5"));
        var combo = row.GetLogicalDescendants().OfType<ComboBox>().First();

        Assert.Equal(0, combo.SelectedIndex);
    }

    // ---- the named setters ---------------------------------------------------

    // These exist so the excluded handlers hold no lambda, and they are covered
    // here rather than through those handlers — which is the point: the write is
    // testable even though the thing that triggers it is not.
    [AvaloniaFact]
    public void SettingAProfileColourWritesIt()
    {
        ClaudeBuddySettings.ReloadForTests();

        ClaudeBuddySettings.SetProfileColor("Claude-Profile-9", "red");

        Assert.Equal("red", ClaudeBuddySettings.For("Claude-Profile-9").Color);
    }

    // Null is how a profile goes back to its name-derived colour, so it has to
    // round-trip as null rather than as "".
    [AvaloniaFact]
    public void ClearingAProfileColourStoresNothing()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.SetProfileColor("Claude-Profile-9", "red");

        ClaudeBuddySettings.SetProfileColor("Claude-Profile-9", null);

        Assert.Null(ClaudeBuddySettings.For("Claude-Profile-9").Color);
    }

    [AvaloniaFact]
    public void SettingAProfileSwatchWritesIt()
    {
        ClaudeBuddySettings.ReloadForTests();

        ClaudeBuddySettings.SetProfileShowSwatch("Claude-Profile-9", false);
        Assert.False(ClaudeBuddySettings.For("Claude-Profile-9").ShowSwatch);

        ClaudeBuddySettings.SetProfileShowSwatch("Claude-Profile-9", true);
        Assert.True(ClaudeBuddySettings.For("Claude-Profile-9").ShowSwatch);
    }
}
