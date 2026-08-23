using Xunit;

namespace ClaudeBuddy.Tests;

// ClaudeBuddyHook.sh and ClaudeBuddyHook.ps1 are twins of each other, one per
// platform, and only one of the two interpreters exists on any given CI
// runner or dev machine. These skip rather than fail to compile/run on the
// wrong OS, so a `dotnet test` run reports "skipped" for the twin that
// couldn't have run here instead of silently never exercising it.
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            Skip = "bash hook only runs on macOS/Linux";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "ps1 hook only runs on Windows";
    }
}

// Opt-in, and it has to stay that way: a test wearing this starts a **real
// Claude Code session** on the machine's own logged-in account and talks to
// whatever other sessions that account can see. Left to run by default it would
// spend the user's quota on every `dotnet test`, and spend a CI runner's on
// every push — which is why the env var is required rather than merely
// respected. Run it deliberately:
//
//   CLAUDE_BUDDY_LIVE_BRIDGE_TESTS=1 dotnet test tests/IntegrationTests/ClaudeBuddy.IntegrationTests.csproj
//
// The rest of the suite drives hook scripts, which are free and local; this is
// the first thing here with a bill attached, so it does not get to be quiet
// about it.
public sealed class LiveBridgeFactAttribute : FactAttribute
{
    public LiveBridgeFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Skip = "the Remote Control bridge is tmux-based, so macOS/Linux only";
            return;
        }

        if (Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LIVE_BRIDGE_TESTS") != "1")
        {
            Skip = "opt-in: starts a real Claude Code session and spends quota (set CLAUDE_BUDDY_LIVE_BRIDGE_TESTS=1)";
            return;
        }

        // Keeps these relays out of the installed app's way. The relay name is a
        // machine-wide mutex per account, so without a tag a test kills the
        // running app's relay, the app takes it back, and they trade it until one
        // loses a race — observed as the same test passing and failing on
        // consecutive runs.
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_RC_BRIDGE_TAG", "test");
    }
}
