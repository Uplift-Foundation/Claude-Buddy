using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The speech and dictation sections of the settings window: which rows appear,
// and the progress line that only exists while something is downloading.
//
// Building these rows is safe; opening the voice dropdown is not. The voice list
// is enumerated inside DropDownOpened rather than up front — `say -v ?` is slow
// enough to be worth deferring, and on the neural side the list is not even
// answerable until the engine is on disk — so nothing here raises that event.
// That laziness is what makes the rest testable at all.
//
// The download progress line needs a field only the excluded download starter
// writes, so the tests set it directly. Reflection into a private field, the same
// way SessionScanTests reads _windows and for the same stated reason: the
// alternative is starting a 300MB download from a test.
[Collection("Settings")]
public class SettingsVoiceRowTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    private static void SetStatus(SettingsWindow window, string field, string? value)
    {
        var f = typeof(SettingsWindow).GetField(
            field, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(f);

        f!.SetValue(window, value);
    }

    private static bool Mentions(Control[] rows, string text) =>
        rows.Any(r => r.GetLogicalDescendants().OfType<TextBlock>()
            .Any(t => t.Text is not null && t.Text.Contains(text, StringComparison.Ordinal)));

    // ---- the neural voice ------------------------------------------------

    [AvaloniaFact]
    public void TheHighQualityVoiceRowIsAlwaysOffered()
    {
        ClaudeBuddySettings.ReloadForTests();
        var window = NewWindow();

        Assert.True(Mentions(window.VoiceRows(), "High-quality voice"));
    }

    // The 300MB and the delay are both said up front. A download that size
    // starting because someone flicked a switch is the kind of surprise worth
    // spending a sentence to avoid.
    [AvaloniaFact]
    public void TheVoiceRowSaysWhatEnablingItCosts()
    {
        ClaudeBuddySettings.ReloadForTests();
        var rows = NewWindow().VoiceRows();

        Assert.True(Mentions(rows, "300 MB"));
        Assert.True(Mentions(rows, "on this machine"));
    }

    // The progress line appears only while there is progress to report, and only
    // when the feature is on — two conditions, because a leftover status from a
    // download that finished before the switch was turned off would otherwise
    // sit there claiming to be current.
    [AvaloniaFact]
    public void TheEngineProgressLineAppearsWhenThereIsProgressToReport()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.NeuralVoiceEnabled = true;

        var window = NewWindow();
        SetStatus(window, "_neuralModelStatus", "Downloading… 42%");

        Assert.True(Mentions(window.VoiceRows(), "Downloading… 42%"));
    }

    [AvaloniaFact]
    public void TheEngineProgressLineIsAbsentWithNothingToReport()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.NeuralVoiceEnabled = true;

        var window = NewWindow();
        SetStatus(window, "_neuralModelStatus", null);

        Assert.False(Mentions(window.VoiceRows(), "Speech engine"));
    }

    [AvaloniaFact]
    public void TheEngineProgressLineIsAbsentWhileTheFeatureIsOff()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.NeuralVoiceEnabled = false;

        var window = NewWindow();
        SetStatus(window, "_neuralModelStatus", "Downloading… 42%");

        Assert.False(Mentions(window.VoiceRows(), "Downloading… 42%"));
    }

    // ---- dictation -------------------------------------------------------

    // Its own status field rather than sharing the voice one, because enabling
    // dictation and enabling the neural voice download different things and can
    // be in flight at the same time.
    [AvaloniaFact]
    public void TheVoiceModelProgressLineIsSeparateFromTheEngineOne()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.VoiceInputEnabled = true;
        ClaudeBuddySettings.NeuralVoiceEnabled = true;

        var window = NewWindow();
        SetStatus(window, "_voiceModelStatus", "Fetching Whisper…");
        SetStatus(window, "_neuralModelStatus", "Fetching Kokoro…");

        var rows = window.VoiceRows();

        Assert.True(Mentions(rows, "Fetching Whisper…"));
        Assert.True(Mentions(rows, "Fetching Kokoro…"));
    }

    [AvaloniaFact]
    public void TheVoiceModelLineIsAbsentWhileDictationIsOff()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.VoiceInputEnabled = false;

        var window = NewWindow();
        SetStatus(window, "_voiceModelStatus", "Fetching Whisper…");

        Assert.False(Mentions(window.VoiceRows(), "Fetching Whisper…"));
    }

    // The privacy claims are the reason anyone would turn dictation on, so they
    // are asserted rather than assumed: transcribed on this machine, and Enter is
    // never pressed for you.
    [AvaloniaFact]
    public void TheDictationRowSaysNothingLeavesTheMachine()
    {
        ClaudeBuddySettings.ReloadForTests();
        var rows = NewWindow().VoiceRows();

        Assert.True(Mentions(rows, "entirely on this machine"));
        Assert.True(Mentions(rows, "Enter is never pressed for you"));
    }

    // ---- the voice picker ------------------------------------------------

    // Built with a placeholder and nothing else. The real list arrives only when
    // the dropdown is opened, which is what keeps building this section from
    // shelling out to `say -v ?` — and is why this test can exist.
    [AvaloniaFact]
    public void TheVoicePickerStartsAsAPlaceholderWithoutEnumeratingAnything()
    {
        ClaudeBuddySettings.ReloadForTests();
        var rows = NewWindow().VoiceRows();

        var combo = rows
            .SelectMany(r => r.GetLogicalDescendants().OfType<ComboBox>())
            .FirstOrDefault();

        Assert.NotNull(combo);
        Assert.Equal(0, combo!.SelectedIndex);
        Assert.Single(combo.ItemsSource!.Cast<object>());
    }
}
