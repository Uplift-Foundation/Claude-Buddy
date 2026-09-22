using System.Diagnostics;
using Xunit;

namespace ClaudeBuddy.Tests
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

        // The end-to-end shape, with this process standing in for the husk: its
        // own children are not a daemon, so the real table answers "nothing
        // underneath" — which is the answer that has to keep working, since it
        // is what every ordinary session gets.
        [UnixFact]
        public void ThisProcessHasNoDaemonUnderneathItInTheRealTable()
        {
            var rows = SessionDependents.ParsePs(RunPs());

            Assert.Equal(
                SessionDependents.Nothing,
                SessionDependents.Inspect(rows, Environment.ProcessId));
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
