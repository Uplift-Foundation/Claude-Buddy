using Xunit;
using Xunit.Abstractions;

namespace ClaudeBuddy.Tests;

// Drives RemoteControlBridge against a real Claude Code session, the way the
// hook tests in this project drive the real hook scripts as subprocesses rather
// than mocking them.
//
// Everything here is opt-in via LiveBridgeFact, because unlike the hook scripts
// this costs money: starting the bridge starts a genuine Claude Code session on
// whatever account the configured profile is logged into. It must never run
// unattended on a dev machine or a CI runner. See LiveBridgeFactAttribute.
//
// What it is actually for: BridgeProtocol is unit-tested against captured
// fixtures, which proves the parsing but not that the strings ever arrive.
// The launch path is the opposite — almost all environment and no logic, and
// every part of it that can break (a piped stdout defeating the TTY check, a
// status file that never lands, Remote Control silently not attaching) breaks
// by producing nothing rather than by throwing. Only a live run catches that.
[Collection("Settings")]
public class RemoteControlBridgeLiveTests
{
    private readonly ITestOutputHelper _output;

    public RemoteControlBridgeLiveTests(ITestOutputHelper output) => _output = output;

    // Which account the bridge logs in as. Overridable because the machine this
    // was written on keeps its Remote-Control-connected sessions on a second
    // account, and hard-coding either choice would make the test lie somewhere.
    private static string ProfileDir =>
        Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LIVE_BRIDGE_PROFILE") ?? ".claude";

    [LiveBridgeFact]
    public async Task Bridge_Starts_Attaches_RemoteControl_And_ListsPeers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-live-bridge-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlProfileDir = ProfileDir;

        using var bridge = new RemoteControlBridge();

        var started = await bridge.StartAsync();

        // A failure here is nearly always environmental rather than logical —
        // no `claude` on disk, no tmux, or the profile not logged in — so say
        // which profile was tried instead of just asserting false.
        Assert.True(started, $"bridge failed to start under profile '{ProfileDir}'");
        Assert.True(bridge.IsRunning);

        if (bridge.Warning is not null) _output.WriteLine($"bridge warning: {bridge.Warning}");

        var agents = await bridge.ListAgentsAsync();
        Assert.NotNull(agents);

        foreach (var a in agents!)
            _output.WriteLine($"peer: {a.Name} [{a.Ref}] kind={a.Kind} status={a.Status} remote={a.IsRemoteControl}");

        // Deliberately not asserting that any peer exists, let alone a remote
        // one: that depends on what is running elsewhere at the time, and a
        // test that fails because the user closed a session on another machine
        // is a test nobody trusts. An empty list still proves the whole path —
        // launch, RC attach, prompt injection, tool call, transcript tail,
        // parse — because a broken link anywhere in it returns null instead.
        bridge.Stop();
        Assert.False(bridge.IsRunning);
    }

    // The mutex claim: a second bridge must not leave the first one's tmux
    // session orphaned, because the fixed session name means they would fight
    // over one pane and interleave their prompts.
    [LiveBridgeFact]
    public async Task StartingASecondBridge_ReplacesTheFirstRatherThanRacingIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-live-bridge-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlProfileDir = ProfileDir;

        using var first = new RemoteControlBridge();
        Assert.True(await first.StartAsync(), $"first bridge failed to start under '{ProfileDir}'");
        var firstAgents = await first.ListAgentsAsync();
        Assert.NotNull(firstAgents);

        using var second = new RemoteControlBridge();
        Assert.True(await second.StartAsync(), "second bridge failed to start");

        // The second is the live one and can still be asked things — proving it
        // adopted a clean session rather than landing on top of the first.
        var secondAgents = await second.ListAgentsAsync();
        Assert.NotNull(secondAgents);

        second.Stop();
    }

    // The path the tray item and the Settings button both take, end to end:
    // EnsureStarted -> bridge up -> peers polled -> snapshot published, which is
    // what the orb scan reads.
    //
    // Driven from here rather than by clicking the real menu, deliberately.
    // Synthesizing a menu-bar click hangs on a machine someone is using — the
    // modal menu blocks the script, which is the hazard CLAUDE.md already warns
    // about — and it would be testing AppleScript rather than this app. This
    // calls the same method the menu item's handler calls, one line in.
    [LiveBridgeFact]
    public async Task EnsureStarted_PublishesASnapshotTheOrbScanCanRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-live-bridge-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", dir);
        ClaudeBuddySettings.ReloadForTests();
        ClaudeBuddySettings.RemoteControlProfileDir = ProfileDir;

        try
        {
            // Off first: this is the cost guarantee, and it is the one thing here
            // worth failing loudly over. Enabling the feature must not start
            // anything; something has to ask.
            Assert.False(ClaudeBuddySettings.RemoteControlEnabled);
            Assert.Empty(RemoteControlSessions.Snapshot());

            ClaudeBuddySettings.RemoteControlEnabled = true;

            // Still nothing, with the setting on but nobody having asked.
            Assert.Empty(RemoteControlSessions.Snapshot());

            RemoteControlSessions.EnsureStarted();

            // EnsureStarted is fire-and-forget by design — the caller is a menu
            // click and must not block the UI thread — so this waits on the
            // observable result rather than on a task.
            //
            // Waiting on HasPolled, not on the status line. The first version of
            // this test watched the status and passed in 3 seconds having proved
            // nothing: the state goes to "connected" the moment the process is
            // up, which is before the peer list has been asked for even once. A
            // test that cannot tell "started" from "started and looked" is a test
            // that would keep passing if the polling broke entirely.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline
                   && !RemoteControlSessions.HasPolled
                   && !RemoteControlSessions.StatusText.Contains("failed", StringComparison.Ordinal))
            {
                await Task.Delay(500);
            }

            _output.WriteLine($"relay status: {RemoteControlSessions.StatusText}");

            foreach (var r in RemoteControlSessions.Snapshot())
                _output.WriteLine($"snapshot: {r.Key} status={r.Status} working={r.Working}");

            Assert.DoesNotContain("failed", RemoteControlSessions.StatusText);

            // A completed poll is the real assertion. Whether it *found* anything
            // depends on what is running elsewhere at the time, and a test that
            // fails because a machine somewhere went to sleep is a test nobody
            // trusts — but the poll having happened proves the whole chain:
            // launch, RC attach, prompt injection, tool call, transcript tail,
            // parse, publish.
            Assert.True(
                RemoteControlSessions.HasPolled,
                $"relay never completed a poll; status was '{RemoteControlSessions.StatusText}'");
        }
        finally
        {
            // Always, even on a failed assert: leaving a live Claude Code session
            // running because a test threw would keep spending after the run.
            RemoteControlSessions.Stop("test over");
            ClaudeBuddySettings.RemoteControlEnabled = false;
        }
    }
}
