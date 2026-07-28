using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // Which Claude Desktop instances are running right now, and which profile
    // directory each one was launched against — the Windows counterpart to
    // MacOSProcessScan, producing the same ClaudeInstance shape so
    // ClaudeDesktopManager.MapInstances needs no per-platform branch.
    //
    // claude.exe is also the name of the Claude Code CLI (installed under
    // %USERPROFILE%\.local\bin), so matching on process name alone would
    // report CLI invocations as Desktop instances. The packaged app's
    // executable lives under Program Files\WindowsApps; the CLI does not.
    //
    // Only the main/browser process's command line reflects how the instance
    // was launched. Electron passes --user-data-dir=<dir> down to every child
    // process (crashpad-handler, gpu-process, renderer, utility) *even when
    // the main process resolved that path itself* rather than being given the
    // flag — verified against a live Default-profile instance, whose main
    // process command line carries no arguments at all while its children all
    // carry an explicit --user-data-dir pointing at the same resolved default
    // directory. So "no --user-data-dir" only means Default when read off the
    // main process; every child process would misreport Default as a profile
    // match on its own directory. The main process is identified by having no
    // --type=... argument, which every child process carries and the main
    // process never does.
    [SupportedOSPlatform("windows")]
    internal static class WindowsProcessScan
    {
        private const string PackagedPathMarker = @"\WindowsApps\";

        private static readonly Regex UserDataDirArg =
            new(@"--user-data-dir=(?:""([^""]*)""|(\S+))", RegexOptions.Compiled);

        // The WMI query below (CommandLine, ExecutablePath) is the expensive
        // half of this scan. A plain process-name snapshot is cheap, so it
        // gates the WMI query: re-run that only when the set of claude.exe
        // pids has actually changed since the last poll.
        private static readonly object Gate = new();
        private static int[] _lastPids = Array.Empty<int>();
        private static IReadOnlyList<ClaudeInstance> _lastResult = Array.Empty<ClaudeInstance>();

        public static IReadOnlyList<ClaudeInstance> Scan()
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<ClaudeInstance>();

            try
            {
                var pids = CurrentPids();

                lock (Gate)
                {
                    if (pids.AsSpan().SequenceEqual(_lastPids)) return _lastResult;
                }

                var result = ScanCore();

                lock (Gate)
                {
                    _lastPids = pids;
                    _lastResult = result;
                }

                return result;
            }
            catch
            {
                // A scan that fails reads as "nothing running", which degrades
                // to offering Launch — never to launching a second instance
                // against a live directory, because the launch path re-checks.
                return Array.Empty<ClaudeInstance>();
            }
        }

        private static int[] CurrentPids()
        {
            var processes = Process.GetProcessesByName("claude");
            try
            {
                var pids = new int[processes.Length];
                for (var i = 0; i < processes.Length; i++) pids[i] = processes[i].Id;
                Array.Sort(pids);
                return pids;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        private static IReadOnlyList<ClaudeInstance> ScanCore()
        {
            var results = new List<ClaudeInstance>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='claude.exe'");
            using var matches = searcher.Get();

            foreach (ManagementBaseObject match in matches)
            {
                using var process = match;

                var path = process["ExecutablePath"] as string;
                if (string.IsNullOrEmpty(path)
                    || path.IndexOf(PackagedPathMarker, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var commandLine = process["CommandLine"] as string ?? "";
                if (commandLine.Contains("--type=", StringComparison.Ordinal)) continue; // a child process, not the main one

                var pid = Convert.ToInt32(process["ProcessId"]);
                var argMatch = UserDataDirArg.Match(commandLine);
                string? userDataDir = argMatch.Success
                    ? (argMatch.Groups[1].Success ? argMatch.Groups[1].Value : argMatch.Groups[2].Value)
                    : null;

                results.Add(new ClaudeInstance(pid, userDataDir is { Length: > 0 } ? userDataDir : null));
            }

            return results;
        }
    }
}
