using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// ClaudeDesktopOverlay.TintTarget: which Claude Desktop window, if any, should be
// wearing its profile's colour right now.
//
// This is the whole decision behind the window tint, and until CB-3 it was a
// lambda inside a method excluded from coverage — which means it was neither
// measured nor tested, in a feature whose failure mode is a coloured rectangle
// sitting on top of an application you were actually using. The prototype did
// exactly that, which is why the frontmost gate exists at all.
//
// In the Settings collection because For() reads the process-wide settings model.
[Collection("Settings")]
public class OverlayTintTargetTests
{
    private static ProfileView Profile(
        string directory, bool running, int pid, bool isDefault = false) =>
        new(DisplayName: directory,
            Directory: "/tmp/" + directory,
            IsDefault: isDefault,
            IsRunning: running,
            Pid: pid,
            Activity: ProfileActivity.None,
            Message: null,
            ThemeMode: "system");

    private static void SetTint(string folder, bool tint)
    {
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.Update(folder, p => p.TintWindow = tint);
    }

    [Fact]
    public void PicksTheRunningProfileWhosePidIsFrontmost()
    {
        SetTint("beta", true);
        var profiles = new List<ProfileView>
        {
            Profile("alpha", running: true, pid: 100),
            Profile("beta", running: true, pid: 200),
        };

        var chosen = ClaudeDesktopOverlay.TintTarget(profiles, 200);

        Assert.NotNull(chosen);
        Assert.Equal("/tmp/beta", chosen!.Directory);
    }

    [Fact]
    public void IgnoresAProfileThatIsNotRunning()
    {
        SetTint("alpha", true);
        var profiles = new List<ProfileView> { Profile("alpha", running: false, pid: 100) };

        Assert.Null(ClaudeDesktopOverlay.TintTarget(profiles, 100));
    }

    // A profile that has never been launched reports pid 0. Without the `Pid != 0`
    // guard, a frontmost pid of 0 — which is what FrontmostPid() returns when it
    // cannot tell — would match it and tint an instance that is not running.
    [Fact]
    public void NeverMatchesPidZeroEvenWhenFrontmostIsAlsoZero()
    {
        SetTint("alpha", true);
        var profiles = new List<ProfileView> { Profile("alpha", running: true, pid: 0) };

        Assert.Null(ClaudeDesktopOverlay.TintTarget(profiles, 0));
    }

    // Opting out of the window tint is a per-profile setting that keeps the
    // swatch and the Dock icon. It is not the same thing as the feature being off,
    // and the difference is only visible here.
    [Fact]
    public void RespectsAProfileThatHasOptedOutOfTheWindowTint()
    {
        SetTint("alpha", false);
        var profiles = new List<ProfileView> { Profile("alpha", running: true, pid: 100) };

        Assert.Null(ClaudeDesktopOverlay.TintTarget(profiles, 100));
    }

    [Fact]
    public void ReturnsNothingWhenNoProfileIsFrontmost()
    {
        SetTint("alpha", true);
        var profiles = new List<ProfileView> { Profile("alpha", running: true, pid: 100) };

        Assert.Null(ClaudeDesktopOverlay.TintTarget(profiles, 999));
    }

    [Fact]
    public void ReturnsNothingWhenThereAreNoProfilesAtAll()
    {
        Assert.Null(ClaudeDesktopOverlay.TintTarget(new List<ProfileView>(), 100));
    }

    // A folder with no stored settings gets ProfileSettings' defaults, and
    // TintWindow defaults to true — so an instance nobody has configured is
    // tinted rather than silently skipped.
    [Fact]
    public void TintsAProfileWithNoStoredSettingsAtAll()
    {
        ClaudeBuddySettings.ReloadForTests();
        var profiles = new List<ProfileView>
        {
            Profile("never-configured", running: true, pid: 100)
        };

        Assert.NotNull(ClaudeDesktopOverlay.TintTarget(profiles, 100));
    }

    // ---- SetEnabled -----------------------------------------------------

    // Safe to drive headlessly, which is worth saying because nothing else in
    // this class is: HideAll() returns immediately when no overlay window has
    // been created, and TrayController.Instance is null outside the real app.
    // So this exercises the gate, the setting write and the early return without
    // ever reaching a native window.

    [Fact]
    public void SetEnabledWritesTheSettingAndTheProperty()
    {
        ClaudeBuddySettings.ReloadForTests();
        var original = ClaudeDesktopOverlay.Enabled;
        try
        {
            ClaudeDesktopOverlay.SetEnabled(!original);

            Assert.Equal(!original, ClaudeDesktopOverlay.Enabled);
            Assert.Equal(!original, ClaudeBuddySettings.TintActiveWindow);
        }
        finally
        {
            ClaudeDesktopOverlay.SetEnabled(original);
        }
    }

    // The early return is not just an optimisation. Without it, setting the
    // value it already has would still write settings.json and refresh the tray,
    // and SetEnabled is called from a menu item that can be clicked repeatedly.
    [Fact]
    public void SetEnabledToTheCurrentValueChangesNothing()
    {
        ClaudeBuddySettings.ReloadForTests();
        var current = ClaudeDesktopOverlay.Enabled;
        ClaudeBuddySettings.TintActiveWindow = !current;

        ClaudeDesktopOverlay.SetEnabled(current);

        // Still the value the test put there, because SetEnabled returned before
        // writing anything.
        Assert.Equal(!current, ClaudeBuddySettings.TintActiveWindow);
        Assert.Equal(current, ClaudeDesktopOverlay.Enabled);
    }

    [Fact]
    public void SetEnabledFalseHidesEverythingWithoutNeedingAWindow()
    {
        ClaudeBuddySettings.ReloadForTests();
        var original = ClaudeDesktopOverlay.Enabled;
        try
        {
            ClaudeDesktopOverlay.SetEnabled(true);
            ClaudeDesktopOverlay.SetEnabled(false);

            Assert.False(ClaudeDesktopOverlay.Enabled);
        }
        finally
        {
            ClaudeDesktopOverlay.SetEnabled(original);
        }
    }
}
