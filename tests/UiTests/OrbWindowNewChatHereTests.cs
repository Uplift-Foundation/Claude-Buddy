using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-168: the orb context menu's "New chat here" item — visible only for a
// local-CLI orb, and what it would pre-fill the dialog with.
//
// [Collection("Settings")]: constructing an OrbWindow reads a colour setting
// in a field initializer — see SettingsCollection.cs.
[Collection("Settings")]
public class OrbWindowNewChatHereTests
{
    private static SessionStatus Status(SessionSource source, string cwd = "/repo/one") =>
        new() { Source = source, Cwd = cwd, State = "idle" };

    // --- NewChatPrefillFor: pure, no window ---

    [Fact]
    public void NoStatusMeansNoPrefill()
    {
        Assert.Null(OrbWindow.NewChatPrefillFor(null));
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void ALocalSessionPrefillsItsOwnCliAndCwd(SessionSource source)
    {
        var prefill = OrbWindow.NewChatPrefillFor(Status(source, "/repo/mine"));

        Assert.NotNull(prefill);
        Assert.Equal(NewChatOrbWatch.CliOf(source), prefill.Value.Cli);
        Assert.Equal("/repo/mine", prefill.Value.Cwd);
    }

    // A non-local session (OpenClaw, remote-control) prefills a null CLI —
    // there's no local CLI to pick — but still carries the cwd through
    // rather than refusing outright. Only IsLocalCli decides whether the
    // menu item is even shown; this stays a total function either way.
    [Fact]
    public void ANonLocalSessionPrefillsANullCli()
    {
        var prefill = OrbWindow.NewChatPrefillFor(Status(SessionSource.OpenClaw));

        Assert.NotNull(prefill);
        Assert.Null(prefill.Value.Cli);
    }

    // --- the menu item's own visibility, via UpdateFrom ---

    [AvaloniaFact]
    public void TheItemIsVisibleForEveryLocalCli()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(SessionSource.ClaudeCode));

        Assert.True(orb.NewChatHereItem.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void TheItemIsHiddenForNonLocalSources(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(source));

        Assert.False(orb.NewChatHereItem.IsVisible);
    }
}
