using Avalonia.Input;
using Xunit;

namespace ClaudeBuddy.Tests;

// The arithmetic and the key mapping behind Cmd+ / Cmd- in the chat panel.
//
// Worth having as a unit suite rather than only as a UI one because both
// halves fail quietly: a ladder that can walk past its own end draws a panel
// nothing on screen can undo, and a gesture that matches too loosely steals a
// keystroke from whatever else wanted it. Neither shows up as an exception.
public class ChatZoomTests
{
    // --- the ladder ------------------------------------------------------

    [Fact]
    public void TheStepsAreOrderedAndTheDefaultIsOneOfThem()
    {
        for (var i = 1; i < ChatZoom.Steps.Length; i++)
            Assert.True(ChatZoom.Steps[i] > ChatZoom.Steps[i - 1],
                $"step {i} ({ChatZoom.Steps[i]}) is not above step {i - 1} ({ChatZoom.Steps[i - 1]})");

        // Cmd+0 has to land on a rung, or the first Cmd+ after a reset would
        // be a step away from somewhere the ladder does not have.
        Assert.Contains(ChatZoom.Default, ChatZoom.Steps);

        Assert.Equal(ChatZoom.Steps[0], ChatZoom.Min);
        Assert.Equal(ChatZoom.Steps[^1], ChatZoom.Max);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, ChatZoom.Default - 0.2)]   // below Min, pinned to 0.8
    [InlineData(9.0, 2.0)]
    [InlineData(0.0, 0.8)]
    [InlineData(-3.0, 0.8)]
    public void ClampPinsAnythingOutsideTheLadderToItsEnds(double given, double expected)
    {
        Assert.Equal(expected, ChatZoom.Clamp(given), 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonNumberReadsAsTheShippedSize(double given)
    {
        // A NaN would otherwise survive Math.Clamp and reach a FontSize, where
        // it draws nothing at all — the one outcome a text-size setting must
        // never be able to produce.
        Assert.Equal(ChatZoom.Default, ChatZoom.Clamp(given));

        // And a keystroke against a broken value still moves: it reads as the
        // shipped size first, then takes its step from there, so the gesture
        // repairs the setting rather than being swallowed by it.
        Assert.Equal(1.15, ChatZoom.Bigger(given), 3);
        Assert.Equal(0.9, ChatZoom.Smaller(given), 3);
    }

    [Fact]
    public void BiggerAndSmallerWalkTheLadderOneRungAtATime()
    {
        Assert.Equal(1.15, ChatZoom.Bigger(1.0), 3);
        Assert.Equal(1.3, ChatZoom.Bigger(1.15), 3);
        Assert.Equal(0.9, ChatZoom.Smaller(1.0), 3);
        Assert.Equal(0.8, ChatZoom.Smaller(0.9), 3);
    }

    [Fact]
    public void TheLadderStopsAtItsEndsRatherThanRunningOff()
    {
        Assert.Equal(ChatZoom.Max, ChatZoom.Bigger(ChatZoom.Max), 3);
        Assert.Equal(ChatZoom.Min, ChatZoom.Smaller(ChatZoom.Min), 3);
    }

    [Fact]
    public void AScaleBetweenTwoRungsMovesToTheRungOnTheSideItWasAskedFor()
    {
        // A hand-edited settings file, or a ladder that changed between
        // versions. Snapping to the nearest rung first would make one of the
        // two directions look like it did nothing.
        Assert.Equal(1.15, ChatZoom.Bigger(1.07), 3);
        Assert.Equal(1.0, ChatZoom.Smaller(1.07), 3);
    }

    [Fact]
    public void AValueOffByAFloatingPointHairStillCountsAsOnItsRung()
    {
        // What comes back out of JSON is not always bit-identical to what went
        // in. Without the slack in Step, Bigger(1.1499999) would return 1.15 —
        // the rung it is already standing on — and the gesture would look dead.
        Assert.Equal(1.3, ChatZoom.Bigger(1.15 - 1e-9), 3);
        Assert.Equal(1.0, ChatZoom.Smaller(1.15 + 1e-9), 3);
    }

    // --- rungs by index, which is what the settings slider moves over -----

    [Theory]
    [InlineData(1.0, 2)]
    [InlineData(0.8, 0)]
    [InlineData(2.0, 7)]
    [InlineData(1.07, 2)]   // nearest, not next
    [InlineData(1.1, 3)]
    public void IndexOfPicksTheNearestRung(double scale, int expected)
    {
        Assert.Equal(expected, ChatZoom.IndexOf(scale));
    }

    [Fact]
    public void AtAndIndexOfAreEachOthersInverseAcrossTheWholeLadder()
    {
        for (var i = 0; i < ChatZoom.Steps.Length; i++)
            Assert.Equal(i, ChatZoom.IndexOf(ChatZoom.At(i)));
    }

    [Fact]
    public void AtPinsAnIndexOutsideTheLadder()
    {
        Assert.Equal(ChatZoom.Min, ChatZoom.At(-4), 3);
        Assert.Equal(ChatZoom.Max, ChatZoom.At(99), 3);
    }

    // --- the gesture ------------------------------------------------------

    [Theory]
    [InlineData(Key.OemPlus, ChatZoom.Command.Bigger)]
    [InlineData(Key.Add, ChatZoom.Command.Bigger)]
    [InlineData(Key.OemMinus, ChatZoom.Command.Smaller)]
    [InlineData(Key.Subtract, ChatZoom.Command.Smaller)]
    [InlineData(Key.D0, ChatZoom.Command.Reset)]
    [InlineData(Key.NumPad0, ChatZoom.Command.Reset)]
    public void EachZoomKeyIsRecognisedUnderEitherPlatformsAccelerator(Key key, ChatZoom.Command expected)
    {
        // Both accelerators are checked on whichever runner this happens to be,
        // so a macOS-only or Windows-only mistake fails on both legs of CI
        // rather than on the one nobody is looking at.
        Assert.Equal(expected, ChatZoom.Gesture(key, KeyModifiers.Meta, KeyModifiers.Meta));
        Assert.Equal(expected, ChatZoom.Gesture(key, KeyModifiers.Control, KeyModifiers.Control));
    }

    [Fact]
    public void ShiftIsIgnoredBecauseThatIsHowAPlusSignIsTyped()
    {
        // On a US layout "+" is Shift and "=", so Cmd+Shift+= is how most
        // people actually press what they read as Cmd++.
        Assert.Equal(ChatZoom.Command.Bigger,
            ChatZoom.Gesture(Key.OemPlus, KeyModifiers.Meta | KeyModifiers.Shift, KeyModifiers.Meta));
    }

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Control)]                        // the other platform's
    [InlineData(KeyModifiers.Meta | KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Meta | KeyModifiers.Control)]
    public void WithoutExactlyTheAcceleratorItIsSomebodyElsesKeystroke(KeyModifiers modifiers)
    {
        Assert.Equal(ChatZoom.Command.None,
            ChatZoom.Gesture(Key.OemPlus, modifiers, KeyModifiers.Meta));
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.D1)]
    [InlineData(Key.Enter)]
    [InlineData(Key.W)]
    public void AKeyThatIsNotAZoomKeyIsNotOne(Key key)
    {
        Assert.Equal(ChatZoom.Command.None, ChatZoom.Gesture(key, KeyModifiers.Meta, KeyModifiers.Meta));
    }

    [Fact]
    public void TheAcceleratorIsCommandOnMacAndControlElsewhere()
    {
        Assert.Equal(
            OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control,
            ChatZoom.Accelerator);

        // And the one-argument overload is the two-argument one under that
        // accelerator, which is the only thing it promises.
        Assert.Equal(ChatZoom.Command.Bigger, ChatZoom.Gesture(Key.OemPlus, ChatZoom.Accelerator));
        Assert.Equal(ChatZoom.Command.None, ChatZoom.Gesture(Key.OemPlus, KeyModifiers.None));
    }

    // --- what a command does ----------------------------------------------

    [Fact]
    public void ApplyIsTheCommandsWholeMeaning()
    {
        Assert.Equal(1.15, ChatZoom.Apply(ChatZoom.Command.Bigger, 1.0), 3);
        Assert.Equal(0.9, ChatZoom.Apply(ChatZoom.Command.Smaller, 1.0), 3);
        Assert.Equal(ChatZoom.Default, ChatZoom.Apply(ChatZoom.Command.Reset, 2.0), 3);

        // None leaves the scale alone — but still clamped, since the value it
        // was handed came out of a settings file.
        Assert.Equal(1.3, ChatZoom.Apply(ChatZoom.Command.None, 1.3), 3);
        Assert.Equal(ChatZoom.Max, ChatZoom.Apply(ChatZoom.Command.None, 40), 3);
    }
}
