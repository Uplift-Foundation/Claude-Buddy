using System.Diagnostics.CodeAnalysis;
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
        private const string NameFlag = "--agent-name";

        // What the app wants to know about a session that turns out to be an
        // agent-team member. All empty for everything else.
        //
        // Name is what the agent is called within its team — MenuUX, Narrative,
        // HitReactSpec. Every member of a team inherits the team's *session*
        // title, so without this every agent's orb showed the same letter and
        // the team read as four copies of one thing.
        internal readonly record struct Membership(string Lead, string Color, string Name);

        // "Not in a team", said with empty strings rather than nulls.
        //
        // `default(Membership)` would do the same job in every existing caller,
        // because all three of them guard with string.IsNullOrEmpty — but a
        // record struct's default leaves every field null, and this value is
        // assigned straight onto SessionStatus.Lead, where "no team" has been
        // the empty string since the field existed. A caller that compared
        // against "" instead, or a rule that asked whether a lead was *known*
        // rather than whether it was set, would be quietly wrong for exactly
        // the sessions that have no pid to ask about. Said out loud so the two
        // cannot drift.
        internal static readonly Membership None = new("", "", "");

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
        public static Membership Of(int pid) =>
            pid <= 0 ? None : OfAll(new[] { pid }, ReadMany)[pid];

        // The common question, for callers that don't care about the colour.
        public static string LeadOf(int pid) => Of(pid).Lead;

        // Every pid's answer at once, which is how the scan asks (CB-212).
        //
        // At once because of what one answer costs on Windows. Measured on the
        // Windows PC this was written on, 384 processes: a single
        // `Win32_Process WHERE ProcessId = N` query took 199 ms median, 356 ms
        // p95, 480 ms max warm (400 samples), and 257/521/712 ms for a
        // process's first one, which pays for COM and WMI setup too (20 fresh
        // processes). The cost is WMI walking the process table, not returning
        // the row: one query naming ten pids with OR measured the same as one
        // naming a single pid, 345 ms against 338 ms median over 20 rounds. So
        // a scan with eight sessions spent two to three seconds of its
        // background half re-reading them, one after another, each time their
        // entries aged out — and ScheduleScan skips every tick that lands
        // while a pass is in flight, so orbs stopped updating for that long
        // once a minute. Asked together, that is one query.
        //
        // pids that are not pids are left out of the answer rather than mapped
        // to None, for the reason Of never caches them.
        internal static IReadOnlyDictionary<int, Membership> OfAll(IEnumerable<int> pids) =>
            OfAll(pids, ReadMany);

        // The same, with the OS read handed over so the cache and batching
        // rules can be tested without an OS call — and without the WMI one in
        // particular, which the macOS runner cannot make.
        internal static IReadOnlyDictionary<int, Membership> OfAll(
            IEnumerable<int> pids,
            Func<IReadOnlyList<int>, IReadOnlyDictionary<int, Dictionary<string, string>>> readMany)
        {
            var answers = new Dictionary<int, Membership>();
            var misses = new List<int>();
            var now = Environment.TickCount64;

            lock (Gate)
            {
                foreach (var pid in pids)
                {
                    if (pid <= 0 || answers.ContainsKey(pid) || misses.Contains(pid)) continue;

                    if (Cache.TryGetValue(pid, out var cached) && now - cached.Stamp < CacheMs)
                    {
                        answers[pid] = cached.Value;
                    }
                    else
                    {
                        misses.Add(pid);
                    }
                }
            }

            if (misses.Count == 0) return answers;

            // Outside the lock: this is the slow part, and holding the gate
            // across it would make a second caller wait on a query that is not
            // about its pids.
            var read = readMany(misses);

            lock (Gate)
            {
                foreach (var pid in misses)
                {
                    // A pid the read has nothing for — gone, or not ours to
                    // query — is "no team known", cached as firmly as any other
                    // answer, exactly as a per-pid read with no row was.
                    var membership = read.TryGetValue(pid, out var args) ? MembershipFrom(args) : None;
                    answers[pid] = membership;
                    Cache[pid] = (membership, now);
                }

                // Sessions come and go all day; without this the map grows for
                // as long as the app runs.
                if (Cache.Count > 256) Prune(now);
            }

            return answers;
        }

        internal static Membership MembershipFrom(IReadOnlyDictionary<string, string> args) => new(
            Sanitize(args.GetValueOrDefault(ParentSessionFlag)),
            Sanitize(args.GetValueOrDefault(ColorFlag)),
            SanitizeName(args.GetValueOrDefault(NameFlag)));

        private static void Prune(long now)
        {
            foreach (var (pid, entry) in Cache.ToList())
            {
                if (now - entry.Stamp >= CacheMs) Cache.Remove(pid);
            }
        }

        // Excluded from coverage: platform dispatch over two OS calls and nothing
        // else. On macOS this asks the kernel for each pid's arguments through
        // MacOSProcessScan (sysctl KERN_PROCARGS2, in-process and cheap, so it
        // stays one call per pid exactly as it was); on Windows it queries WMI
        // for every pid at once. A platform that is neither gets an empty map
        // rather than an exception, and that arm is unreachable from either CI
        // runner by construction.
        //
        // Everything this hands back is decided elsewhere and covered there —
        // ArgumentsIn, WindowsQueries and Sanitize below, and the cache above.
        // What is left here is "which OS call do I make", which cannot be asked
        // without making one.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyDictionary<int, Dictionary<string, string>> ReadMany(IReadOnlyList<int> pids)
        {
            if (OperatingSystem.IsWindows()) return WindowsArguments(pids);

            var found = new Dictionary<int, Dictionary<string, string>>();
            if (!OperatingSystem.IsMacOS()) return found;

            foreach (var pid in pids)
            {
                found[pid] = MacOSProcessScan.ArgumentValues(pid, ParentSessionFlag, ColorFlag, NameFlag);
            }

            return found;
        }

        // The flags this class reads, out of one Windows command line. Windows
        // hands a process's arguments back as the single string it was started
        // with, so they are found by pattern rather than by position: the flag,
        // a space or `=`, and the value, quoted or not.
        internal static Dictionary<string, string> ArgumentsIn(string? command)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(command)) return found;

            foreach (var flag in new[] { ParentSessionFlag, ColorFlag, NameFlag })
            {
                var match = Regex.Match(command, Regex.Escape(flag) + @"[= ]""?([^""\s]+)");
                if (match.Success) found[flag] = match.Groups[1].Value;
            }

            return found;
        }

        // The WQL that asks for these pids' command lines, a bounded number of
        // pids per query. Bounded because a query's text has a length limit and
        // a machine with a few hundred sessions should not find it; 32 is far
        // above any real team and far below that limit. The pids are ints, so
        // nothing a process controls is spliced into the text.
        internal const int PidsPerQuery = 32;

        internal static IEnumerable<string> WindowsQueries(IReadOnlyList<int> pids)
        {
            for (var i = 0; i < pids.Count; i += PidsPerQuery)
            {
                yield return "SELECT ProcessId, CommandLine FROM Win32_Process WHERE "
                    + string.Join(" OR ", pids.Skip(i).Take(PidsPerQuery).Select(pid => $"ProcessId = {pid}"));
            }
        }

        // A session id or a colour name and nothing else. Neither is spliced
        // into a command or a path — they are only compared against other
        // session ids and looked up in a colour table — but they come from a
        // process this app doesn't own, so they get the same treatment as
        // anything else read off disk.
        internal static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return "";

            foreach (var c in value)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return "";
            }

            return value;
        }

        // Names are shown, not matched, so this keeps what it can rather than
        // rejecting the whole value: anything that isn't a letter, digit or
        // ordinary separator is dropped, and what's left is trimmed. A name
        // that survives as nothing is treated as no name at all.
        internal static string SanitizeName(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var kept = new string(value
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')
                .ToArray())
                .Trim();

            return kept.Length > 48 ? kept[..48].TrimEnd() : kept;
        }

        // Excluded from coverage: a WMI query. System.Management reaches COM to
        // ask Win32_Process for other processes' command lines, which has no
        // equivalent on the macOS runner and no seam on the Windows one. The
        // query text and the parsing are WindowsQueries and ArgumentsIn, both
        // covered; this is the round trip between them.
        //
        // Since CB-212 this is only ever reached from the scan's background
        // half (and from Of, which nothing on the scan's UI half calls), so it
        // can be as slow as WMI is without anybody waiting on it.
        [ExcludeFromCodeCoverage]
        [SupportedOSPlatform("windows")]
        private static Dictionary<int, Dictionary<string, string>> WindowsArguments(IReadOnlyList<int> pids)
        {
            var found = new Dictionary<int, Dictionary<string, string>>();

            foreach (var query in WindowsQueries(pids))
            {
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(query);

                    foreach (var row in searcher.Get())
                    {
                        using var process = (System.Management.ManagementObject)row;
                        var pid = Convert.ToInt32(process["ProcessId"]);
                        found[pid] = ArgumentsIn(process["CommandLine"] as string);
                    }
                }
                catch
                {
                    // No WMI, or processes this app can't query. Both mean "no
                    // team known", which is the same as not being in one.
                }
            }

            return found;
        }
    }
}
