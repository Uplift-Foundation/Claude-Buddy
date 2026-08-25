using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.UiTests;

// Whether flicking a switch should start a download, and two picker rules that
// only show up with an odd value already stored.
//
// The download decisions are separated from the handlers that act on them
// precisely so they can be tested: acting means fetching 300MB or 150MB, which no
// test may do, but deciding is arithmetic over what is already on disk. Both
// halves of each decision matter — enabling with the engine already present must
// NOT re-download it, and disabling must never start one.
[Collection("Settings")]
public class SettingsDownloadDecisionTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(ctor);

        return (SettingsWindow)ctor!.Invoke(null);
    }

    // ---- the neural engine -------------------------------------------------

    [AvaloniaFact]
    public void TurningTheNeuralVoiceOffNeverStartsADownload()
    {
        ClaudeBuddySettings.ReloadForTests();

        Assert.False(SettingsWindow.ShouldStartNeuralDownload(false));
    }

    [AvaloniaFact]
    public void TurningItOnWithNothingOnDiskStartsOne()
    {
        ClaudeBuddySettings.ReloadForTests();
        WipeEngine();

        Assert.True(SettingsWindow.ShouldStartNeuralDownload(true));
    }

    // The case that costs 300MB if it is wrong: the engine is already there, so
    // enabling must not fetch it again.
    [AvaloniaFact]
    public void TurningItOnWithTheEngineAlreadyThereDoesNotRefetchIt()
    {
        ClaudeBuddySettings.ReloadForTests();
        PlaceEngine();

        try
        {
            Assert.True(NeuralSpeech.Installed);
            Assert.False(SettingsWindow.ShouldStartNeuralDownload(true));
        }
        finally
        {
            WipeEngine();
        }
    }

    // ---- dictation's model -------------------------------------------------

    [AvaloniaFact]
    public void TurningDictationOffNeverStartsADownload()
    {
        ClaudeBuddySettings.ReloadForTests();

        Assert.False(SettingsWindow.ShouldStartVoiceInputDownload(false));
    }

    // Whichever way round this machine happens to be, the rule is the same: it
    // asks for a download exactly when the model is missing. Derived rather than
    // asserted as a literal, since a developer running this may well have the
    // model already.
    [AvaloniaFact]
    public void TurningDictationOnAsksForTheModelOnlyWhenItIsMissing()
    {
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal(
            !SpeechTranscriber.ModelDownloaded,
            SettingsWindow.ShouldStartVoiceInputDownload(true));
    }

    // ---- pickers holding a value they were not offered ----------------------

    // A relay idle timeout set by hand — or carried over from a version with
    // different choices — is offered as itself rather than silently snapping to
    // the nearest option. Snapping would change a setting the user never touched.
    [AvaloniaFact]
    public void AHandWrittenRelayTimeoutIsOfferedAsItself()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = 7;

        var combo = (ComboBox)NewWindow().RemoteControlIdlePicker();
        var labels = combo.ItemsSource!.Cast<object>().Select(o => o?.ToString()).ToList();

        Assert.Contains(labels, l => l is not null && l.Contains("7"));
        Assert.True(combo.SelectedIndex >= 0);
    }

    // A value that IS one of the offered choices is not duplicated into the list
    // as well.
    [AvaloniaFact]
    public void AStandardRelayTimeoutIsNotOfferedTwice()
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlIdleMinutes = 7;
        var withHandWritten = ((ComboBox)NewWindow().RemoteControlIdlePicker())
            .ItemsSource!.Cast<object>().Count();

        ClaudeBuddySettings.RemoteControlIdleMinutes = 30;
        var standard = ((ComboBox)NewWindow().RemoteControlIdlePicker())
            .ItemsSource!.Cast<object>().Count();

        Assert.Equal(withHandWritten - 1, standard);
    }

    private static void PlaceEngine()
    {
        var version = NeuralSpeech.EngineVersion;
        var dir = Path.Combine(NeuralSpeech.Root, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, NeuralSpeech.EngineExeName), "not really an engine");
        File.WriteAllText(NeuralSpeech.ModelPath, "not really a model");
    }

    private static void WipeEngine()
    {
        if (Directory.Exists(NeuralSpeech.Root)) Directory.Delete(NeuralSpeech.Root, true);
    }
}
