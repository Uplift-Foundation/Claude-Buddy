using System;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiScreenshots;

// The menu-bar icon, recoloured to whatever the user chose for a state.
//
// In this suite rather than tests/UiTests because it needs REAL Skia: it decodes
// a 64x64 asset PNG, walks its pixels, and encodes the result back into a
// WindowIcon. Under the null renderer the decode hands back nothing to walk,
// which is the same reason ClaudeDesktopBundles.WriteTinted's pixel maths lives
// here — see IconTintTests next door.
//
// Worth having: this is premultiplied-alpha arithmetic done by hand, and the
// failure mode is a tray icon that looks washed out or has a dark halo, which
// nobody would think to file a bug about.
[Collection("Settings")]
public class TrayIconTintTests
{
    // Set(state, null) is how a colour goes back to the baked default — there is
    // no Reset, because "no stored colour" and "the default colour" are the same
    // state by design.
    private static void ResetColours()
    {
        foreach (var state in new[] { "idle", "generating", "waiting", "no-such-state" })
        {
            OrbColors.Set(state, null);
        }
    }

    // A state left on its default colour is not tinted at all — the baked PNG is
    // already the right colour, and re-tinting it would be work per colour change
    // for no visible difference.
    [AvaloniaFact]
    public void ADefaultColouredStateIsNotTinted()
    {
        ClaudeBuddySettings.ReloadForTests();
        ResetColours();

        Assert.Null(TrayController.Tinted("idle"));
    }

    // A customised state produces an icon. That is the whole feature: the menu
    // bar says which state you are in, in the colour you picked for it.
    [AvaloniaFact]
    public void ACustomisedStateProducesATintedIcon()
    {
        ClaudeBuddySettings.ReloadForTests();
        ResetColours();

        try
        {
            OrbColors.Set("idle", "#FF00FF");

            Assert.NotNull(TrayController.Tinted("idle"));
        }
        finally
        {
            ResetColours();
        }
    }

    // Each of the three states can be tinted independently — they are three
    // different assets, and a typo in one filename would show up as exactly one
    // state failing to recolour.
    [AvaloniaTheory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void EveryStateHasAnAssetToTint(string state)
    {
        ClaudeBuddySettings.ReloadForTests();
        ResetColours();

        try
        {
            OrbColors.Set(state, "#FF00FF");

            Assert.NotNull(TrayController.Tinted(state));
        }
        finally
        {
            ResetColours();
        }
    }

    // A state with no asset behind it falls back to nothing rather than throwing.
    // The catch is a graceful answer rather than a reflex: the caller then keeps
    // the baked PNG, so the icon still says which state you are in — just not in
    // the chosen hue. A throw here would take the tray down instead.
    [AvaloniaFact]
    public void AStateWithNoAssetFallsBackRatherThanThrowing()
    {
        ClaudeBuddySettings.ReloadForTests();
        ResetColours();

        try
        {
            OrbColors.Set("no-such-state", "#FF00FF");

            Assert.Null(TrayController.Tinted("no-such-state"));
        }
        finally
        {
            ResetColours();
        }
    }
}
