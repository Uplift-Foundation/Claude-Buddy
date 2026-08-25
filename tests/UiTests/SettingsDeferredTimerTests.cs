using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The other half of the deferred write: what happens where there IS a dispatcher.
//
// tests/IntegrationTests covers the same setters in a process with no dispatcher
// loop, and found that nothing throws there — an Avalonia DispatcherTimer
// constructs and starts happily with no loop running, so the write simply waits
// for a tick that never comes. This suite has a real dispatcher, so the timer is
// genuinely created and started, which is the path the running app takes.
//
// The timer is never waited on. Its interval exists so that dragging across a
// colour wheel costs one write instead of several hundred, and a test that slept
// for it would be the sixth flake of the shape this branch has spent five commits
// removing. FlushPendingSave is the seam, and it is the same seam the app uses
// from everything that might be the last thing to happen.
[Collection("Settings")]
public class SettingsDeferredTimerTests
{
    private static string Stage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-deferred-ui-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        return dir;
    }

    private static string OnDisk(string dir) =>
        File.Exists(Path.Combine(dir, "settings.json"))
            ? File.ReadAllText(Path.Combine(dir, "settings.json"))
            : "";

    [AvaloniaFact]
    public void ADeferredSettingIsHeldAndThenFlushed()
    {
        var dir = Stage();

        ClaudeBuddySettings.IdleColor = "#abcdef";

        Assert.Equal("", OnDisk(dir));

        ClaudeBuddySettings.FlushPendingSave();

        Assert.Contains("abcdef", OnDisk(dir));
    }

    // Several changes in a row cost one write, which is the entire point: the
    // colour pickers raise their change event on every pointer move across the
    // wheel.
    [AvaloniaFact]
    public void ManyChangesInARowCostOneWrite()
    {
        var dir = Stage();

        for (var i = 0; i < 50; i++)
        {
            ClaudeBuddySettings.IdleColor = $"#0000{i:X2}";
        }

        Assert.Equal("", OnDisk(dir));

        ClaudeBuddySettings.FlushPendingSave();

        // The last one wins, not the first.
        Assert.Contains("000031", OnDisk(dir));
    }

    // The timer is reused rather than replaced on each change — it is created
    // once and restarted — so a second round of changes after a flush still
    // defers rather than writing straight through.
    [AvaloniaFact]
    public void TheTimerIsReusedAcrossRoundsOfChanges()
    {
        var dir = Stage();

        ClaudeBuddySettings.IdleColor = "#111111";
        ClaudeBuddySettings.FlushPendingSave();
        Assert.Contains("111111", OnDisk(dir));

        ClaudeBuddySettings.GeneratingColor = "#222222";
        Assert.DoesNotContain("222222", OnDisk(dir));

        ClaudeBuddySettings.FlushPendingSave();
        Assert.Contains("222222", OnDisk(dir));
    }
}
