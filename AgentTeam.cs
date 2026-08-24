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
        public static Membership Of(int pid)
        {
            if (pid <= 0) return None;

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
                Sanitize(args.GetValueOrDefault(ColorFlag)),
                SanitizeName(args.GetValueOrDefault(NameFlag)));

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

        // Excluded from coverage: platform dispatch over two OS calls and nothing
        // else. On macOS this runs `ps` as a real subprocess through
        // MacOSProcessScan; on Windows it queries WMI for the command line. The
        // third arm exists only so a platform that is neither returns an empty
        // map rather than throwing, and it is unreachable from either CI runner
        // by construction.
        //
        // Everything this hands back is decided elsewhere and covered there —
        // Sanitize below, and the cache above it. What is left here is "which of
        // the two OS calls do I make", which cannot be asked without making one.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string> Read(int pid)
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacOSProcessScan.ArgumentValues(pid, ParentSessionFlag, ColorFlag, NameFlag);
            }

            if (OperatingSystem.IsWindows()) return WindowsArguments(pid);

            return new Dictionary<string, string>(StringComparer.Ordinal);
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
        // ask Win32_Process for another process's command line, which has no
        // equivalent on the macOS runner and no seam on the Windows one.
        [ExcludeFromCodeCoverage]
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

                    foreach (var flag in new[] { ParentSessionFlag, ColorFlag, NameFlag })
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
