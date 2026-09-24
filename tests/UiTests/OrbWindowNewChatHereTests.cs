using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-168: the orb context menu's "New chat here" item — visible for a
// local-CLI orb or an OpenClaw orb, and what it would pre-fill the dialog
// with in each case.
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
        Assert.Null(OrbWindow.NewChatPrefillFor(null, "any-id"));
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void ALocalSessionPrefillsItsOwnCliAndCwd(SessionSource source)
    {
        var prefill = OrbWindow.NewChatPrefillFor(Status(source, "/repo/mine"), "session-id");

        Assert.NotNull(prefill);
        Assert.Equal(NewChatOrbWatch.CliOf(source), prefill.Value.Cli);
        Assert.Equal("/repo/mine", prefill.Value.Cwd);
        Assert.Null(prefill.Value.AgentId);
    }

    // An OpenClaw session prefills a null CLI — there's no local CLI to pick
    // — but carries the agent id parsed out of the session's own key instead
    // (OpenClawSessions.AgentIdOf's "openclaw:agent:<id>:<surface>" shape,
    // confirmed against a real gateway in docs/openclaw-findings.md).
    [Fact]
    public void AnOpenClawSessionPrefillsItsOwnAgentIdAndNoCli()
    {
        var prefill = OrbWindow.NewChatPrefillFor(
            Status(SessionSource.OpenClaw), "openclaw:agent:main:dashboard:abc123");

        Assert.NotNull(prefill);
        Assert.Null(prefill.Value.Cli);
        Assert.Equal("main", prefill.Value.AgentId);
    }

    // A non-local, non-OpenClaw session (remote-control, Claude Cloud)
    // prefills nothing useful — there's no local CLI and no OpenClaw agent —
    // but this stays a total function rather than refusing outright; only
    // the menu item's own visibility (IsLocalCli || OpenClaw) decides
    // whether this is ever called for one.
    [Fact]
    public void ARemoteControlSessionPrefillsNeitherACliNorAnAgent()
    {
        var prefill = OrbWindow.NewChatPrefillFor(Status(SessionSource.RemoteControl), "session-id");

        Assert.NotNull(prefill);
        Assert.Null(prefill.Value.Cli);
        Assert.Null(prefill.Value.AgentId);
    }

    // --- the menu item's own visibility, via UpdateFrom ---

    [AvaloniaTheory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    [InlineData(SessionSource.OpenClaw)]
    public void TheItemIsVisibleForEveryLocalCliAndOpenClaw(SessionSource source)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(source));

        Assert.True(orb.NewChatHereItem.IsVisible);
    }

    [AvaloniaFact]
    public void TheItemIsHiddenForRemoteControl()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(SessionSource.RemoteControl));

        Assert.False(orb.NewChatHereItem.IsVisible);
    }
}
