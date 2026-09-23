using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-167's orb half: the right-click "Sound" submenu, driven the same way
// OrbWindowPresenceTests drives ApplyEndSessionGuard — through
// RebuildSoundSubmenus() directly rather than a synthesized right-click,
// since opening a real ContextMenu needs a shown window and a working popup
// that nothing else in this suite depends on.
//
// [Collection("Settings")] because every case here reads and writes
// ClaudeBuddySettings.OrbTurnSounds, which is process-wide the same way
// every other settings-touching suite in this collection is.
[Collection("Settings")]
public class OrbSoundSubmenuTests : IDisposable
{
    private readonly List<string> _clearedKeys = new();

    public void Dispose()
    {
        SettingsWindow.ChooseSoundFileForTests = null;
        foreach (var key in _clearedKeys) ClaudeBuddySettings.ClearOrbTurnSound(key);
    }

    private OrbWindow NewOrb(string? soundKey = null)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString())
        {
            SoundKey = soundKey ?? "orb-sound-test-" + Guid.NewGuid()
        };
        _clearedKeys.Add(orb.SoundKey);
        return orb;
    }

    private static List<string> SystemSounds() => SystemSoundCatalog.List(
        SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);

    // FindControl rather than reflection into the compiler-generated field —
    // it resolves through the same NameScope a real x:Name reference does,
    // and needs no BindingFlags guess about the generated field's visibility.
    private static MenuItem FinishedMenu(OrbWindow orb) =>
        orb.FindControl<MenuItem>("SoundFinishedMenuItem")!;

    private static MenuItem AttentionMenu(OrbWindow orb) =>
        orb.FindControl<MenuItem>("SoundAttentionMenuItem")!;

    // --- building the list ---

    [AvaloniaFact]
    public void RebuildingPopulatesBothSubmenusWithTheDefaultCheckedByDefault()
    {
        var orb = NewOrb();

        orb.RebuildSoundSubmenus();

        var finished = FinishedMenu(orb).Items.Cast<MenuItem>().ToList();
        var attention = AttentionMenu(orb).Items.Cast<MenuItem>().ToList();

        Assert.Equal($"Default ({SystemSoundCatalog.DefaultFinishedSoundName})", finished[0].Header);
        Assert.True(finished[0].IsChecked);
        Assert.All(finished.Skip(1), item => Assert.False(item.IsChecked));

        Assert.Equal($"Default ({SystemSoundCatalog.DefaultAttentionSoundName})", attention[0].Header);
        Assert.True(attention[0].IsChecked);

        // needsAttentionSound has no vibe-summary value.
        Assert.DoesNotContain(attention, item => Equals(item.Header, SettingsWindow.VibeSummaryLabel));
        Assert.Contains(finished, item => Equals(item.Header, SettingsWindow.VibeSummaryLabel));
    }

    // Rebuilt fresh: a saved override is reflected as the checked item, not
    // just written and forgotten.
    [AvaloniaFact]
    public void TheSavedOverrideIsTheCheckedItem()
    {
        var orb = NewOrb();
        ClaudeBuddySettings.SetOrbTurnSound(orb.SoundKey, "off", null);

        orb.RebuildSoundSubmenus();

        var finished = FinishedMenu(orb).Items.Cast<MenuItem>().ToList();
        var checkedItem = Assert.Single(finished, item => item.IsChecked);
        Assert.Equal("Off", checkedItem.Header);

        var attention = AttentionMenu(orb).Items.Cast<MenuItem>().ToList();
        Assert.True(attention[0].IsChecked); // still Default: only Finished was overridden
    }

    // --- selecting writes the override, and leaves the other trigger alone ---

    [AvaloniaFact]
    public void ClickingOffWritesTheFinishedOverrideWithoutTouchingAttention()
    {
        var orb = NewOrb();
        ClaudeBuddySettings.SetOrbTurnSound(orb.SoundKey, null, "Ping");

        orb.RebuildSoundSubmenus();
        var offItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Off"));
        offItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        var over = ClaudeBuddySettings.OrbTurnSoundFor(orb.SoundKey);
        Assert.Equal("off", over?.Finished);
        Assert.Equal("Ping", over?.Attention);
    }

    [AvaloniaFact]
    public void ClickingDefaultClearsJustThatTriggersOverride()
    {
        var orb = NewOrb();
        ClaudeBuddySettings.SetOrbTurnSound(orb.SoundKey, "off", "Ping");

        orb.RebuildSoundSubmenus();
        var defaultItem = FinishedMenu(orb).Items.Cast<MenuItem>().First();
        defaultItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        var over = ClaudeBuddySettings.OrbTurnSoundFor(orb.SoundKey);
        Assert.Null(over?.Finished);
        Assert.Equal("Ping", over?.Attention);
    }

    // Clearing both triggers removes the entry entirely — SetOrbTurnSound's
    // own rule, reached here from the menu rather than asserted only against
    // the settings layer directly.
    [AvaloniaFact]
    public void ClickingDefaultOnBothTriggersRemovesTheEntryEntirely()
    {
        var orb = NewOrb();
        ClaudeBuddySettings.SetOrbTurnSound(orb.SoundKey, "off", "off");

        orb.RebuildSoundSubmenus();
        FinishedMenu(orb).Items.Cast<MenuItem>().First()
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        orb.RebuildSoundSubmenus();
        AttentionMenu(orb).Items.Cast<MenuItem>().First()
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor(orb.SoundKey));
    }

    [AvaloniaFact]
    public void ClickingASystemSoundWritesItsNameForAttention()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        var orb = NewOrb();
        orb.RebuildSoundSubmenus();

        var item = AttentionMenu(orb).Items.Cast<MenuItem>()
            .Single(i => Equals(i.Header, systemSounds[0]));
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(systemSounds[0], ClaudeBuddySettings.OrbTurnSoundFor(orb.SoundKey)?.Attention);
    }

    // --- Choose file… ---

    [AvaloniaFact]
    public async Task ChoosingChooseFileWritesThePickedPathForFinished()
    {
        var picked = OperatingSystem.IsWindows()
            ? @"C:\Users\me\Music\orb-chime.wav"
            : "/Users/me/orb-chime.wav";
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(picked);

        var orb = NewOrb();
        orb.RebuildSoundSubmenus();

        var chooseItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Choose file…"));
        chooseItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        // async void Click handler: yield so its continuation (which runs
        // past the already-completed seam task) gets a turn before the
        // assertion reads the setting it writes.
        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(picked, ClaudeBuddySettings.OrbTurnSoundFor(orb.SoundKey)?.Finished);
    }

    [AvaloniaFact]
    public async Task CancellingChooseFileWritesNothing()
    {
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(null);

        var orb = NewOrb();
        orb.RebuildSoundSubmenus();

        var chooseItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Choose file…"));
        chooseItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor(orb.SoundKey));
    }

    // --- an orb with no stable key yet ---

    // SetOrbTurnSound/OrbTurnSoundFor both no-op on an empty key (the same
    // rule ChatPanelSizeFor's neighbours follow), so an orb whose
    // RestoreOrbPosition hasn't run yet — SoundKey still "" — offers a menu
    // that simply writes nothing, rather than throwing or crashing the
    // context menu open.
    [AvaloniaFact]
    public void AnOrbWithNoSoundKeyYetDoesNotThrow()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        Assert.Equal("", orb.SoundKey);

        orb.RebuildSoundSubmenus();
        var offItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Off"));
        offItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Null(ClaudeBuddySettings.OrbTurnSoundFor(""));
    }

    // SessionMenu_Opening reaches RebuildSoundSubmenus() even when no
    // SessionManager is current — the guard below it is for
    // ApplyEndSessionGuard's own DependentsOf call, and must not also skip
    // the sound submenus, which have nothing to do with dependents.
    [AvaloniaFact]
    public void SessionMenuOpeningPopulatesTheSubmenusWithNoSessionManagerCurrent()
    {
        Assert.Null(SessionManager.Instance);

        var orb = NewOrb();
        orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        Assert.NotEmpty(FinishedMenu(orb).Items.Cast<MenuItem>());
        Assert.NotEmpty(AttentionMenu(orb).Items.Cast<MenuItem>());
    }
}
