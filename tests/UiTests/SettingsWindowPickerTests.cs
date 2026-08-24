using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// The settings rows that are a dropdown or a slider rather than a switch.
//
// Driven through the control each builder returns rather than by hunting the
// visual tree, for the reason the switch tests give: what a row actually holds
// depends on which theme templates loaded, so finding it by walking the tree
// tests the theme. Each builder hands back its own control, so a test can change
// the control's value and watch the setting follow.
//
// Safe to drive, and worth checking why. Every handler here either writes a
// setting or calls SessionManager.Instance?.ReapplyArrangement() — and under the
// headless lifetime App never starts a SessionManager, so Instance is null and
// that call is a no-op. Nothing else in these rows reaches the OS. The voice
// pickers are deliberately not driven: opening one enumerates the machine's
// voices, which launches `say`.
public class SettingsWindowPickerTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    // --- click actions ---

    // Three rows share one builder, and each is handed its own getter and setter.
    // A builder that captured the wrong one would make all three rows write the
    // same setting — the whole point of passing them in.
    [AvaloniaFact]
    public void EachClickRowWritesTheSettingItWasGiven()
    {
        var window = NewWindow();
        var written = new List<string>();

        var combo = (ComboBox)window.ClickPicker(() => "none", written.Add);

        combo.SelectedIndex = 0;

        Assert.Single(written);
        Assert.Equal("terminal", written[0]);
    }

    // Every choice in the list maps to its own value, in order. A mis-indexed
    // list would silently bind "Read the latest reply" to opening the chat panel,
    // and the label would still look right on screen.
    [AvaloniaTheory]
    [InlineData(0, "terminal")]
    [InlineData(1, "chat")]
    [InlineData(2, "speak")]
    [InlineData(3, "none")]
    public void TheChosenLabelMapsToItsOwnValue(int index, string expected)
    {
        var window = NewWindow();
        string? written = null;

        // Seeded with something the case is *not* selecting. A picker opens on
        // its saved value, so seeding it with the value under test would leave
        // SelectedIndex already there, raise no SelectionChanged, and the case
        // would pass by writing nothing at all — which is how the first draft of
        // this test passed three rows and failed the fourth.
        var seed = expected == "none" ? "terminal" : "none";

        var combo = (ComboBox)window.ClickPicker(() => seed, v => written = v);

        combo.SelectedIndex = index;

        Assert.Equal(expected, written);
    }

    // The picker opens on whatever is saved, so a user sees their own choice
    // rather than the first option.
    [AvaloniaFact]
    public void ThePickerOpensOnTheSavedChoice()
    {
        var window = NewWindow();

        var combo = (ComboBox)window.ClickPicker(() => "speak", _ => { });

        Assert.Equal(2, combo.SelectedIndex);
    }

    // A saved value this build does not offer is added to the list rather than
    // dropped, so opening settings on a newer profile does not silently rewrite
    // the choice to the first option.
    [AvaloniaFact]
    public void AnUnknownSavedValueIsKeptRatherThanReset()
    {
        var window = NewWindow();

        var combo = (ComboBox)window.ClickPicker(() => "something-from-a-later-build", _ => { });

        Assert.True(combo.SelectedIndex >= 0,
            "an unrecognised saved value must still be selected, not reset");
        Assert.Equal(5, ((System.Collections.IList)combo.ItemsSource!).Count);
    }

    // --- the arrangement shape ---

    [AvaloniaFact]
    public void ChoosingAShapeWritesIt()
    {
        var window = NewWindow();
        ClaudeBuddySettings.ArrangeShape = "heart";

        var combo = (ComboBox)window.ShapePicker();
        combo.SelectedIndex = 3;

        Assert.Equal("star", ClaudeBuddySettings.ArrangeShape);
    }

    [AvaloniaFact]
    public void TheShapePickerOpensOnTheSavedShape()
    {
        ClaudeBuddySettings.ArrangeShape = "grid";
        var window = NewWindow();

        var combo = (ComboBox)window.ShapePicker();

        Assert.Equal(4, combo.SelectedIndex);
    }

    // Every shape the picker offers is one OrbArrangement actually knows. A
    // label bound to a shape the geometry does not implement would arrange orbs
    // into whatever its fallback is, with the settings window insisting
    // otherwise.
    [AvaloniaFact]
    public void EveryOfferedShapeIsOneTheArrangementKnows()
    {
        // Set explicitly, because ClaudeBuddySettings is a process-wide static and
        // the picker deliberately adds an unrecognised saved shape to its own
        // list. Without this the case inherited whatever the previous test left
        // behind and then asserted the geometry knows it — which is how it first
        // failed, on a shape another test had invented.
        ClaudeBuddySettings.ArrangeShape = "heart";

        var window = NewWindow();
        var combo = (ComboBox)window.ShapePicker();
        var count = ((System.Collections.IList)combo.ItemsSource!).Count;

        for (var i = 0; i < count; i++)
        {
            combo.SelectedIndex = i;
            var shape = ClaudeBuddySettings.ArrangeShape;

            Assert.Contains(shape, ArrangementSweep.Shapes);
        }
    }

    // Same tolerance as the click picker: a shape from a later build survives
    // being looked at.
    [AvaloniaFact]
    public void AnUnknownSavedShapeIsKept()
    {
        ClaudeBuddySettings.ArrangeShape = "spiral-from-a-later-build";
        var window = NewWindow();

        var combo = (ComboBox)window.ShapePicker();

        Assert.True(combo.SelectedIndex >= 0);
    }

    // --- the spacing slider ---

    [AvaloniaFact]
    public void DraggingTheSpacingSliderWritesIt()
    {
        var window = NewWindow();
        var slider = (Slider)window.SpacingSlider();

        slider.Value = 1.5;

        Assert.Equal(1.5, ClaudeBuddySettings.ArrangeSpacing, 3);
    }

    [AvaloniaFact]
    public void TheSliderOpensOnTheSavedSpacing()
    {
        ClaudeBuddySettings.ArrangeSpacing = 1.25;
        var window = NewWindow();

        var slider = (Slider)window.SpacingSlider();

        Assert.Equal(1.25, slider.Value, 3);
    }

    // The slider's range is the range the geometry was verified across, which is
    // what tests/ArrangementTests sweeps: a slider that let a user past either
    // end would arrange orbs into a shape nothing has ever checked.
    [AvaloniaFact]
    public void TheSlidersRangeIsTheRangeTheGeometryWasSweptAcross()
    {
        var window = NewWindow();
        var slider = (Slider)window.SpacingSlider();

        Assert.Equal(ArrangementSweep.Spacings.Min(), slider.Minimum, 3);
        Assert.Equal(ArrangementSweep.Spacings.Max(), slider.Maximum, 3);
    }

    // Snapped to ticks, so the value written is one of a known set rather than
    // whatever pixel the pointer landed on — which is what keeps the saved
    // number readable in settings.json.
    [AvaloniaFact]
    public void TheSliderSnapsToItsTicks()
    {
        var window = NewWindow();
        var slider = (Slider)window.SpacingSlider();

        Assert.True(slider.IsSnapToTickEnabled);
        Assert.True(slider.TickFrequency > 0);
    }
}
