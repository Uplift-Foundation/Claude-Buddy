using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;

namespace ClaudeBuddy.Tests;

// The colour rows, and the guard that stops a theme choosing a user's colours for
// them.
//
// This one is worth the effort out of proportion to its size, because the bug it
// fixed was silent and self-perpetuating. Seeding a ColorPicker's Color and
// subscribing afterwards is not enough: the macOS theme's template raises
// ColorChanged *after* that with a colour of its own, and it wrote #2C273C /
// #50D140 / #E82323 into settings.json on the first launch that ever opened this
// window. Three colours nobody chose became the user's colours, the swatches
// re-seeded from them next time, and nothing anywhere looked like an error.
//
// The fix is to arm on a click or a focus — you cannot pick a colour without
// opening the drop-down first — and to treat everything before that as the
// template talking to itself. Comparing against the stored value cannot catch it
// on its own, because a spurious change is a genuine difference. So the tests
// below are about *when* a change counts, not about what colour comes out.
[Collection("Settings")]
public class SettingsColourRowTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static ColorPicker PickerIn(Control row) =>
        row.GetLogicalDescendants().OfType<ColorPicker>().Single();

    // The template's own spurious change: nothing has been clicked or focused.
    private static void TemplateSets(ColorPicker picker, Color colour) => picker.Color = colour;

    // What a person doing it looks like — the drop-down has to be opened first,
    // and the row arms on the pointer going down or on focus arriving.
    private static void PersonSets(ColorPicker picker, Color colour)
    {
        // The pointer going down is what arms the row, and it is registered as a
        // tunnelling handler so it arrives before the template's own button marks
        // it handled. Raised directly rather than hit-tested, the same way
        // ChatPanelTests drives its drag handles and for the same reason.
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        picker.RaiseEvent(new PointerPressedEventArgs(
            picker, pointer, picker, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));

        picker.Color = colour;
    }

    private const string State = "waiting";

    private static void ResetColour() => OrbColors.Set(State, null);

    // --- the guard ---

    // The bug, as a test. A change nobody asked for must not become the user's
    // colour.
    [AvaloniaFact]
    public void AColourTheTemplateChoosesIsNotSaved()
    {
        ResetColour();
        try
        {
            var shipped = OrbColors.For(State);
            var window = NewWindow();
            var picker = PickerIn((Control)window.ColorRow("Waiting", State));

            TemplateSets(picker, Color.FromRgb(0x2C, 0x27, 0x3C));

            Assert.Equal(shipped, OrbColors.For(State));
        }
        finally
        {
            ResetColour();
        }
    }

    // ...and the swatch is put back rather than left showing a colour the app is
    // not using. Declining to save it would leave the window lying about what the
    // orbs are.
    [AvaloniaFact]
    public void TheSwatchIsPutBackAfterAColourNobodyChose()
    {
        ResetColour();
        try
        {
            var shipped = OrbColors.For(State);
            var window = NewWindow();
            var picker = PickerIn((Control)window.ColorRow("Waiting", State));

            TemplateSets(picker, Color.FromRgb(0x2C, 0x27, 0x3C));

            Assert.Equal(shipped.R, picker.Color.R);
            Assert.Equal(shipped.G, picker.Color.G);
            Assert.Equal(shipped.B, picker.Color.B);
        }
        finally
        {
            ResetColour();
        }
    }

    // Once a person has touched the control, a change is theirs and is saved.
    [AvaloniaFact]
    public void AColourAPersonPicksIsSaved()
    {
        ResetColour();
        try
        {
            var window = NewWindow();
            var picker = PickerIn((Control)window.ColorRow("Waiting", State));

            PersonSets(picker, Color.FromRgb(0x12, 0x34, 0x56));

            var saved = OrbColors.For(State);
            Assert.Equal(0x12, saved.R);
            Assert.Equal(0x34, saved.G);
            Assert.Equal(0x56, saved.B);
        }
        finally
        {
            ResetColour();
        }
    }

    // A real edit that changes nothing still must not write. Writing today's
    // default as an explicit hex would freeze it into the file and light up the
    // Reset button for a colour nobody chose — the same failure one step along.
    [AvaloniaFact]
    public void PickingTheColourItAlreadyIsWritesNothing()
    {
        ResetColour();
        try
        {
            var window = NewWindow();
            var picker = PickerIn((Control)window.ColorRow("Waiting", State));
            var shipped = OrbColors.For(State);

            PersonSets(picker, Color.FromRgb(shipped.R, shipped.G, shipped.B));

            Assert.True(
                string.IsNullOrEmpty(ClaudeBuddySettings.WaitingColor),
                "picking the colour it already is must not freeze today's default into the file");
        }
        finally
        {
            ResetColour();
        }
    }

    // The picker opens on the colour actually in use, so the swatch and the orbs
    // agree the moment the window appears.
    [AvaloniaFact]
    public void ThePickerOpensOnTheColourInUse()
    {
        ResetColour();
        try
        {
            OrbColors.Set(State, "#0A0B0C");

            var window = NewWindow();
            var picker = PickerIn((Control)window.ColorRow("Waiting", State));

            Assert.Equal(0x0A, picker.Color.R);
            Assert.Equal(0x0B, picker.Color.G);
            Assert.Equal(0x0C, picker.Color.B);
        }
        finally
        {
            ResetColour();
        }
    }

    // Alpha is hidden *and* disabled, so the control never shows a value the app
    // will not honour: the orb builds its own alphas — the glow's gradient stops
    // and the tray icon's ring — so a user-set one would be thrown away silently
    // or make the orb look broken.
    [AvaloniaFact]
    public void AlphaIsNeitherShownNorEditable()
    {
        var window = NewWindow();
        var picker = PickerIn((Control)window.ColorRow("Waiting", State));

        Assert.False(picker.IsAlphaVisible);
        Assert.False(picker.IsAlphaEnabled);
    }

    // Each state has its own row writing its own colour, which is the same
    // copy-paste hazard the click rows have — and here a mistake would make two
    // states indistinguishable on screen.
    [AvaloniaFact]
    public void EachStateRowWritesItsOwnColour()
    {
        OrbColors.Set("waiting", null);
        OrbColors.Set("generating", null);
        try
        {
            var window = NewWindow();

            var waiting = PickerIn((Control)window.ColorRow("Waiting", "waiting"));
            var generating = PickerIn((Control)window.ColorRow("Working", "generating"));

            PersonSets(waiting, Color.FromRgb(0x11, 0x22, 0x33));
            PersonSets(generating, Color.FromRgb(0x44, 0x55, 0x66));

            Assert.Equal(0x11, OrbColors.For("waiting").R);
            Assert.Equal(0x44, OrbColors.For("generating").R);
        }
        finally
        {
            OrbColors.Set("waiting", null);
            OrbColors.Set("generating", null);
        }
    }
}
