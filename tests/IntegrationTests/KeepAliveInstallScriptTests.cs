using System.Diagnostics;
using System.Xml;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-49: tools/install-hooks.sh is also where the crash keep-alive
// LaunchAgent gets written and torn down, on macOS -- see that script's own
// header comment for why it lives there rather than in a separate tool. This
// drives it as a real subprocess, the same pattern HookScriptShTests uses
// for ClaudeBuddyHook.sh, and stops short of the one thing that genuinely
// can't be exercised headlessly: a real `launchctl load`/`unload` against
// this machine's actual launchd session. CLAUDE_BUDDY_KEEPALIVE_DRY_RUN
// suppresses that call so these can run on CI without registering or
// unregistering anything real; CLAUDE_BUDDY_LAUNCHAGENTS_DIR and
// CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES redirect the plist path and the
// "which app is installed" search away from this machine's real
// ~/Library/LaunchAgents and /Applications, the same test-seam pattern
// CLAUDE_BUDDY_SETTINGS_DIR already uses elsewhere in this repo.
//
// Not covered here, and named rather than silently skipped: an actual
// `launchctl load` succeeding or failing against a real launchd session, and
// the Windows Scheduled Task side of CB-49 (tools/ClaudeBuddy.iss), which
// needs a real Inno Setup compile and a real Windows Task Scheduler to
// verify and which this environment has neither of.
public class KeepAliveInstallScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Script = Path.Combine(RepoRoot, "tools", "install-hooks.sh");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "install-hooks.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find tools/install-hooks.sh by walking up from " + AppContext.BaseDirectory);
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

    // A null value removes the variable from the child's environment, which
    // inherits this process's — and TestBootstrap has set
    // CLAUDE_BUDDY_SETTINGS_DIR in it.
    private static ScriptResult Run(string[] args, IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (key, value) in env)
                if (value is null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new ScriptResult(proc.ExitCode, stdout, stderr);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_NamesBundleIdThrottleAndExecutable()
    {
        var result = Run(["--print-keepalive-plist", "/Applications/Orbweaver.app/Contents/MacOS/ClaudeBuddy"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        // Must match build-macos-app.sh's BUNDLE_ID -- a mismatch here would
        // mean the plist install-hooks.sh writes doesn't reconcile with any
        // app build-macos-app.sh actually installs. Unchanged by the CB-255
        // rename, deliberately: the LaunchAgent's label is the bundle id.
        Assert.Contains("<string>io.github.wtvamp.claudebuddy</string>", result.Stdout);
        Assert.Contains("<integer>60</integer>", result.Stdout);
        Assert.Contains("/Applications/Orbweaver.app/Contents/MacOS/ClaudeBuddy", result.Stdout);
    }

    // CB-255: the keep-alive's log goes beside the app's own logs, which moved
    // from Logs/ClaudeBuddy to Logs/Orbweaver.
    [MacKeepAliveFact]
    public void PrintKeepalivePlist_LogsUnderTheOrbweaverLogsFolder()
    {
        var result = Run(["--print-keepalive-plist", "/tmp/fake/ClaudeBuddy"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/Library/Logs/Orbweaver/keepalive.log</string>", result.Stdout);
        Assert.DoesNotContain("Logs/ClaudeBuddy", result.Stdout);
    }

    // ---- CB-255: which settings.json, with no override set ------------------
    //
    // HOME is a temp folder and CLAUDE_BUDDY_SETTINGS_DIR is removed, so the
    // script reads Application Support under the fake home exactly as it would
    // on a real Mac. The app candidates stay pinned to a fake bundle so only
    // the settings path varies.

    private static string Support(string home, string folder) =>
        Path.Combine(home, "Library", "Application Support", folder);

    private static void WriteSettings(string home, string folder, bool serve)
    {
        Directory.CreateDirectory(Support(home, folder));
        File.WriteAllText(Path.Combine(Support(home, folder), "settings.json"),
            $"{{ \"remoteControlServeOnLaunch\": {(serve ? "true" : "false")} }}");
    }

    private static ScriptResult KeepaliveOnlyUnderHome(string home, string launchAgents, string app) =>
        Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["HOME"] = home,
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = null,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgents,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
            ["CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES"] = app,
        });

    // Upgrade day: the app has not launched since the rename, so its settings
    // are still in the legacy folder. Reading only the new one would take
    // Serve on launch as off and tear the user's keep-alive down.
    [MacKeepAliveFact]
    public void KeepaliveOnly_ReadsTheLegacySettingsWhenTheOrbweaverFolderHasNone()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        var exePath = WriteFakeAppBundle(home.Path);
        WriteSettings(home.Path, "ClaudeBuddy", serve: true);

        var result = KeepaliveOnlyUnderHome(home.Path, launchAgentsDir.Path, Path.Combine(home.Path, "Fake.app"));

        Assert.Equal(0, result.ExitCode);
        var plistPath = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        Assert.True(File.Exists(plistPath), result.Stdout + result.Stderr);
        Assert.Contains(exePath, File.ReadAllText(plistPath));
    }

    // Both folders have settings: the new one wins, as it does in the app's
    // own migration. Its "false" removes a stale agent although the legacy
    // file still says true.
    [MacKeepAliveFact]
    public void KeepaliveOnly_TheOrbweaverSettingsWinOverTheLegacyOnes()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        WriteFakeAppBundle(home.Path);
        WriteSettings(home.Path, "ClaudeBuddy", serve: true);
        WriteSettings(home.Path, "Orbweaver", serve: false);
        var stalePlist = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        File.WriteAllText(stalePlist, "<stale/>");

        var result = KeepaliveOnlyUnderHome(home.Path, launchAgentsDir.Path, Path.Combine(home.Path, "Fake.app"));

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(stalePlist), "the legacy settings were read although the Orbweaver ones exist");
    }

    // The control for the two above: the new folder alone is read, so a pass
    // there cannot come from the script reading only the legacy one.
    [MacKeepAliveFact]
    public void KeepaliveOnly_ReadsTheOrbweaverSettings()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        WriteFakeAppBundle(home.Path);
        WriteSettings(home.Path, "Orbweaver", serve: true);

        var result = KeepaliveOnlyUnderHome(home.Path, launchAgentsDir.Path, Path.Combine(home.Path, "Fake.app"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist")));
    }

    // CB-255: with no candidate override, Orbweaver.app is looked for before
    // Claude Buddy.app. Both are planted under the fake home's Applications;
    // the assertion allows a real /Applications/Orbweaver.app on the machine
    // running the suite to win (it is ahead of ~/Applications), but never a
    // Claude Buddy.app while an Orbweaver.app exists.
    [MacKeepAliveFact]
    public void KeepaliveOnly_PrefersOrbweaverAppOverTheLegacyBundle()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        var applications = Path.Combine(home.Path, "Applications");
        WriteFakeAppBundle(applications, "Claude Buddy.app");
        WriteFakeAppBundle(applications, "Orbweaver.app");
        WriteSettings(home.Path, "Orbweaver", serve: true);

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["HOME"] = home.Path,
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = null,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
            ["CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES"] = null,
        });

        Assert.Equal(0, result.ExitCode);
        var plist = File.ReadAllText(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist"));
        Assert.Contains("/Orbweaver.app/Contents/MacOS/ClaudeBuddy", plist);
        Assert.DoesNotContain("Claude Buddy.app", plist);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_RestartsOnCrashOnly_NotOnADeliberateQuit()
    {
        var result = Run(["--print-keepalive-plist", "/tmp/fake/ClaudeBuddy"]);

        // KeepAlive as a dict with SuccessfulExit=false, not a bare <true/>:
        // a bare KeepAlive relaunches after ANY exit, including the app's own
        // normal Quit (App.axaml.cs's Shutdown(), which exits 0) -- exactly
        // the "app that will not stay quit" CB-49 itself warns against.
        Assert.Contains("<key>SuccessfulExit</key>", result.Stdout);
        Assert.Contains("<false/>", result.Stdout);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_IsWellFormedXml()
    {
        var result = Run(["--print-keepalive-plist", "/tmp/fake/ClaudeBuddy"]);

        Assert.Equal(0, result.ExitCode);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(result.Stdout), settings);
        while (reader.Read()) { } // throws XmlException on malformed input
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenServeOnLaunchIsFalse_RemovesAnyStaleAgentAndWritesNothing()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": false }");
        var stalePlist = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        File.WriteAllText(stalePlist, "<stale/>");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = settingsDir.Path,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(stalePlist),
            "turning the setting off should remove a stale agent instead of leaving it registered");
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenSettingsFileIsMissing_DoesNotEnableIt()
    {
        // A fresh install has no settings.json at all yet -- this is the
        // default-off case the ticket asks for, distinct from an explicit
        // "false".
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var fakeApp = new TempDir();
        WriteFakeAppBundle(fakeApp.Path);

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = settingsDir.Path,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
            ["CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(fakeApp.Path, "Fake.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist")));
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenServeOnLaunchIsTrue_AndAnAppIsFound_WritesAnAgentPointingAtIt()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var fakeApp = new TempDir();
        var exePath = WriteFakeAppBundle(fakeApp.Path);
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": true }");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = settingsDir.Path,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
            ["CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(fakeApp.Path, "Fake.app"),
        });

        Assert.Equal(0, result.ExitCode);
        var plistPath = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        Assert.True(File.Exists(plistPath));
        Assert.Contains(exePath, File.ReadAllText(plistPath));
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenServeOnLaunchIsTrue_AndNoAppIsFound_SkipsWithoutError()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": true }");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = settingsDir.Path,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
            // A candidate list pointing nowhere real, rather than the
            // hard-coded /Applications default -- otherwise this test's
            // result depends on whether the machine running it happens to
            // have Claude Buddy.app installed.
            ["CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES"] =
                Path.Combine(launchAgentsDir.Path, "Nowhere.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist")));
    }

    [MacKeepAliveFact]
    public void Uninstall_RemovesTheAgentEvenWhenServeOnLaunchIsStillTrue()
    {
        // Uninstalling the CLI hooks (--uninstall) is this repo's uninstall
        // path (see the DMG's "Read Me First.txt" and install-hooks.sh's own
        // header) -- the agent has to come out here regardless of what the
        // setting currently says, since the app it points at is on its way
        // out too.
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": true }");
        var plistPath = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        File.WriteAllText(plistPath, "<placeholder/>");

        var result = Run(["--keepalive-only", "--uninstall"], new Dictionary<string, string?>
        {
            ["CLAUDE_BUDDY_SETTINGS_DIR"] = settingsDir.Path,
            ["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "1",
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(plistPath));
    }

    // A minimal .app bundle: just enough for resolve_app_executable's
    // executable-exists check in install-hooks.sh to find something real.
    private static string WriteFakeAppBundle(string root, string name = "Fake.app")
    {
        var macosDir = Path.Combine(root, name, "Contents", "MacOS");
        Directory.CreateDirectory(macosDir);
        var exePath = Path.Combine(macosDir, "ClaudeBuddy");
        File.WriteAllText(exePath, "#!/bin/sh\n");
        File.SetUnixFileMode(exePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return exePath;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("cb-keepalive-").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
