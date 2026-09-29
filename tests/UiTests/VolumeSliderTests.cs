using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.UiTests;

// CB-200's two sliders in the settings window: that each renders over the
// right range holding the saved level, that moving it writes the setting,
// and that the Speech one is disabled and labelled — never a slider that
// silently does nothing — while a custom speak command is selected.
//
// In the Settings collection because every case flips process-wide settings
// (the engine and both levels) before building a window; see
// SettingsCollection.cs. Each case restores what it changed.
[Collection("Settings")]
public class VolumeSliderTests
{
    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!.Invoke(null);

    private static void With(string engine, double speech, double alert, Action body)
    {
        var wasEngine = ClaudeBuddySettings.SpeakEngine;
        var wasSpeech = ClaudeBuddySettings.SpeechVolume;
        var wasAlert = ClaudeBuddySettings.AlertVolume;
        try
        {
            ClaudeBuddySettings.SpeakEngine = engine;
            ClaudeBuddySettings.SpeechVolume = speech;
            ClaudeBuddySettings.AlertVolume = alert;
            body();
        }
        finally
        {
            ClaudeBuddySettings.SpeakEngine = wasEngine;
            ClaudeBuddySettings.SpeechVolume = wasSpeech;
            ClaudeBuddySettings.AlertVolume = wasAlert;
        }
    }

    private static Control RowLabelled(Control[] rows, string label) =>
        rows.Single(r => r.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == label));

    // ---- discoverable ------------------------------------------------------

    [AvaloniaFact]
    public void TheVoiceSectionOffersASpeechVolumeRowHoldingTheSavedLevel() =>
        With("system", 0.4, 1.0, () =>
        {
            var row = RowLabelled(NewWindow().VoiceRows(), "Speech volume");
            var slider = row.GetLogicalDescendants().OfType<Slider>().Single();

            Assert.Equal(AudioVolume.Min, slider.Minimum);
            Assert.Equal(AudioVolume.Max, slider.Maximum);
            Assert.Equal(0.4, slider.Value, 3);
            Assert.True(slider.IsSnapToTickEnabled);
            Assert.Equal(AudioVolume.Step, slider.TickFrequency);
            Assert.Equal("40%", ToolTip.GetTip(slider));
        });

    [AvaloniaFact]
    public void TheSoundsSectionOffersAnAlertVolumeRowHoldingTheSavedLevel() =>
        With("system", 1.0, 0.65, () =>
        {
            var row = RowLabelled(NewWindow().SoundRows(), "Alert volume");
            var slider = row.GetLogicalDescendants().OfType<Slider>().Single();

            Assert.Equal(0.65, slider.Value, 3);
            Assert.True(slider.IsEnabled);
        });

    [AvaloniaFact]
    public void BothRowsAreFoundBySearchingForVolume() =>
        With("system", 1.0, 1.0, () =>
        {
            var window = NewWindow();
            var rows = window.VoiceRows().Concat(window.SoundRows()).OfType<SettingsWindow.SettingsRow>().ToList();

            Assert.Equal(2, rows.Count(r => r.SearchText?.Contains("volume", StringComparison.OrdinalIgnoreCase) == true
                                            && r.GetLogicalDescendants().OfType<Slider>().Any()));
        });

    // ---- persists ----------------------------------------------------------

    [AvaloniaFact]
    public void MovingTheSpeechSliderWritesOnlyTheSpeechLevel() =>
        With("system", 1.0, 0.8, () =>
        {
            var window = NewWindow();
            window.VoiceRows();
            var slider = window.SpeechVolumeSlider!;

            slider.Value = 0.5;
            Assert.Equal(0.5, ClaudeBuddySettings.SpeechVolume, 3);
            Assert.Equal(0.8, ClaudeBuddySettings.AlertVolume, 3);
            Assert.Equal("50%", ToolTip.GetTip(slider));

            // An unrelated property changing is not a level change.
            slider.MinWidth = 200;
            Assert.Equal(0.5, ClaudeBuddySettings.SpeechVolume, 3);

            // And it survives a reload from disk, as a relaunch would.
            ClaudeBuddySettings.ReloadForTests();
            Assert.Equal(0.5, ClaudeBuddySettings.SpeechVolume, 3);
        });

    [AvaloniaFact]
    public void MovingTheAlertSliderWritesOnlyTheAlertLevel() =>
        With("system", 0.9, 1.0, () =>
        {
            var slider = NewWindow().AlertVolumeSlider();

            slider.Value = 0.25;
            Assert.Equal(0.25, ClaudeBuddySettings.AlertVolume, 3);
            Assert.Equal(0.9, ClaudeBuddySettings.SpeechVolume, 3);

            ClaudeBuddySettings.ReloadForTests();
            Assert.Equal(0.25, ClaudeBuddySettings.AlertVolume, 3);
        });

    // ---- disabled on Custom ------------------------------------------------

    [AvaloniaTheory]
    [InlineData("system")]
    [InlineData("neural")]
    public void AnEngineThatHonoursALevelHasAnEnabledSliderAndNoNote(string engine) =>
        With(engine, 1.0, 1.0, () =>
        {
            var window = NewWindow();
            window.VoiceRows();

            Assert.True(window.SpeechVolumeSlider!.IsEnabled);
            Assert.False(window.SpeechVolumeNote!.IsVisible);
        });

    [AvaloniaFact]
    public void ACustomCommandGreysTheSpeechSliderAndSaysWhy() =>
        With("custom", 0.4, 1.0, () =>
        {
            var window = NewWindow();
            var row = RowLabelled(window.VoiceRows(), "Speech volume");

            Assert.False(window.SpeechVolumeSlider!.IsEnabled);
            Assert.True(window.SpeechVolumeNote!.IsVisible);
            Assert.Equal("Not supported for custom commands", window.SpeechVolumeNote.Text);

            // The note is inside the row itself, beside the slider it explains.
            Assert.Contains(window.SpeechVolumeNote, row.GetLogicalDescendants());
            // The saved level is still shown, not reset, so switching back to a
            // voice that honours it finds it where it was left.
            Assert.Equal(0.4, window.SpeechVolumeSlider.Value, 3);
        });

    [AvaloniaFact]
    public void ACustomCommandLeavesTheAlertSliderAlone() =>
        With("custom", 1.0, 0.5, () =>
        {
            var window = NewWindow();
            window.VoiceRows();
            var alert = RowLabelled(window.SoundRows(), "Alert volume")
                .GetLogicalDescendants().OfType<Slider>().Single();

            Assert.True(alert.IsEnabled);
            Assert.False(window.SpeechVolumeSlider!.IsEnabled);
        });

    // ---- noted on a fallback engine (CB-200 review) -------------------------

    // Kokoro selected, but only an older engine on disk: it ignores the level,
    // so the row says so — and the slider stays live, because the level is
    // saved for when this build's engine arrives. The negative control is the
    // same setup with this build's own engine, which must show no note; a
    // fixture that failed to place anything would fail that half, not pass it.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOlderNeuralEngineIsNotedWithoutGreyingTheSlider(bool olderOnly) =>
        With("neural", 0.4, 1.0, () => WithEngineOnDisk(olderOnly ? "0.0.1-older" : NeuralSpeech.EngineVersion, () =>
        {
            var window = NewWindow();
            var row = RowLabelled(window.VoiceRows(), "Speech volume");

            Assert.True(window.SpeechVolumeSlider!.IsEnabled);
            Assert.Equal(olderOnly, window.SpeechVolumeNote!.IsVisible);
            Assert.Equal(olderOnly ? AudioVolume.FallbackEngineNote : null, window.SpeechVolumeNote.Text);
            Assert.Contains(window.SpeechVolumeNote, row.GetLogicalDescendants());
        }));

    // Puts a fake engine (and the model) where NeuralSpeech looks, under the
    // per-run settings directory TestBootstrap isolates, with the neural voice
    // switched on — and removes exactly what it placed.
    private static void WithEngineOnDisk(string version, Action body)
    {
        var wasEnabled = ClaudeBuddySettings.NeuralVoiceEnabled;
        var directory = Path.Combine(NeuralSpeech.Root, version);
        var modelExisted = File.Exists(NeuralSpeech.ModelPath);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, NeuralSpeech.EngineExeName), Array.Empty<byte>());
        if (!modelExisted) File.WriteAllBytes(NeuralSpeech.ModelPath, Array.Empty<byte>());
        try
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = true;
            body();
        }
        finally
        {
            ClaudeBuddySettings.NeuralVoiceEnabled = wasEnabled;
            Directory.Delete(directory, recursive: true);
            if (!modelExisted) File.Delete(NeuralSpeech.ModelPath);
        }
    }

    // ---- labels wrap rather than clip --------------------------------------

    // The Sounds row's "When a session needs you" was cut to "When a session
    // need" beside Windows' wider combo box. Every Row label now wraps, so a
    // long one takes a second line instead of running under its control.
    [AvaloniaFact]
    public void ALongRowLabelWrapsInsteadOfClipping() =>
        With("system", 1.0, 1.0, () =>
        {
            var label = RowLabelled(NewWindow().SoundRows(), "When a session needs you")
                .GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == "When a session needs you");

            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, label.TextWrapping);
        });

    // Choosing a voice can change the engine without rebuilding the window;
    // the slider has to follow it both ways.
    [AvaloniaFact]
    public void TheSliderFollowsTheEngineWhenTheChoiceChanges() =>
        With("system", 1.0, 1.0, () =>
        {
            var window = NewWindow();
            window.VoiceRows();
            Assert.True(window.SpeechVolumeSlider!.IsEnabled);

            ClaudeBuddySettings.SpeakEngine = "custom";
            window.RefreshSpeechVolumeAvailability();
            Assert.False(window.SpeechVolumeSlider.IsEnabled);
            Assert.True(window.SpeechVolumeNote!.IsVisible);

            ClaudeBuddySettings.SpeakEngine = "neural";
            window.RefreshSpeechVolumeAvailability();
            Assert.True(window.SpeechVolumeSlider.IsEnabled);
            Assert.False(window.SpeechVolumeNote.IsVisible);
        });

    // The real wiring: the voice picker's own SelectionChanged is what calls
    // the refresh. Driven without opening the dropdown — which would enumerate
    // the machine's voices — by handing the placeholder combo a second item
    // and selecting it; ChooseVoice returns early on the unscanned
    // placeholder, and the refresh still runs.
    [AvaloniaFact]
    public void TheVoicePickersSelectionChangeRefreshesTheSlider() =>
        With("system", 1.0, 1.0, () =>
        {
            var window = NewWindow();
            var rows = window.VoiceRows();
            var picker = RowLabelled(rows, "Speak voice").GetLogicalDescendants().OfType<ComboBox>().Single();
            Assert.True(window.SpeechVolumeSlider!.IsEnabled);

            ClaudeBuddySettings.SpeakEngine = "custom";
            picker.ItemsSource = new[] { "placeholder", "another" };
            picker.SelectedIndex = 1;

            Assert.False(window.SpeechVolumeSlider.IsEnabled);
        });
}
