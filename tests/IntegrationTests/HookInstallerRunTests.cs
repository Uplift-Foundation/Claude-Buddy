using Xunit;

namespace ClaudeBuddy.Tests;

// HookInstaller.Run and RunScript against real subprocesses, and the log they
// leave (CB-258). The thing being pinned is that a run which fails, hangs, is
// not found or cannot start now *says so* — before this it was swallowed whole,
// and a profile added in Settings that was never wired looked the same as one
// that was.
//
// The fake installers are one-line shell/cmd commands rather than files, since
// what is under test is what Run keeps of a child's exit code and streams, not
// any particular script. The real scripts are driven separately below
// (HookInstallerScriptsTests).
[Collection("LogDir")]
public class HookInstallerRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb-hookrun-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _logScope;

    public HookInstallerRunTests()
    {
        Directory.CreateDirectory(_root);
        _logScope = CrashLog.ScopeForTests(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        _logScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A command that prints "out" to stdout and "err" to stderr and exits with
    // the given code, in whichever shell this OS has.
    private static (string File, string[] Args) Fake(int exitCode) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"echo out& echo err 1>&2& exit {exitCode}" })
            : ("/bin/sh", new[] { "-c", $"echo out; echo err >&2; exit {exitCode}" });

    private string Log() =>
        File.Exists(HookInstallerLog.Path_) ? File.ReadAllText(HookInstallerLog.Path_) : "";

    [Fact]
    public void ASuccessfulRunKeepsExitCodeAndOutputAndIsLogged()
    {
        var (file, args) = Fake(0);

        var result = HookInstaller.Run(file, args, "fake-installer");

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out", result.Output);
        Assert.Contains("err", result.Error);
        Assert.Contains("fake-installer  Ok  exit 0", Log());
    }

    [Fact]
    public void ANonzeroExitIsAFailureWithItsStderrAndIsLogged()
    {
        var (file, args) = Fake(3);

        var result = HookInstaller.Run(file, args, "fake-installer");

        Assert.Equal(HookInstallOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("err", result.Error);

        var log = Log();
        Assert.Contains("fake-installer  Failed  exit 3", log);
        Assert.Contains("      err", log);
    }

    [Fact]
    public void AChildThatHangsIsKilledAndReportedAsTimedOutWithWhatItSaid()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "echo started& ping -n 30 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", "echo started; sleep 30" });

        var result = HookInstaller.Run(file, args, "fake-installer", timeoutMs: 1_000);

        Assert.Equal(HookInstallOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Contains("started", result.Output);
        Assert.Contains("fake-installer  TimedOut", Log());
    }

    // The drain-order fix: the old Run read stdout to the end and only then
    // stderr, so a child that fills the stderr pipe (about 64 KB) before closing
    // stdout blocked on its write and the run sat there until the timeout. Here
    // stderr is 300 KB written *before* any stdout, with a timeout far longer
    // than the run needs, so a regression shows up as TimedOut rather than Ok.
    [Fact]
    public void AChildThatFloodsStderrBeforeWritingStdoutDoesNotStallTheRun()
    {
        if (OperatingSystem.IsWindows()) return; // sh and head; the drain order is not OS-specific

        var result = HookInstaller.Run("/bin/sh",
            new[] { "-c", "head -c 300000 /dev/zero | tr '\\0' x >&2; echo done" },
            "fake-installer", timeoutMs: 15_000);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Equal(300_000, result.Error.Length);
        Assert.Contains("done", result.Output);
    }

    [Fact]
    public void AProgramThatCannotBeStartedIsReportedNotThrown()
    {
        var missing = Path.Combine(_root, "no-such-installer");

        var result = HookInstaller.Run(missing, Array.Empty<string>(), "fake-installer");

        Assert.Equal(HookInstallOutcome.Threw, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("fake-installer  Threw", Log());
    }

    [Fact]
    public void AnEnvironmentOverrideReachesTheChildAndANullValueRemovesOne()
    {
        const string removed = "CB258_REMOVED_VARIABLE";
        Environment.SetEnvironmentVariable(removed, "inherited");
        try
        {
            var (file, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", $"echo set=%CB258_SET_VARIABLE% gone=%{removed}%" })
                : ("/bin/sh", new[] { "-c", $"echo set=$CB258_SET_VARIABLE gone=${removed}" });

            var result = HookInstaller.Run(file, args, "fake-installer", environment:
                new Dictionary<string, string?> { ["CB258_SET_VARIABLE"] = "yes", [removed] = null });

            Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
            Assert.Contains("set=yes", result.Output);
            // cmd leaves an unset %VAR% unexpanded; sh expands it to nothing.
            Assert.DoesNotContain("inherited", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(removed, null);
        }
    }

    [Fact]
    public void AMissingScriptIsReportedAsNotFoundAndLogged()
    {
        var empty = Path.Combine(_root, "empty-app", "Contents", "MacOS");
        Directory.CreateDirectory(empty);

        var result = HookInstaller.RunScript("install-macos-hooks.sh", baseDirectory: empty);

        Assert.Equal(HookInstallOutcome.ScriptNotFound, result.Outcome);
        Assert.Equal("install-macos-hooks.sh", result.Script);
        Assert.Contains("install-macos-hooks.sh  ScriptNotFound", Log());
    }

    [Fact]
    public void AFoundScriptIsRunWithTheAutoColorFlagOnlyWhenAsked()
    {
        if (OperatingSystem.IsWindows()) return; // RunScript's interpreter is /bin/bash

        var macOs = Path.Combine(_root, "app", "Contents", "MacOS");
        var resources = Path.Combine(_root, "app", "Contents", "Resources");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "fake-installer.sh"), "echo \"args:$*\"\n");

        var plain = HookInstaller.RunScript("fake-installer.sh", autoColor: false, baseDirectory: macOs);
        var colored = HookInstaller.RunScript("fake-installer.sh", autoColor: true, baseDirectory: macOs);

        Assert.Equal("args:", plain.Output.Trim());
        Assert.Equal("args:--auto-color", colored.Output.Trim());
    }

    [Fact]
    public void IsWiredLooksForTheHookScriptInTheProfilesSettings()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".wired"));
        Directory.CreateDirectory(Path.Combine(home, ".bare"));
        File.WriteAllText(Path.Combine(home, ".wired", "settings.json"),
            """{"hooks":{"Stop":[{"hooks":[{"command":"bash \"$HOME/.claude/claude-buddy/ClaudeBuddyHook.sh\" idle"}]}]}}""");
        File.WriteAllText(Path.Combine(home, ".bare", "settings.json"), """{"theme":"auto"}""");

        Assert.True(HookInstaller.IsWiredIn(".wired", home));
        Assert.False(HookInstaller.IsWiredIn(".bare", home));
        Assert.False(HookInstaller.IsWiredIn(".absent", home));
    }

    [Fact]
    public void IsWiredReadsTheRealHomeAndFindsNothingForAProfileThatDoesNotExist()
    {
        Assert.False(HookInstaller.IsWired(".cb258-no-such-profile-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void IsWiredIsFalseRatherThanThrowingWhenTheFileCannotBeRead()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".locked"));
        var settings = Path.Combine(home, ".locked", "settings.json");
        File.WriteAllText(settings, "ClaudeBuddyHook");

        // Held open with no sharing, so the read throws an IOException on every
        // OS — the shape of a settings file mid-write by Claude Code itself.
        using var hold = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(HookInstaller.IsWiredIn(".locked", home));
    }

    [Fact]
    public void TheLogRotatesAtItsCeilingInsteadOfDroppingTheNewestEntry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HookInstallerLog.Path_)!);
        File.WriteAllText(HookInstallerLog.Path_, new string('x', (int)HookInstallerLog.MaxBytes));

        HookInstallerLog.Record("fake-installer", HookInstallResult.NotFound("fake-installer"));

        Assert.StartsWith(new string('x', 10), File.ReadAllText(HookInstallerLog.Path_ + ".1"));
        var fresh = Log();
        Assert.Contains("fake-installer  ScriptNotFound", fresh);
        Assert.DoesNotContain("xxxxxxxxxx", fresh);
    }

    [Fact]
    public void ALogDirectoryThatCannotBeCreatedIsSurvived()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where the log directory should go");

        using var scope = CrashLog.ScopeForTests(Path.Combine(blocker, "logs"));

        var result = HookInstaller.Run(Fake(0).File, Fake(0).Args, "fake-installer");

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
    }
}
