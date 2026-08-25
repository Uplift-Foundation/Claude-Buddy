using System.Text.Json.Nodes;
using Xunit;

namespace ClaudeBuddy.Tests;

// The two settings the URL router keeps, through a real settings.json.
//
// Both matter beyond the usual round-trip check. RouteClaudeUrls decides
// whether Claude Buddy claims a *system-wide* URL scheme, so a value that
// failed to persist would either leave the schemes claimed after the user
// turned it off, or re-claim them on every launch after they said no.
// PreviousClaudeUrlHandler is what makes that claim reversible at all — lose
// it and the scheme cannot be handed back to whoever owned it.
[Collection("Settings")]
public class ClaudeUrlRoutingSettingsTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-urlrouting-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
    }

    [Fact]
    public void RouteClaudeUrls_DefaultsOnAndRoundTripsBothWays()
    {
        // Default-on is deliberate: with more than one profile, the alternative
        // is a sign-in that silently completes in the wrong account. Nothing is
        // claimed until a second profile exists, so this costs a single-profile
        // install nothing.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.True(ClaudeBuddySettings.RouteClaudeUrls);

        ClaudeBuddySettings.RouteClaudeUrls = false;

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        Assert.False(root!["routeClaudeUrls"]!.GetValue<bool>());

        // And back through a real reload, not just the in-memory model.
        ClaudeBuddySettings.ReloadForTests();
        Assert.False(ClaudeBuddySettings.RouteClaudeUrls);

        ClaudeBuddySettings.RouteClaudeUrls = true;
        ClaudeBuddySettings.ReloadForTests();
        Assert.True(ClaudeBuddySettings.RouteClaudeUrls);
    }

    [Fact]
    public void PreviousClaudeUrlHandler_StartsEmptyAndRoundTrips()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Equal("", ClaudeBuddySettings.PreviousClaudeUrlHandler);

        ClaudeBuddySettings.PreviousClaudeUrlHandler = "com.anthropic.claudefordesktop";
        ClaudeBuddySettings.ReloadForTests();

        Assert.Equal("com.anthropic.claudefordesktop", ClaudeBuddySettings.PreviousClaudeUrlHandler);
    }

    [Fact]
    public void PreviousClaudeUrlHandler_TreatsNullAsCleared()
    {
        // Restore() writes "" back after handing the scheme over. A null
        // reaching the model would round-trip as a JSON null and come back as
        // an empty string anyway, so the setter normalises rather than letting
        // the two representations diverge.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        ClaudeBuddySettings.PreviousClaudeUrlHandler = "something";
        ClaudeBuddySettings.PreviousClaudeUrlHandler = null!;

        Assert.Equal("", ClaudeBuddySettings.PreviousClaudeUrlHandler);

        ClaudeBuddySettings.ReloadForTests();
        Assert.Equal("", ClaudeBuddySettings.PreviousClaudeUrlHandler);
    }

    [Fact]
    public void AMissingKeyReadsAsTheDefaultRatherThanFalse()
    {
        // The downgrade case the _unknownKeys machinery exists for: a
        // settings.json written by a build without this feature has no
        // routeClaudeUrls at all, and reading that as "off" would quietly
        // disable the fix for everyone upgrading.
        var dir = NewSettingsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"version":1,"showOrbs":true}""");

        PointSettingsAt(dir);

        Assert.True(ClaudeBuddySettings.RouteClaudeUrls);
        Assert.Equal("", ClaudeBuddySettings.PreviousClaudeUrlHandler);
    }
}
