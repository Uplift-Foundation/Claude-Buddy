using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// ClaudeBuddySettings is a static class: one model shared by the whole
// process, guarded by its own lock but with no isolation between test
// cases. Every test here repoints CLAUDE_BUDDY_SETTINGS_DIR and calls
// ReloadForTests() before touching anything, and the whole class is
// [Collection("Settings")] so xUnit never runs two of them at once — without
// that, two settings tests running in parallel would stomp each other's
// env var and both read/write whichever directory won the race.
[CollectionDefinition("Settings")]
public class SettingsCollection
{
}

[Collection("Settings")]
public class SettingsRoundTripTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-integrationtests-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    [Fact]
    public void SettingWithADirectSetter_IsWrittenToDiskImmediately()
    {
        // TwoLetterGlyphs's setter calls Save() directly (ClaudeBuddySettings.cs
        // ~line 601), not SaveSoon() — no debounce, so it should be on disk the
        // instant the setter returns.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var settingsPath = Path.Combine(dir, "settings.json");
        Assert.True(File.Exists(settingsPath));

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root!["twoLetterGlyphs"]!.GetValue<bool>());
    }

    [Fact]
    public void ShowHeartbeats_DefaultsOnAndRoundTripsBothWays()
    {
        // Default-on is load-bearing rather than incidental: these orbs are on
        // screen today, and an upgrade that removed several of somebody's agents
        // would read as the gateway having dropped them.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.True(ClaudeBuddySettings.OpenClawShowHeartbeats);

        // False in particular, because false is the value a bug would produce by
        // accident — a missing key reads as default-true and would hide it.
        ClaudeBuddySettings.OpenClawShowHeartbeats = false;

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.False(root!["openclawShowHeartbeats"]!.GetValue<bool>());

        // And survives a reload, which is what the setting is for.
        PointSettingsAt(dir);
        Assert.False(ClaudeBuddySettings.OpenClawShowHeartbeats);
    }

    [Fact]
    public void ShowHeartbeats_IsInKnownKeys_SoItIsWrittenExactlyOnce()
    {
        // The trap KnownKeys' own comment describes: a key that Save writes but
        // that Load didn't recognise round-trips through _unknownKeys *as well
        // as* being written properly, and JsonObject rejects the duplicate. That
        // failure is a hard throw on every Save, so it is worth one assertion.
        //
        // Written as a real second launch rather than by reading KnownKeys: it is
        // the interaction between Load and Save that breaks, not the list.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        ClaudeBuddySettings.OpenClawShowHeartbeats = false;

        PointSettingsAt(dir);
        ClaudeBuddySettings.TwoLetterGlyphs = true;   // any Save at all

        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
        var root = JsonNode.Parse(text) as JsonObject;

        Assert.False(root!["openclawShowHeartbeats"]!.GetValue<bool>());

        // One occurrence, not two.
        var occurrences = text.Split("\"openclawShowHeartbeats\"").Length - 1;
        Assert.Equal(1, occurrences);
    }

    // The single most valuable untested piece of ClaudeBuddySettings.cs, per
    // its own comment on _unknownKeys (~line 38): Save() rebuilds the whole
    // document from the in-memory model, so any key it doesn't know about
    // would otherwise be silently deleted the next time anything is saved —
    // observed for real as speakCommand/neuralVoiceEnabled/neuralVoice
    // vanishing when an older build ran briefly next to a newer settings.json.
    // _unknownKeys exists so a build that has never heard of a key still
    // writes it back untouched.
    [Fact]
    public void AnUnknownSettingsKey_SurvivesASaveTriggeredByAnUnrelatedChange()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");

        File.WriteAllText(settingsPath, """
            {
              "showOrbs": true,
              "twoLetterGlyphs": false,
              "futureFeatureFlag": true
            }
            """);

        PointSettingsAt(dir);

        // Any real setter works here; TwoLetterGlyphs's Save() is direct and
        // synchronous, so the write below is guaranteed to have happened by
        // the time this method returns.
        ClaudeBuddySettings.TwoLetterGlyphs = true;

        var rewritten = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(rewritten);
        Assert.True(
            rewritten!.ContainsKey("futureFeatureFlag"),
            "a key this build doesn't recognise must round-trip through _unknownKeys " +
            "rather than being silently dropped on the next save");
        Assert.True(rewritten["futureFeatureFlag"]!.GetValue<bool>());

        // And the real change this Save() was actually for still landed.
        Assert.True(rewritten["twoLetterGlyphs"]!.GetValue<bool>());
    }

    // IdleColor/GeneratingColor/WaitingColor are the only three setters that
    // go through SaveSoon() instead of Save() directly (grep confirms — see
    // ClaudeBuddySettings.cs ~line 630-646), because the colour pickers raise
    // a change event on every pointer move and a direct Save() per event
    // would thrash the disk. SaveSoon() debounces via a DispatcherTimer.
    //
    // The task brief for this test assumed that constructing/starting that
    // timer with no Avalonia dispatcher running throws, and that the write
    // therefore falls through to a synchronous Save() immediately. Checked
    // by hand with a reflection probe before writing this: it does not.
    // Avalonia.Threading.DispatcherTimer constructs and Starts() without any
    // exception in a plain xUnit process (Avalonia lazily creates a
    // per-thread dispatcher rather than requiring Application.Run() first),
    // and 1 full second of Thread.Sleep afterwards was not enough for
    // settings.json to appear — because nothing in a console test process
    // ever pumps that dispatcher's queue, the timer is left "enabled" but
    // never actually ticks, and Save() is simply never called.
    //
    // So the real, exercisable contract is FlushPendingSave() — the method
    // this class exposes specifically for "a pending deferred write must not
    // be lost", per its own comment ("A deferred write that never happens is
    // a preference silently lost, so anything that might be the last thing
    // to happen calls this."). This test proves that contract: the debounced
    // setter alone leaves nothing on disk, and FlushPendingSave() is what
    // actually gets it there.
    [Fact]
    public void ADeferredColorSetting_IsNotOnDiskUntilFlushPendingSaveIsCalled()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        ClaudeBuddySettings.IdleColor = "green";

        Assert.False(
            File.Exists(settingsPath),
            "SaveSoon() debounces via a DispatcherTimer that nothing in this test process pumps, " +
            "so the write should still be pending immediately after the setter returns");

        ClaudeBuddySettings.FlushPendingSave();

        Assert.True(
            File.Exists(settingsPath),
            "FlushPendingSave() exists precisely so a pending deferred write is not silently lost");

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        var orbColors = root!["orbColors"] as JsonObject;
        Assert.NotNull(orbColors);
        Assert.Equal("green", orbColors!["idle"]!.GetValue<string>());
    }
}
