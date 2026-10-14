using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // Where a click on a Claude Desktop-hosted session's orb goes: to the Claude
    // Desktop window that is running it.
    //
    // **The hook does not record the host app's pid, and that is deliberate (CB-265).**
    // A pid written into a status file is a fact about one moment, and Desktop is
    // restarted, updated and (for profiles) cloned into other bundles often enough
    // that the answer must be fresh at click time. So the walk happens here, over
    // the same process table SessionDependents already reads: from the session's
    // own `claude` up through its parents until something looks like the Electron
    // main process.
    //
    // The shape that walk is built from was measured on a real Mac (2026-10-14),
    // not recalled: `claude` (8251) <- `Contents/Helpers/disclaimer --pgroup --`
    // (8250) <- `<...>/Claude.app/Contents/MacOS/Claude` (4392) <- launchd (1).
    // The disclaimer in the middle is why "the parent" is never the answer, and
    // the bundle can be a profile's cloned copy under Application Support — whose
    // path contains spaces, so nothing here splits a command on whitespace.
    internal static class DesktopSessionFocus
    {
        // The most ancestors examined. Real chains are two or three deep; the cap
        // exists so a malformed table cannot make a click walk forever, and is
        // generous enough that no real launcher layering comes near it.
        internal const int MaxHops = 8;

        // The same suffix MacOSProcessScan treats as "the Electron main
        // executable" — kept literal here rather than shared because that one is
        // private to a scan this file must not change, and the two are pinned
        // against each other by a test.
        private const string MacMainSuffix = "/Claude.app/Contents/MacOS/Claude";

        private const string WindowsAppsMarker = @"\WindowsApps\";

        // The first ancestor of sessionPid (starting *above* it) that
        // isHostMain accepts, or 0. Zero for every way of not finding one: no
        // pid, a pid absent from the table, a chain that ends, a cycle, or
        // running out of hops — the caller treats them all as "ask the
        // fallback", so they do not need telling apart.
        internal static int HostOf(
            IReadOnlyList<SessionDependents.ProcessRow> table, int sessionPid, Func<string, bool> isHostMain)
        {
            if (sessionPid <= 0) return 0;

            var byPid = new Dictionary<int, SessionDependents.ProcessRow>();
            foreach (var row in table) byPid[row.Pid] = row;

            var visited = new HashSet<int> { sessionPid };
            var current = sessionPid;

            for (var hop = 0; hop < MaxHops; hop++)
            {
                if (!byPid.TryGetValue(current, out var child)) return 0;

                var parent = child.ParentPid;
                if (parent <= 0 || !visited.Add(parent)) return 0;
                if (!byPid.TryGetValue(parent, out var ancestor)) return 0;

                if (isHostMain(ancestor.Command)) return parent;

                current = parent;
            }

            return 0;
        }

        // Whether a process command line is Claude Desktop's *main* process, as
        // opposed to a helper or the disclaimer that launches `claude`.
        //
        // macOS: the executable path ends in the main-exe suffix, followed by
        // the end of the line or a space (arguments). Anywhere else in the line
        // does not count, and a longer name sharing the prefix does not either.
        // Windows: the packaged (WindowsApps) install, minus Electron's child
        // processes — the rule WindowsProcessScan.MapRow applies.
        internal static bool LooksLikeDesktopMain(string command, bool onWindows)
        {
            if (string.IsNullOrEmpty(command)) return false;

            if (onWindows)
            {
                // The marker has to be in the executable itself, not merely
                // somewhere on the line: a launcher or shell above the session
                // whose *arguments* name a WindowsApps path would otherwise be
                // taken for the host and activated in its place.
                return ExecutableOf(command).Contains(WindowsAppsMarker, StringComparison.OrdinalIgnoreCase)
                    && !command.Contains("--type=", StringComparison.Ordinal);
            }

            return MacMainEndsAt(command);
        }

        // The executable part of a Windows command line: the quoted first
        // token when it is quoted, which Win32_Process gives for anything under
        // Program Files; otherwise everything up to the first ".exe", since an
        // unquoted path can still contain spaces; failing both, the first word.
        internal static string ExecutableOf(string command)
        {
            var line = command.TrimStart();
            if (line.StartsWith('"'))
            {
                var close = line.IndexOf('"', 1);
                return close < 0 ? line[1..] : line[1..close];
            }

            var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0) return line[..(exe + 4)];

            var space = line.IndexOf(' ');
            return space < 0 ? line : line[..space];
        }

        private static bool MacMainEndsAt(string command)
        {
            var from = 0;
            while (true)
            {
                var at = command.IndexOf(MacMainSuffix, from, StringComparison.Ordinal);
                if (at < 0) return false;

                var end = at + MacMainSuffix.Length;
                if (end == command.Length || command[end] == ' ') return true;

                from = at + 1;
            }
        }

        // The walk found nothing: if exactly one Claude Desktop is running, it is
        // the answer. Never a guess between two — raising the wrong profile's
        // window onto a session it does not own is worse than doing nothing.
        internal static int FallbackPid(IReadOnlyList<ClaudeInstance> running) =>
            running.Count == 1 ? running[0].Pid : 0;

        // Bring the Claude Desktop window hosting this session forward. Returns
        // whether anything was activated. Every effect is a seam so a test drives
        // the whole decision without touching a process; the defaults are the OS
        // calls and carry [ExcludeFromCodeCoverage] on their own methods.
        //
        // acknowledge is called only when something was activated, for the same
        // reason TerminalFocuser's is: it tells the orb the click did something,
        // and a click that did nothing must not claim otherwise.
        internal static bool Focus(
            SessionStatus status,
            Action? acknowledge,
            Func<IReadOnlyList<SessionDependents.ProcessRow>?>? table = null,
            Func<IReadOnlyList<ClaudeInstance>>? instances = null,
            Action<int>? activate = null,
            bool? onWindows = null)
        {
            var windows = onWindows ?? OperatingSystem.IsWindows();
            var rows = (table ?? DefaultTable)();

            var pid = rows is null
                ? 0
                : HostOf(rows, status.SessionPid, c => LooksLikeDesktopMain(c, windows));

            if (pid <= 0) pid = FallbackPid((instances ?? DefaultInstances)());
            if (pid <= 0) return false;

            (activate ?? DefaultActivate)(pid);
            acknowledge?.Invoke();
            return true;
        }

        // Excluded from coverage: reads the live process table.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<SessionDependents.ProcessRow>? DefaultTable() =>
            SessionDependents.CurrentTable();

        // Excluded from coverage: a walk of every process on the machine running
        // the tests. The decision made with the answer is FallbackPid.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<ClaudeInstance> DefaultInstances() =>
            OperatingSystem.IsWindows() ? WindowsProcessScan.Scan() : MacOSProcessScan.Scan();

        // Excluded from coverage: activates another application's window.
        [ExcludeFromCodeCoverage]
        private static void DefaultActivate(int pid) => ClaudeDesktopManager.Focus(pid);
    }
}
