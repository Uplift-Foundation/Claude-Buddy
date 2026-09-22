using Avalonia.Input;
using Xunit;

namespace ClaudeBuddy.Tests;

// HotkeyRegistry is pure — no window, no settings read, no OS call — so
// every one of these runs with nothing arranged first. See CB-155: the
// registry is the one part of the global-hotkey feature a headless test
// can reach at all; the two platform hooks that actually register a key
// with the OS are excluded from coverage and named as such in the PR.
public class HotkeyRegistryTests
{
    [Fact]
    public void Default_ToggleOrbsVisible_IsControlAltH()
    {
        var combo = HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible);

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, combo.Modifiers);
        Assert.Equal(Key.H, combo.Key);
    }

    [Theory]
    [InlineData("Ctrl+Alt+H", KeyModifiers.Control | KeyModifiers.Alt, Key.H)]
    [InlineData("ctrl+alt+h", KeyModifiers.Control | KeyModifiers.Alt, Key.H)] // case-insensitive
    [InlineData("Control+Shift+P", KeyModifiers.Control | KeyModifiers.Shift, Key.P)]
    [InlineData("Cmd+Shift+H", KeyModifiers.Meta | KeyModifiers.Shift, Key.H)]
    [InlineData("Command+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Win+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Windows+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Super+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Meta+H", KeyModifiers.Meta, Key.H)]
    [InlineData("Option+Ctrl+H", KeyModifiers.Alt | KeyModifiers.Control, Key.H)]
    [InlineData(" Ctrl + Alt + H ", KeyModifiers.Control | KeyModifiers.Alt, Key.H)] // TrimEntries
    public void TryParse_AcceptsEveryDocumentedModifierSpelling(string spec, KeyModifiers expectedMods, Key expectedKey)
    {
        Assert.True(HotkeyRegistry.TryParse(spec, out var combo));
        Assert.Equal(expectedMods, combo.Modifiers);
        Assert.Equal(expectedKey, combo.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("H")]                 // no modifier at all
    [InlineData("Ctrl+")]              // modifier with no key
    [InlineData("Ctrl+NotARealKey")]   // key token doesn't parse
    [InlineData("Ctrl+H+P")]           // two non-modifier tokens
    [InlineData("Frobnicate+H")]       // unrecognised modifier token
    public void TryParse_RejectsMalformedOrIncompleteSpecs(string? spec)
    {
        Assert.False(HotkeyRegistry.TryParse(spec!, out _));
    }

    [Fact]
    public void Format_RoundTripsWhatTryParseAccepted()
    {
        Assert.True(HotkeyRegistry.TryParse("Ctrl+Alt+H", out var combo));
        Assert.Equal("Ctrl+Alt+H", HotkeyRegistry.Format(combo));

        Assert.True(HotkeyRegistry.TryParse("Cmd+Shift+P", out var meta));
        Assert.Equal("Shift+Cmd+P", HotkeyRegistry.Format(meta)); // Format's fixed order: Ctrl, Alt, Shift, Cmd
    }

    [Fact]
    public void Format_OrdersModifiersConsistentlyRegardlessOfInputOrder()
    {
        Assert.True(HotkeyRegistry.TryParse("Shift+Ctrl+Alt+H", out var combo));
        Assert.Equal("Ctrl+Alt+Shift+H", HotkeyRegistry.Format(combo));
    }

    [Fact]
    public void Resolve_UsesTheOverrideWhenItParses()
    {
        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleOrbsVisible, "Ctrl+Shift+P");

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, combo.Modifiers);
        Assert.Equal(Key.P, combo.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a real hotkey")]
    public void Resolve_FallsBackToTheDefaultWhenTheOverrideIsMissingOrInvalid(string? overrideSpec)
    {
        var expected = HotkeyRegistry.Default(HotkeyAction.ToggleOrbsVisible);

        var combo = HotkeyRegistry.Resolve(HotkeyAction.ToggleOrbsVisible, overrideSpec);

        Assert.Equal(expected, combo);
    }

    [Fact]
    public void Default_UnknownAction_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HotkeyRegistry.Default((HotkeyAction)999));
    }
}
