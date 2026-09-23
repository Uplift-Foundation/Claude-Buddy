using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-167's settings-window half: the master switch and the two pickers,
// driven the same way SettingsWindowPickerTests drives ClickPicker/
// ShapePicker — through the control each builder hands back, never by
// hunting the visual tree, because what a themed row actually contains
// depends on which template loaded and this suite has no business testing
// that.
//
// ChimePlayer.PlayForTests stands in for the sound card, the same seam
// TurnSoundScanTests uses for the scan side of this feature — every case
// here proves what *would* play without a process or a speaker anywhere
// near the test host. SoundChoices' system-sound entries are read off
// SystemSoundCatalog.DefaultDirectory/DefaultExtensions directly rather
// than a temp directory, since that is exactly what SoundPicker itself
// does in production — the assertions below ask the same catalogue the
// production code asks, so they hold on both this machine and the CI
// runner's regardless of which sounds either one actually has installed.
[Collection("Settings")]
public class SoundSettingsRowTests : IDisposable
{
    private readonly bool _wasEnabled = ClaudeBuddySettings.TurnSoundsEnabled;
    private readonly string? _wasFinished = ClaudeBuddySettings.TurnFinishedSound;
    private readonly string? _wasAttention = ClaudeBuddySettings.NeedsAttentionSound;
    private readonly List<string> _played = new();

    public SoundSettingsRowTests()
    {
        ChimePlayer.PlayForTests = path => _played.Add(path);
    }

    public void Dispose()
    {
        ChimePlayer.PlayForTests = null;
        SettingsWindow.ChooseSoundFileForTests = null;
        ClaudeBuddySettings.TurnSoundsEnabled = _wasEnabled;
        ClaudeBuddySettings.TurnFinishedSound = _wasFinished;
        ClaudeBuddySettings.NeedsAttentionSound = _wasAttention;
    }

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static List<string> SystemSounds() => SystemSoundCatalog.List(
        SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);

    // --- SoundChoices, the list both the settings pickers and the orb's
    // Sound submenu build from ---

    [AvaloniaFact]
    public void FinishedChoicesLeadWithDefaultOffAndTheSummaryThenTheSystemSoundsThenChooseFile()
    {
        var choices = SettingsWindow.SoundChoices(null, "Glass", includeSummary: true);

        Assert.Equal($"Default (Glass)", choices[0].Label);
        Assert.Null(choices[0].Value);
        Assert.Equal(("Off", "off"), choices[1]);
        Assert.Equal((SettingsWindow.VibeSummaryLabel, "summary"), choices[2]);

        var systemSounds = SystemSounds();
        for (var i = 0; i < systemSounds.Count; i++)
        {
            Assert.Equal((systemSounds[i], systemSounds[i]), choices[3 + i]);
        }

        var last = choices[^1];
        Assert.Equal("Choose file…", last.Label);
        Assert.Equal(SettingsWindow.ChooseFileValue, last.Value);
    }

    // needsAttentionSound has no vibe-summary value — the settings comment on
    // it says so, and this is that rule reaching the list a user actually
    // sees.
    [AvaloniaFact]
    public void AttentionChoicesOmitTheSummary()
    {
        var choices = SettingsWindow.SoundChoices(null, "Ping", includeSummary: false);

        Assert.DoesNotContain(choices, c => c.Value == "summary");
        Assert.DoesNotContain(choices, c => c.Label == SettingsWindow.VibeSummaryLabel);
    }

    // A value this list would not otherwise offer — a hand-edited settings
    // file, or a sound removed since it was chosen — is shown under its own
    // name rather than silently dropped, the courtesy ClickPicker's own
    // unknown-value entry extends.
    [AvaloniaFact]
    public void AnUnknownSystemNameIsOfferedUnderItsOwnName()
    {
        var choices = SettingsWindow.SoundChoices("NotARealSound", "Glass", includeSummary: true);

        Assert.Contains(("NotARealSound", "NotARealSound"), choices);
    }

    // Same for an absolute path — a file the user chose in an earlier run —
    // except it is shown by its file name rather than the whole path, the
    // same reason SoundChoices' own comment gives.
    [AvaloniaFact]
    public void AnUnknownAbsolutePathIsOfferedUnderItsFileName()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\Users\me\Music\ding.wav" : "/Users/me/ding.wav";

        var choices = SettingsWindow.SoundChoices(path, "Glass", includeSummary: true);

        Assert.Contains((Path.GetFileName(path), path), choices);
    }

    // --- the pickers start on the saved value ---

    [AvaloniaFact]
    public void TheFinishedPickerStartsOnDefaultWhenNothingIsSaved()
    {
        ClaudeBuddySettings.TurnFinishedSound = null;

        var combo = SettingsWindow.TurnFinishedSoundPicker();

        Assert.Equal(0, combo.SelectedIndex);
    }

    [AvaloniaFact]
    public void TheFinishedPickerStartsOnOffWhenThatIsSaved()
    {
        ClaudeBuddySettings.TurnFinishedSound = "off";

        var combo = SettingsWindow.TurnFinishedSoundPicker();

        Assert.Equal(1, combo.SelectedIndex);
    }

    [AvaloniaFact]
    public void TheFinishedPickerStartsOnVibeSummaryWhenThatIsSaved()
    {
        ClaudeBuddySettings.TurnFinishedSound = "summary";

        var combo = SettingsWindow.TurnFinishedSoundPicker();

        var items = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();
        Assert.Equal(SettingsWindow.VibeSummaryLabel, items[combo.SelectedIndex]);
    }

    [AvaloniaFact]
    public void TheAttentionPickerStartsOnASavedSystemSound()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        ClaudeBuddySettings.NeedsAttentionSound = systemSounds[0];

        var combo = SettingsWindow.NeedsAttentionSoundPicker();

        var items = Assert.IsAssignableFrom<IEnumerable<string>>(combo.ItemsSource).ToList();
        Assert.Equal(systemSounds[0], items[combo.SelectedIndex]);
    }

    // --- choosing writes the setting, and previews once ---

    [AvaloniaFact]
    public void ChoosingOffWritesOffAndPlaysNothing()
    {
        ClaudeBuddySettings.TurnFinishedSound = null;
        var combo = SettingsWindow.TurnFinishedSoundPicker();

        combo.SelectedIndex = 1; // "Off"

        Assert.Equal("off", ClaudeBuddySettings.TurnFinishedSound);
        Assert.Empty(_played);
    }

    [AvaloniaFact]
    public void ChoosingVibeSummaryWritesSummaryAndPlaysNothing()
    {
        ClaudeBuddySettings.TurnFinishedSound = null;
        var combo = SettingsWindow.TurnFinishedSoundPicker();

        combo.SelectedIndex = 2; // "Vibe summary"

        Assert.Equal("summary", ClaudeBuddySettings.TurnFinishedSound);
        Assert.Empty(_played);
    }

    [AvaloniaFact]
    public void ChoosingBackToDefaultWritesNull()
    {
        ClaudeBuddySettings.TurnFinishedSound = "off";
        var combo = SettingsWindow.TurnFinishedSoundPicker();
        Assert.Equal(1, combo.SelectedIndex);

        combo.SelectedIndex = 0; // "Default (Glass)"

        Assert.Null(ClaudeBuddySettings.TurnFinishedSound);
    }

    [AvaloniaFact]
    public void ChoosingASystemSoundWritesItsNameAndPreviewsItOnce()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        ClaudeBuddySettings.NeedsAttentionSound = null;
        var combo = SettingsWindow.NeedsAttentionSoundPicker();

        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        combo.SelectedIndex = items.IndexOf(systemSounds[0]);

        Assert.Equal(systemSounds[0], ClaudeBuddySettings.NeedsAttentionSound);

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // --- Choose file… ---

    [AvaloniaFact]
    public void ChoosingChooseFileWritesThePickedPathAndSelectsItByName()
    {
        var picked = OperatingSystem.IsWindows()
            ? @"C:\Users\me\Music\custom-chime.wav"
            : "/Users/me/custom-chime.wav";

        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(picked);

        ClaudeBuddySettings.TurnFinishedSound = null;
        var combo = SettingsWindow.TurnFinishedSoundPicker();

        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        combo.SelectedIndex = items.IndexOf("Choose file…");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(picked, ClaudeBuddySettings.TurnFinishedSound);

        var reselected = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        Assert.Equal(Path.GetFileName(picked), reselected[combo.SelectedIndex]);
    }

    [AvaloniaFact]
    public void CancellingChooseFileLeavesTheSettingAndSelectionAlone()
    {
        ClaudeBuddySettings.TurnFinishedSound = "off";
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(null);

        var combo = SettingsWindow.TurnFinishedSoundPicker();
        var offIndex = combo.SelectedIndex;

        var items = ((IEnumerable<string>)combo.ItemsSource!).ToList();
        combo.SelectedIndex = items.IndexOf("Choose file…");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("off", ClaudeBuddySettings.TurnFinishedSound);
        Assert.Equal(offIndex, combo.SelectedIndex);
        Assert.Empty(_played);
    }

    // --- the preview button ---

    [AvaloniaFact]
    public void ThePreviewButtonReplaysWhateverIsCurrentlySelected()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        var window = NewWindow();
        var button = (Button)window.PreviewButton(() => systemSounds[0], "Glass");

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    [AvaloniaFact]
    public void PreviewSoundIsSilentForOffAndSummary()
    {
        SettingsWindow.PreviewSound("off", "Glass");
        SettingsWindow.PreviewSound("summary", "Glass");

        Assert.Empty(_played);
    }

    [AvaloniaFact]
    public void PreviewSoundResolvesTheDefaultWhenGivenNull()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        SettingsWindow.PreviewSound(null, systemSounds[0]);

        var expectedPath = SystemSoundCatalog.Resolve(
            systemSounds[0], SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);
        Assert.Equal(new[] { expectedPath }, _played);
    }

    // --- the master switch and the row list ---

    // Driven at the handler, not the control, for the reason
    // SettingsWindowRowTests' own header gives: a row's toggle is a
    // ToggleSwitch whose template is borrowed from Fluent at runtime and
    // falls back to a CheckBox when that fails, so which control a row holds
    // is a property of the theme rather than of the setting. Synthesizing a
    // click would be testing the borrow.
    [AvaloniaFact]
    public void TheMasterSwitchWritesTurnSoundsEnabled()
    {
        var window = NewWindow();

        window.OnTurnSoundsToggled(true);
        Assert.True(ClaudeBuddySettings.TurnSoundsEnabled);

        window.OnTurnSoundsToggled(false);
        Assert.False(ClaudeBuddySettings.TurnSoundsEnabled);
    }

    [AvaloniaFact]
    public void SoundRowsReturnsExactlyThreeRows()
    {
        var window = NewWindow();

        Assert.Equal(3, window.SoundRows().Length);
    }
}
