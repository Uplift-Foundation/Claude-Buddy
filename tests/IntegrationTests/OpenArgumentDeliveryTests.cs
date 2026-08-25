using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests;

// The seam between ClaudeDesktopUrlRouter's command line and open(1), driven
// against a real /usr/bin/open and a real .app bundle.
//
// This suite exists because the unit test next door cannot fail the way this
// can. ClaudeDesktopUrlRoutingTests asserts the *order of our own array* — that
// the URL comes before "--args" and the switch after it — and that assertion
// passes whether or not open(1) does anything with the distinction. It is a
// test of a belief about someone else's program, written in a file that never
// runs it. CLAUDE.md is specific about this: a format someone else defines is
// covered here *as well as* by a unit test, "because the two fail differently".
//
// What is actually at stake is the OAuth callback. If the URL lands after
// "--args" it arrives as argv, the app's openURLs handler never fires, and the
// sign-in token is silently dropped into a window nobody was looking at — the
// exact failure the whole routing feature was built to stop, arriving from the
// one line of the fix that no test could see.
//
// The probe is a shell script in a bundle rather than a real application,
// which is enough: what is being asked is which channel open(1) chose, and a
// script that logs its own argv answers that. An argument it can see came
// through argv; one it cannot came through LaunchServices as an Apple Event,
// which is what openURLs receives.
public sealed class OpenArgumentDeliveryTests : IDisposable
{
    private const string Scheme = "cbprobe";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-open-" + Guid.NewGuid().ToString("N")[..12]);

    private string Bundle => Path.Combine(_root, "Probe.app");
    private string ArgvLog => Path.Combine(_root, "argv.log");

    public void Dispose()
    {
        // The long-lived probe from the -n case, if it is still sleeping. Keyed
        // on this instance's own temp path so a parallel test's probe is never
        // the one that gets killed.
        Run("/usr/bin/pkill", "-f", Bundle);

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A probe that has not finished dying still holds the executable.
            // Leaving a temp directory behind is not worth failing a run over.
        }
    }

    // The case the fix depends on, driven end to end.
    //
    // The URL is an operand and precedes "--args"; the switch follows it. If
    // open(1) works the way ClaudeDesktopUrlRouter believes, the app sees the
    // switch in argv and does *not* see the URL there — because the URL went
    // through LaunchServices instead, which is the channel openURLs listens on.
    [MacOpenFact]
    public void AUrlBeforeDashDashArgsIsDeliveredAsAnEventRatherThanAsArgv()
    {
        StageProbe();

        var url = $"{Scheme}://callback?code=abc123";
        var directory = Path.Combine(_root, "profile with space");

        // The production command line, not a hand-written one. The bundle path
        // is the only substitution: everything about the *shape* — where the
        // URL sits, where "--args" sits, that the environment variable rides
        // along — comes from the code under test.
        var route = new UrlRoute(directory, Bundle, directory, AlreadyRunning: false, Pid: 0);
        var arguments = ClaudeDesktopUrlRouter.Arguments(route, url);

        // -n is deliberately absent, and the next test is what that costs if it
        // is ever added. Asserted here too so the array being driven below is
        // known to be the one the router builds.
        Assert.DoesNotContain("-n", arguments);

        Launch(arguments);
        var argv = WaitForArgv();

        Assert.Contains("--user-data-dir=" + directory, argv);
        Assert.DoesNotContain(url, argv);

        // The switch survives the space in the path as a single token rather
        // than being split into two arguments — the other thing open(1) is
        // being trusted with, and every profile path contains "Application
        // Support".
        Assert.Contains(argv, argument => argument.EndsWith("profile with space"));
    }

    // The same launch with the URL moved after "--args": the mistake this is
    // guarding against, asserted as the failure it is rather than described in
    // a comment. Here the URL lands in argv, which means openURLs never fired
    // and a real Claude Desktop would have thrown the sign-in token away.
    //
    // Written out by hand on purpose. It is the one array in this file that the
    // production code must never produce, so it must not be built by the
    // production code.
    [MacOpenFact]
    public void AUrlAfterDashDashArgsLandsInArgvWhereNothingIsListeningForIt()
    {
        StageProbe();

        var url = $"{Scheme}://callback?code=abc123";
        var directory = Path.Combine(_root, "profile with space");

        Launch(new[] { "-a", Bundle, "--args", url, "--user-data-dir=" + directory });
        var argv = WaitForArgv();

        Assert.Contains(url, argv);
    }

    // "--args" does not imply "-n", which is the other half of the router's
    // command line being safe: a link routed to a profile that is already up
    // must reach the instance that is up, not start a second Chromium on its
    // userData directory. That is the concurrent leveldb access this whole
    // feature exists to prevent, so "we simply don't pass -n" deserves better
    // than being believed.
    //
    // The probe sleeps so there is something for LaunchServices to find. A
    // second `open` against a running bundle activates it instead of launching
    // it, so the log gains no second line.
    [MacOpenFact]
    public void OpenWithoutDashNDoesNotStartASecondInstance()
    {
        StageProbe(linger: true);

        Launch(new[] { "-a", Bundle, "--args", "--user-data-dir=/tmp/one" });
        WaitForArgv();

        Launch(new[] { "-a", Bundle, "--args", "--user-data-dir=/tmp/two" });

        // Nothing to wait *for* — the assertion is that nothing happens — so
        // this waits for the machine to have had a fair chance to be wrong: the
        // same poll the other cases use, spent expecting a second line that
        // should never arrive.
        var second = WaitForLines(2, TimeSpan.FromSeconds(5));

        Assert.False(second, "a second `open` without -n started a second instance");
        Assert.Single(File.ReadAllLines(ArgvLog));
    }

    // ---- the probe ---------------------------------------------------------

    // A minimal .app: an Info.plist naming the executable, and a shell script
    // that appends its own argv to a file. The bundle id is unique per test
    // instance because LaunchServices tracks "is it running" by id, and two of
    // these running concurrently under one id would make the -n case above see
    // another test's process.
    private void StageProbe(bool linger = false)
    {
        var macOs = Path.Combine(Bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        File.WriteAllText(ArgvLog, "");

        var id = "com.example.cbprobe." + Path.GetFileName(_root);
        File.WriteAllText(Path.Combine(Bundle, "Contents", "Info.plist"),
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
             <plist version="1.0"><dict>
             <key>CFBundleExecutable</key><string>probe</string>
             <key>CFBundleIdentifier</key><string>{id}</string>
             <key>CFBundleName</key><string>Probe</string>
             <key>CFBundlePackageType</key><string>APPL</string>
             <key>CFBundleURLTypes</key><array><dict>
               <key>CFBundleURLName</key><string>probe</string>
               <key>CFBundleURLSchemes</key><array><string>{Scheme}</string></array>
             </dict></array>
             </dict></plist>
             """);

        // One line per launch, one bracketed token per argument, so a split
        // argument is visible as two tokens rather than having to be inferred.
        var script = $"""
                      #!/bin/sh
                      printf 'ARGV' >> '{ArgvLog}'
                      for a in "$@"; do printf '\t%s' "$a" >> '{ArgvLog}'; done
                      printf '\n' >> '{ArgvLog}'
                      """;

        // Long enough for the second launch above to find it running, short
        // enough that an abandoned probe cleans itself up if Dispose never
        // gets to pkill it.
        if (linger) script += "\nsleep 30\n";

        var executable = Path.Combine(macOs, "probe");
        File.WriteAllText(executable, script);
        File.SetUnixFileMode(executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // Registering is what lets LaunchServices answer "is this bundle
        // running", which the -n case depends on. `open -a <path>` addresses
        // the bundle by path and does not need it, so a failure here is not
        // worth failing the test over.
        Run("/System/Library/Frameworks/CoreServices.framework/Frameworks/"
            + "LaunchServices.framework/Support/lsregister", "-f", Bundle);
    }

    private void Launch(IEnumerable<string> arguments)
    {
        Assert.True(Run("/usr/bin/open", arguments.ToArray()),
            "open(1) refused the launch");
    }

    // The argv of the first launch, split back into tokens.
    private string[] WaitForArgv()
    {
        Assert.True(WaitForLines(1, TimeSpan.FromSeconds(20)),
            "the probe never ran, or never wrote its argv");

        var line = File.ReadAllLines(ArgvLog)[0];
        return line.Split('\t').Skip(1).ToArray();
    }

    // Polls rather than sleeps. A fixed sleep is either a flake or a tax, and
    // this suite already has to survive being reordered by a Release run.
    private bool WaitForLines(int count, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.ReadAllLines(ArgvLog).Length >= count) return true;
            }
            catch (IOException)
            {
                // Mid-append. Try again.
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static bool Run(string executable, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(20_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
