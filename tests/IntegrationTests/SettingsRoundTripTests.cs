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

    // ArrangeAnchor is the shape's saved on-screen centre (see
    // SessionManager.ArrangementAnchor) — it must survive a restart or the
    // next "arrange" click after a relaunch silently falls back to the
    // screen-centre default, which reads to a user as "it forgot where I put
    // it". Written manually into Save()'s JsonObject and parsed back out of
    // Load()'s, the same way OrbPositions is a few lines above each — this
    // test exists because the first cut of this feature added the property
    // to Model without wiring either side, so it worked for the life of one
    // process and silently reset on every restart.
    [Fact]
    public void ArrangeAnchor_SurvivesARestart()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.ArrangeAnchor = new ClaudeBuddySettings.OrbPlacement(640, 360);

        // Simulate a relaunch: force the next access to re-read settings.json
        // from disk rather than serve the in-memory model.
        PointSettingsAt(dir);

        var restored = ClaudeBuddySettings.ArrangeAnchor;
        Assert.NotNull(restored);
        Assert.Equal(640, restored!.X);
        Assert.Equal(360, restored.Y);
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

    // The three Remote Control settings, round-tripped together.
    //
    // The specific hazard this guards is the one KnownKeys exists for: a key
    // written by Save() but missing from KnownKeys is *also* replayed out of
    // _unknownKeys, and JsonObject throws on the duplicate — so a forgotten
    // KnownKeys entry doesn't degrade, it breaks every Save from then on.
    // Reading the values back through a reload is what proves all five steps
    // (model field, accessor, Load parse, Save write, KnownKeys) were done.
    [Fact]
    public void RemoteControlSettings_RoundTripThroughDiskAndReload()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.RemoteControlEnabled = true;
        ClaudeBuddySettings.RemoteControlProfileDir = ".claude-board";
        ClaudeBuddySettings.RemoteControlIdleMinutes = 25;

        // Back from disk, not from the in-memory model the setters just wrote.
        PointSettingsAt(dir);

        Assert.True(ClaudeBuddySettings.RemoteControlEnabled);
        Assert.Equal(".claude-board", ClaudeBuddySettings.RemoteControlProfileDir);
        Assert.Equal(25, ClaudeBuddySettings.RemoteControlIdleMinutes);
    }

    [Fact]
    public void RemoteControlSettings_HaveSafeDefaultsBeforeAnyoneChoosesThem()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        // Off by default: enabling it is what permits Buddy to start a real
        // Claude Code session on the user's account, which costs them quota.
        Assert.False(ClaudeBuddySettings.RemoteControlEnabled);

        // Never empty — the bridge has to launch under some config directory.
        Assert.Equal(
            ClaudeBuddySettings.DefaultRemoteControlProfileDir,
            ClaudeBuddySettings.RemoteControlProfileDir);

        Assert.Equal(
            ClaudeBuddySettings.DefaultRemoteControlIdle,
            ClaudeBuddySettings.RemoteControlIdleMinutes);
    }

    // A negative idle would read as "already expired" to every comparison
    // downstream, stopping the bridge the instant it started — which would look
    // like the feature being broken rather than a bad setting.
    // RemoteControlIdleNever is the deliberate way to say "never stop".
    [Fact]
    public void RemoteControlIdleMinutes_ClampsANegativeToNever()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.RemoteControlIdleMinutes = -5;

        Assert.Equal(ClaudeBuddySettings.RemoteControlIdleNever, ClaudeBuddySettings.RemoteControlIdleMinutes);
    }

    // An unchosen profile stays null on disk rather than being written as a copy
    // of today's default, so changing the shipped default still reaches users
    // who never picked one.
    [Fact]
    public void RemoteControlProfileDir_IsNotWrittenToDiskUntilChosen()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        ClaudeBuddySettings.RemoteControlEnabled = true;

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root!.ContainsKey("remoteControlProfileDir"));
        Assert.Null(root["remoteControlProfileDir"]);

        // The accessor still answers with the default, so callers never see null.
        Assert.Equal(
            ClaudeBuddySettings.DefaultRemoteControlProfileDir,
            ClaudeBuddySettings.RemoteControlProfileDir);
    }

    // One panel size per agent, keyed and reloaded independently — the whole
    // point of the setting, and the part a shared-singleton bug would break
    // silently: ChatPanel is one window serving every session, so a size
    // stored under the wrong key looks fine until a second agent is opened.
    [Fact]
    public void ChatPanelSizes_RoundTripPerAgentThroughDisk()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetChatPanelSize("agent-a", 500, 600);
        ClaudeBuddySettings.SetChatPanelSize("agent-b", 300.4, 250.6);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        var sizes = root!["chatPanelSizes"] as JsonObject;
        Assert.NotNull(sizes);

        // Rounded on the way in, so a drag that ends on a fraction of a DIP
        // doesn't leave 250.60000000000002 in a file people hand-edit.
        Assert.Equal(300, sizes!["agent-b"]!["width"]!.GetValue<double>());
        Assert.Equal(251, sizes["agent-b"]!["height"]!.GetValue<double>());

        // Read back from disk rather than from the model that just wrote it:
        // the parse side is its own code path and had to be added by hand to
        // Load, which is exactly the step speakVoice was once missing.
        PointSettingsAt(dir);

        var a = ClaudeBuddySettings.ChatPanelSizeFor("agent-a");
        Assert.NotNull(a);
        Assert.Equal(500, a!.Width);
        Assert.Equal(600, a.Height);

        Assert.Equal(300, ClaudeBuddySettings.ChatPanelSizeFor("agent-b")!.Width);

        // Never resized means null, not a copy of the shipped default — that
        // is what lets ChatPanel keep owning what "default" means.
        Assert.Null(ClaudeBuddySettings.ChatPanelSizeFor("agent-never-opened"));

        // A session with no stable identity (a local CLI orb with no cwd) has
        // nothing to save under, and saying so must not create a "" entry.
        ClaudeBuddySettings.SetChatPanelSize("", 400, 400);
        Assert.Null(ClaudeBuddySettings.ChatPanelSizeFor(""));
    }
    // The hazard this file already documents for colours, in the newest block
    // to read numbers: Load() sits inside one catch that replaces the *whole*
    // model with defaults, so a wrong-typed value reaching GetValue<double>()
    // would cost every profile name and dragged orb position in the file over
    // one bad panel size. Number() is why it doesn't.
    [Fact]
    public void AMalformedChatPanelSize_CostsOnlyThatEntry()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");

        // Hand-written rather than round-tripped through the setters: these
        // are shapes the app itself would never produce, and a file people
        // edit is exactly where they come from.
        File.WriteAllText(settingsPath, """
        {
          "twoLetterGlyphs": true,
          "orbPositions": { "/some/repo": { "x": 12, "y": 34 } },
          "chatPanelSizes": {
            "agent-string-width": { "width": "wide", "height": 400 },
            "agent-missing-height": { "width": 500 },
            "agent-not-an-object": 7,
            "agent-null-width": { "width": null, "height": 400 },
            "agent-fine": { "width": 480, "height": 500 }
          }
        }
        """);

        PointSettingsAt(dir);

        // The good entry survives...
        var fine = ClaudeBuddySettings.ChatPanelSizeFor("agent-fine");
        Assert.NotNull(fine);
        Assert.Equal(480, fine!.Width);

        // ...each broken one is dropped rather than half-applied...
        Assert.Null(ClaudeBuddySettings.ChatPanelSizeFor("agent-string-width"));
        Assert.Null(ClaudeBuddySettings.ChatPanelSizeFor("agent-missing-height"));
        Assert.Null(ClaudeBuddySettings.ChatPanelSizeFor("agent-not-an-object"));
        Assert.Null(ClaudeBuddySettings.ChatPanelSizeFor("agent-null-width"));

        // ...and — the actual point — nothing else in the file was lost to it.
        Assert.True(ClaudeBuddySettings.TwoLetterGlyphs);
        var position = ClaudeBuddySettings.OrbPositionFor("/some/repo");
        Assert.NotNull(position);
        Assert.Equal(12, position!.X);
    }

    // A panel is re-bound every time its orb is clicked, and the drag handler
    // saves on every pointer release — so the guard against rewriting an
    // unchanged value is what keeps that from being a file write each time.
    // Asserted by deleting the file and checking nothing recreates it, which
    // is deterministic where comparing timestamps would not be.
    [Fact]
    public void SettingAChatPanelSizeToTheValueItAlreadyHas_WritesNothing()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        ClaudeBuddySettings.SetChatPanelSize("agent-a", 500, 600);
        Assert.True(File.Exists(settingsPath));

        File.Delete(settingsPath);

        // Same size, including a fraction that rounds to the same whole DIPs
        // as the stored value — the comparison happens after rounding.
        ClaudeBuddySettings.SetChatPanelSize("agent-a", 500, 600);
        ClaudeBuddySettings.SetChatPanelSize("agent-a", 500.2, 599.8);

        Assert.False(
            File.Exists(settingsPath),
            "an unchanged size should return before Save(), so nothing recreates the deleted file");

        // A real change still writes, so the guard isn't just refusing to save.
        ClaudeBuddySettings.SetChatPanelSize("agent-a", 501, 600);
        Assert.True(File.Exists(settingsPath));
    }

    // chatPanelSizes is a key an older build has never heard of, which is the
    // situation _unknownKeys exists for — worth pinning for this key
    // specifically, since the generic test uses an invented one that no
    // version of this app will ever write.
    [Fact]
    public void ChatPanelSizes_SurviveASaveByABuildThatOnlyKnowsOtherKeys()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.SetChatPanelSize("agent-a", 500, 600);
        ClaudeBuddySettings.TwoLetterGlyphs = true;
        ClaudeBuddySettings.IdleColor = "green";
        ClaudeBuddySettings.FlushPendingSave();

        PointSettingsAt(dir);

        var a = ClaudeBuddySettings.ChatPanelSizeFor("agent-a");
        Assert.NotNull(a);
        Assert.Equal(500, a!.Width);
        Assert.Equal(600, a.Height);
    }
}
