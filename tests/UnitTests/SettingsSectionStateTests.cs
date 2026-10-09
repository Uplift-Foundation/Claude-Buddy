using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// ClaudeBuddySettings.IsSettingsSectionCollapsed / SetSettingsSectionCollapsed —
// the model behind CB-166's fold state, with no window in front of it.
//
// In the Settings collection for the reason SettingsCollection.cs gives:
// ClaudeBuddySettings is one process-wide static, and running these alongside
// anything else that touches it is exactly the once-in-five failure that rule
// exists to remove.
[Collection("Settings")]
public class SettingsSectionStateTests
{
    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-settings-sections-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject ReadRawSettings()
    {
        var text = File.ReadAllText(OrbweaverSettings.Path_);
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public void NothingStoredMeansNothingCollapsedAndNoKeyWritten()
    {
        FreshSettings();

        Assert.False(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));

        // A fresh install has never called SetSettingsSectionCollapsed, so
        // nothing has forced a save at all — no settings.json on disk yet,
        // not merely one with an empty array in it.
        Assert.False(File.Exists(OrbweaverSettings.Path_));
    }

    [Fact]
    public void CollapsingWritesTheIdAndExpandingRemovesIt()
    {
        FreshSettings();

        OrbweaverSettings.SetSettingsSectionCollapsed("voice", true);

        Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));
        var afterCollapse = ReadRawSettings();
        Assert.Equal(new[] { "voice" }, afterCollapse["collapsedSettingsSections"]!.AsArray()
            .Select(n => n!.GetValue<string>()));

        OrbweaverSettings.SetSettingsSectionCollapsed("voice", false);

        Assert.False(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));
        var afterExpand = ReadRawSettings();
        Assert.Empty(afterExpand["collapsedSettingsSections"]!.AsArray());
    }

    [Fact]
    public void SurvivesReloadForTests()
    {
        FreshSettings();

        OrbweaverSettings.SetSettingsSectionCollapsed("orbs", true);
        OrbweaverSettings.SetSettingsSectionCollapsed("codex", true);

        OrbweaverSettings.ReloadForTests();

        Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("orbs"));
        Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("codex"));
        Assert.False(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));
    }

    // A triangle emits one event per click, so setting a section to the state
    // it is already in must not touch the file — Save() is plain, not
    // SaveSoon(), and a no-change write here would mean every Rebuild() that
    // happens to re-seed a header costs a disk write for nothing.
    [Fact]
    public void SettingTheSameStateAgainDoesNotRewriteTheFile()
    {
        FreshSettings();

        OrbweaverSettings.SetSettingsSectionCollapsed("voice", true);
        var writtenAt = File.GetLastWriteTimeUtc(OrbweaverSettings.Path_);

        System.Threading.Thread.Sleep(20);
        OrbweaverSettings.SetSettingsSectionCollapsed("voice", true);

        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(OrbweaverSettings.Path_));
    }

    // An id this build has never heard of — a section removed in a later
    // version, say — round-trips rather than being silently dropped, the same
    // guard _unknownKeys gives whole keys.
    [Fact]
    public void AnUnknownIdIsKeptAndIgnored()
    {
        FreshSettings();

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(OrbweaverSettings.Path_)!, "settings.json"),
            """{ "collapsedSettingsSections": ["some-future-section"] }""");
        OrbweaverSettings.ReloadForTests();

        Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("some-future-section"));
        Assert.False(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));

        OrbweaverSettings.SetSettingsSectionCollapsed("voice", true);

        var raw = ReadRawSettings();
        Assert.Contains("some-future-section", raw["collapsedSettingsSections"]!.AsArray()
            .Select(n => n!.GetValue<string>()));
    }

    // A garbage entry — a number, an object — costs only itself. Load() sits
    // inside one catch that replaces the *entire* model with defaults on any
    // exception, so this has to be defended per entry or one bad hand-edit to
    // this array would reset every other setting in the file, not just this
    // one's fold state.
    [Fact]
    public void AGarbageEntryCostsOnlyItself()
    {
        FreshSettings();

        OrbweaverSettings.SpeakVoice = "Samantha";
        OrbweaverSettings.SetSettingsSectionCollapsed("voice", true);

        File.WriteAllText(
            OrbweaverSettings.Path_,
            ReadRawSettings().ToJsonString().Replace(
                "\"voice\"",
                "\"voice\", 42, { \"nested\": true }"));

        OrbweaverSettings.ReloadForTests();

        // The garbage entries did not survive, but the real neighbour did...
        Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("voice"));

        // ...and, critically, so did an entirely unrelated setting in the same
        // file — proof the outer catch-all never fired.
        Assert.Equal("Samantha", OrbweaverSettings.SpeakVoice);
    }
}
