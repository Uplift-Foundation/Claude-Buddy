using Xunit;

namespace ClaudeBuddy.Tests;

// The deferred write, and what happens when a write cannot happen at all.
//
// The colour pickers raise their change event on every pointer move across the
// wheel, so those three setters defer instead of writing — dragging across a
// gradient would otherwise be a few hundred writes of settings.json. Deferring
// means the last one can be lost, which is why FlushPendingSave exists and why
// anything that might be the last thing to happen calls it.
//
// This suite has no dispatcher LOOP running, and that turns out to be the
// interesting part. SaveSoon's catch says "No dispatcher... write now", but an
// Avalonia DispatcherTimer constructs and starts quite happily in a process with
// no loop: nothing throws, so the catch never runs and the write is deferred to a
// tick that never arrives. The preference then survives only because something
// calls FlushPendingSave. That is asserted below rather than assumed — and it
// means the catch is unreachable in practice, which is noted in the commit rather
// than "corrected", since making it reachable would be a behaviour change.
[Collection("Settings")]
public class SettingsDeferredWriteTests
{
    private static string Stage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-deferred-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        return dir;
    }

    private static string OnDisk(string dir) =>
        File.Exists(Path.Combine(dir, "settings.json"))
            ? File.ReadAllText(Path.Combine(dir, "settings.json"))
            : "";

    [Fact]
    public void WithNoDispatcherLoopADeferredSettingWaitsForAFlush()
    {
        var dir = Stage();

        ClaudeBuddySettings.IdleColor = "#123456";

        // Not on disk yet — see the note above. This is the assertion that says
        // the deferral really is a deferral rather than the immediate write the
        // catch's comment promises.
        Assert.Equal("", OnDisk(dir));

        ClaudeBuddySettings.FlushPendingSave();

        Assert.Contains("123456", OnDisk(dir));
    }

    // All three defer, and one flush writes all of them — which is the whole
    // point of coalescing: dragging across a colour wheel touches a setter
    // hundreds of times and should cost one write.
    [Fact]
    public void EachOfTheThreeOrbColoursDefersAndOneFlushWritesThemAll()
    {
        var dir = Stage();

        ClaudeBuddySettings.IdleColor = "#111111";
        ClaudeBuddySettings.GeneratingColor = "#222222";
        ClaudeBuddySettings.WaitingColor = "#333333";

        Assert.Equal("", OnDisk(dir));

        ClaudeBuddySettings.FlushPendingSave();

        var json = OnDisk(dir);
        Assert.Contains("111111", json);
        Assert.Contains("222222", json);
        Assert.Contains("333333", json);
    }

    // Flushing twice writes once. The second call sees a stopped timer and
    // returns, which is what makes it safe to call from everything that might be
    // the last thing to happen — and it is called from several such places, so it
    // runs far more often than there are pending writes.
    [Fact]
    public void FlushingTwiceIsHarmless()
    {
        var dir = Stage();
        ClaudeBuddySettings.IdleColor = "#123456";

        ClaudeBuddySettings.FlushPendingSave();
        var after = OnDisk(dir);

        ClaudeBuddySettings.FlushPendingSave();

        Assert.Equal(after, OnDisk(dir));
    }

    [Fact]
    public void FlushingBeforeAnythingHasEverBeenDeferredIsHarmless()
    {
        Stage();

        ClaudeBuddySettings.FlushPendingSave();
    }

    // A setting that does NOT defer is on disk the moment its setter returns.
    // The distinction is the reason SaveSoon exists at all, so it is worth one
    // assertion side by side with the deferred ones.
    [Fact]
    public void ASettingThatDoesNotDeferIsWrittenStraightAway()
    {
        var dir = Stage();

        ClaudeBuddySettings.TwoLetterGlyphs = !ClaudeBuddySettings.TwoLetterGlyphs;

        Assert.NotEqual("", OnDisk(dir));
    }

    // ---- a write that cannot happen --------------------------------------

    // Losing a preference is not worth taking the app down for, but losing it with
    // no trace is the "no error, just doesn't work" trap this project avoids
    // elsewhere on purpose. So a failed save is caught AND logged.
    //
    // Arranged per platform: there is no portable way to make a directory
    // unwritable — Unix has mode bits, Windows has ACLs — so on Windows this
    // reaches the same catch through a path that cannot be written instead.
    [Fact]
    public void AWriteThatCannotHappenIsLoggedRatherThanCrashingOrVanishing()
    {
        var log = Path.Combine(Path.GetTempPath(), "claude_buddy", "settings-errors.log");
        var before = File.Exists(log) ? new FileInfo(log).Length : 0;

        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(
                "CLAUDE_BUDDY_SETTINGS_DIR", @"C:\cb-tests\no:such|dir");
            ClaudeBuddySettings.ReloadForTests();

            ClaudeBuddySettings.TwoLetterGlyphs = !ClaudeBuddySettings.TwoLetterGlyphs;
        }
        else
        {
            var dir = Stage();
            File.SetUnixFileMode(dir, UnixFileMode.None);
            try
            {
                ClaudeBuddySettings.TwoLetterGlyphs = !ClaudeBuddySettings.TwoLetterGlyphs;
            }
            finally
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Assert.True(File.Exists(log), $"expected a failure log at {log}");
        Assert.True(new FileInfo(log).Length > before,
            "expected the save failure to be appended to the log");
        Assert.Contains("Save failed", File.ReadAllText(log));
    }
}
