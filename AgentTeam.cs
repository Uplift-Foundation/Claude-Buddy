using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // Which session, if any, leads the team a given session belongs to.
    //
    // Every member of an agent team is spawned as its own `claude` process, and
    // Claude Code hands it the answer on its command line:
    //
    //   claude --agent-id CatAudioSourcing@session-6a6fcb43
    //          --agent-name CatAudioSourcing --team-name session-6a6fcb43
    //          --agent-color blue
    //          --parent-session-id 6a6fcb43-fa28-4894-9940-c1c6c9970e54 ...
    //
    // `--parent-session-id` is the lead's session id outright, which is what
    // TeamLinks needs, so the app reads it off the process it is already
    // tracking — SessionStatus.SessionPid, the same pid the liveness check uses.
    //
    // The first version of this asked the *hook* instead: read `teamName` out of
    // the member's transcript, then `leadSessionId` out of
    // ~/.claude/teams/<team>/config.json. That worked, but it only learned the
    // answer when a member next fired a hook — so an agent that had gone quiet,
    // or one already running when the hook was updated, kept a status file with
    // no team in it and sat there looking like an unrelated session. Found
    // exactly that way: two live agents in a team, no arrows, because neither
    // had run a tool since the hook changed. Reading the process has no such
    // window; it is true the moment the orb appears, and it needs no hook
    // update at all.
    //
    // These flags are Claude Code's internals rather than a documented
    // interface. If they change, the lookup returns nothing and every orb is
    // simply drawn the way it was before teams existed.
    internal static class AgentTeam
    {
        private const string ParentSessionFlag = "--parent-session-id";
        private const string ColorFlag = "--agent-color";

        // What the app wants to know about a session that turns out to be an
        // agent-team member. Both empty for everything else.
        internal readonly record struct Membership(string Lead, string Color);

        // A live process's arguments never change, so this is a cache with a
        // safety valve rather than a poll: re-read after a minute so a recycled
        // pid can't pin a wrong answer for the life of the app. Same reasoning,
        // and the same interval, as MacOSProcessScan's environment cache.
        private const long CacheMs = 60_000;

        private static readonly object Gate = new();
        private static readonly Dictionary<int, (Membership Value, long Stamp)> Cache = new();

        // An empty Lead means "not a team member", which is the answer for
        // almost every session and is cached just as firmly as a real one — the
        // point is to ask the kernel once per session, not once per scan.
        public static Membership Of(int pid)
        {
            if (pid <= 0) return default;

            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (Cache.TryGetValue(pid, out var cached) && now - cached.Stamp < CacheMs)
                {
                    return cached.Value;
                }
            }

            var args = Read(pid);
            var membership = new Membership(
                Sanitize(args.GetValueOrDefault(ParentSessionFlag)),
                Sanitize(args.GetValueOrDefault(ColorFlag)));

            lock (Gate)
            {
                Cache[pid] = (membership, now);

                // Sessions come and go all day; without this the map grows for
                // as long as the app runs.
                if (Cache.Count > 256) Prune(now);
            }

            return membership;
        }

        // The common question, for callers that don't care about the colour.
        public static string LeadOf(int pid) => Of(pid).Lead;

        private static void Prune(long now)
        {
            foreach (var (pid, entry) in Cache.ToList())
            {
                if (now - entry.Stamp >= CacheMs) Cache.Remove(pid);
            }
        }

        private static Dictionary<string, string> Read(int pid)
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacOSProcessScan.ArgumentValues(pid, ParentSessionFlag, ColorFlag);
            }

            if (OperatingSystem.IsWindows()) return WindowsArguments(pid);

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // A session id or a colour name and nothing else. Neither is spliced
        // into a command or a path — they are only compared against other
        // session ids and looked up in a colour table — but they come from a
        // process this app doesn't own, so they get the same treatment as
        // anything else read off disk.
        private static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return "";

            foreach (var c in value)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return "";
            }

            return value;
        }

        [SupportedOSPlatform("windows")]
        private static Dictionary<string, string> WindowsArguments(int pid)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");

                foreach (var row in searcher.Get())
                {
                    using var process = (System.Management.ManagementObject)row;
                    var command = process["CommandLine"] as string;
                    if (string.IsNullOrEmpty(command)) continue;

                    foreach (var flag in new[] { ParentSessionFlag, ColorFlag })
                    {
                        var match = Regex.Match(command,
                            Regex.Escape(flag) + @"[= ]""?([^""\s]+)");
                        if (match.Success) found[flag] = match.Groups[1].Value;
                    }
                }
            }
            catch
            {
                // No WMI, or a process this app can't query. Both mean "no team
                // known", which is the same as not being in one.
            }

            return found;
        }
    }
}
