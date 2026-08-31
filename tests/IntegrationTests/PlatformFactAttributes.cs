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

// The bash hook, plus a python3 to build the process tree it is judged against.
//
// HookPidWalkShTests stages a parent process with a chosen argv[0] and no
// controlling terminal, and neither of those is available from bash on macOS —
// there is no setsid(1), and `exec -a` cannot detach a terminal. python3's
// os.setsid()/os.execv() do both in two lines; that file's header explains why
// each is load-bearing rather than convenient.
//
// Present on both CI runners this suite runs on. Skipped rather than failed if
// it ever isn't, so a missing interpreter reads as "not exercised here" instead
// of as a broken hook.
public sealed class PythonUnixFactAttribute : FactAttribute
{
    public PythonUnixFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Skip = "bash hook only runs on macOS/Linux";
            return;
        }

        if (Python3Path() is null)
            Skip = "needs python3 to stage a tty-less parent with a chosen argv[0]";
    }

    // Resolved by hand rather than left to the shell: this suite invokes
    // interpreters directly (UseShellExecute = false), so a bare "python3"
    // would depend on the launching environment's PATH being inherited, which
    // is exactly the assumption BackgroundJobs.Read had to stop making.
    public static string? Python3Path()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "python3");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
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

// open(1) is macOS's, and so is every claim made about it. UnixFact would let
// this run on Linux, where /usr/bin/open is either absent or a completely
// different program — a green run there would mean nothing, and a red one would
// be reporting on the wrong binary.
public sealed class MacOpenFactAttribute : FactAttribute
{
    public MacOpenFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "drives /usr/bin/open, which is macOS's";
            return;
        }

        if (!File.Exists("/usr/bin/open"))
            Skip = "no /usr/bin/open on this machine";
    }
}

// The cloned-bundle cache is a macOS feature end to end: ClaudeDesktopBundles
// short-circuits to null everywhere else, so a "passing" run on Windows would be
// asserting on a function that declined to do anything. It also reads
// CFBundleVersion through plutil, which is macOS's — hence the second check,
// mirroring MacOpenFact rather than assuming a stock install.
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "the cloned-bundle cache is macOS-only";
            return;
        }

        if (!File.Exists("/usr/bin/plutil"))
            Skip = "no /usr/bin/plutil on this machine";
    }
}
