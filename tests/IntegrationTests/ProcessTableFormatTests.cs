using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests
{
    // SessionDependents.ParsePs against the bytes `ps` actually prints, rather
    // than against a fixture of them.
    //
    // In IntegrationTests for the reason the hook-script suites are: this is a
    // seam with a format nobody here owns, and the two levels fail differently.
    // The unit suite's fixtures catch the parser getting a column wrong; only a
    // real invocation catches the *exchange* being wrong — a flag `ps` does not
    // accept on this platform, a header row that suppressing the headers was
    // supposed to remove, a column order that is not the one asked for. Every
    // one of those returns a clean empty list, which reads exactly like a
    // machine with nothing running on it, which is the answer that makes the
    // whole guard silently do nothing.
    //
    // Unix only, and deliberately not skipped-into-nothing on Windows: the
    // Windows arm is a WMI query rather than a `ps`, and it has no equivalent
    // here. That arm is the half of CB-26 that is inferred rather than
    // reproduced, and a test that pretended to cover it would be the worse
    // outcome.
    public class ProcessTableFormatTests
    {
        [UnixFact]
        public void RealPsOutputParsesIntoRowsThatIncludeThisProcess()
        {
            var listing = RunPs();
            var rows = SessionDependents.ParsePs(listing);

            // A machine always has more than a handful of processes, so a
            // near-empty answer means the exchange failed rather than that the
            // table is small.
            Assert.True(rows.Count > 5, $"only {rows.Count} rows parsed from {listing.Length} bytes");

            var self = rows.Find(row => row.Pid == Environment.ProcessId);

            Assert.NotEqual(0, self.Pid);
            Assert.NotEqual(0, self.ParentPid);

            // The column that a naive split would truncate, and the one every
            // rule in SessionDependents reads. `dotnet test` runs the suite
            // under a testhost, so the command line is long and full of spaces.
            Assert.NotEqual("", self.Command);
        }

        // The end-to-end shape, on a husk this test owns: an ordinary process
        // with nothing underneath it, read back out of the real table, answers
        // "nothing underneath" — the answer every ordinary session gets.
        //
        // **Not this process (CB-236).** It used to ask about the testhost
        // itself, and the testhost is shared: every class running in parallel
        // hangs its own children off it. HookPidWalkShTests stages a process
        // titled `claude … daemon run` there on purpose, and while it was alive
        // the real table quite correctly said this pid had a daemon below it —
        // a flake about which test happened to be running alongside, forced
        // deterministically by holding such a child open around the old body.
        // A husk spawned here has only the children this test gives it.
        [UnixFact]
        public void AnOrdinaryProcessHasNoDaemonUnderneathItInTheRealTable()
        {
            using var husk = Husk("sleep 30; :");

            var rows = SessionDependents.ParsePs(RunPs());

            Assert.Contains(rows, row => row.Pid == husk.Pid);
            Assert.Equal(SessionDependents.Nothing, SessionDependents.Inspect(rows, husk.Pid));
        }

        // The control the case above never had: the same real exchange does
        // see a daemon when one is there. Without it, an exchange that silently
        // parsed nothing — the failure this file exists to catch — would pass
        // "nothing underneath" as easily as a working one.
        [UnixFact]
        public void AStagedDaemonUnderAHuskIsSeenInTheRealTable()
        {
            var ready = Path.Combine(Path.GetTempPath(), "cb-daemon-" + Guid.NewGuid().ToString("N"));

            // `exec -a` titles the child the way the real binary appears; the
            // trailing `; :` stops bash exec'ing sleep in its place and losing
            // the title; and the pid file is written by the titled process
            // itself, so it exists only once that row is in the table.
            using var husk = Husk(
                "(exec -a claude /bin/bash -c 'echo $$ > \"$1\"; sleep 30; :' _ \"$1\" daemon run) & wait",
                ready);

            try
            {
                WaitForFile(ready);

                var verdict = SessionDependents.Inspect(SessionDependents.ParsePs(RunPs()), husk.Pid);

                Assert.True(verdict.DaemonBelow, "the real table did not show the staged daemon under its husk");
            }
            finally
            {
                File.Delete(ready);
            }
        }

        // A bash child of this process, killed with its whole tree on dispose.
        private sealed class OwnedProcess(Process process) : IDisposable
        {
            internal int Pid => process.Id;

            public void Dispose()
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }

        private static OwnedProcess Husk(string script, string? arg = null)
        {
            var psi = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("_");
            if (arg is not null) psi.ArgumentList.Add(arg);

            var process = Process.Start(psi);
            Assert.NotNull(process);
            return new OwnedProcess(process!);
        }

        // Waits on the signal itself, with a backstop only for a staged process
        // that never starts — not a tolerance the passing path depends on.
        private static void WaitForFile(string path)
        {
            var backstop = Stopwatch.StartNew();
            while (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                Assert.True(backstop.Elapsed < TimeSpan.FromMinutes(1), "the staged daemon never wrote its pid file");
                Thread.Sleep(20);
            }
        }

        // The columns SessionDependents.Snapshot asks for, spelled the same way
        // — if this argument list drifts from the one in the app, this file goes
        // on passing while the app reads nothing.
        private static string RunPs()
        {
            var psi = new ProcessStartInfo("/bin/ps")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-eo");
            psi.ArgumentList.Add("pid=,ppid=,args=");

            using var process = Process.Start(psi);
            Assert.NotNull(process);

            var output = process!.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            Assert.True(process.WaitForExit(10_000));

            error.GetAwaiter().GetResult();
            Assert.Equal(0, process.ExitCode);

            return output.GetAwaiter().GetResult();
        }
    }
}
