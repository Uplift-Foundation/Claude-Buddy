using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ClaudeBuddy
{
    // What the daemon says a background session is *doing*, as opposed to
    // whether it is worth an orb at all.
    //
    // Five answers rather than a bool for the same reason SessionManager's
    // ScanVerdict is an enum: the two "no" answers below are not the same fact.
    // NotAJob is something known about the session — the listing was read and
    // this session is not in it. Unknown is something known about the *CLI* —
    // there was no listing to read. Only the first may change what an orb looks
    // like, and collapsing them is how a briefly unavailable `claude` would dim
    // fifteen orbs at once.
    internal enum JobPhase
    {
        // Read a listing, and this session was not on it: an interactive
        // session, a subagent, or a status file that outlived its session.
        NotAJob,

        // Mid-turn. Indistinguishable from Parked on disk, which is the whole
        // reason this enum exists.
        Working,

        // Between turns: the job's worker is alive and resumable, and nothing
        // is happening in it. The daemon's word for this is "blocked".
        Parked,

        // Finished for good. Its worker stays alive, so nothing about the
        // process says so — see the hygiene sweep in SessionManager.
        Done,

        // No listing. Fail-open: nothing downstream may act on this.
        Unknown
    }

    // What the daemon thinks of a background session, which its status file
    // can't tell you.
    //
    // A background agent's hook writes the same states every other session
    // writes — "idle" when it isn't mid-turn — so a job that has finished for
    // good is indistinguishable on disk from one sitting between turns. The
    // difference only exists in `claude agents`, which carries a separate
    // `state` ("done" once the work is over) alongside the status the hook
    // reports.
    //
    // That difference matters because a finished job has nothing to show: it
    // has no terminal, and `claude attach` on it prints "Attaching…" and exits
    // immediately, so an orb for one is a click that opens a window and closes
    // it again. Better not to have the orb.
    internal static class BackgroundJobs
    {
        // The scan runs every couple of seconds and this shells out, so it is
        // cached well above that. Long enough to be cheap, short enough that an
        // agent finishing doesn't leave a stale orb around for long.
        private const long CacheMs = 10_000;

        // In a holder rather than a field on this class, because it is only ever
        // taken by States(), which is excluded — and a static field initializer
        // does not run until some static field is touched, so this one reads as
        // an uncovered line in a class whose measured half never reaches it.
        // Excluding the holder is the honest description: it belongs to the
        // excluded code, not to the tested code.
        [ExcludeFromCodeCoverage]
        private static class Cache
        {
            internal static readonly object Gate = new();
        }
        private static Dictionary<string, string>? _states;
        private static long _stamp;

        // Whether this session is a background job that is still going.
        //
        // Asked of two shapes of session. The first records no pid at all — a
        // wider group than it sounds: a background agent has none, but neither
        // does a subagent, and neither does a session whose status file outlived
        // it. The second is a status file that lost SessionManager.Superseded's
        // pid tie-break — which happens for real to an Agent View background
        // session, since dispatching one shares its parent's pid rather than
        // getting one of its own. Both shapes write the same "idle" whether they
        // still have work to do or not, so the daemon's own list is the only
        // place to settle it.
        // Excluded from coverage: States() runs `claude agents --json` as a real
        // subprocess to ask the daemon for its listing. The answer, which is the
        // part with a decision in it, is IsLive below — separated for exactly this
        // reason and covered against hand-written listings in BackgroundJobsTests.
        [ExcludeFromCodeCoverage]
        public static bool IsLiveJob(string sessionId) => IsLive(States(), sessionId);

        // The answer, separated from fetching the listing so it can be tested
        // without a daemon to ask. `states` is what Parse returned — null for a
        // listing that could not be read at all.
        //
        // Absent from a listing that was read successfully means not a job at
        // all: a subagent, or a session that ended. "done" means it was one and
        // has finished. Neither has anything left to show, and an orb for
        // either is a click that can only fail.
        //
        // A listing that couldn't be read answers true, not false. This decides
        // whether to *hide* an orb, and no orb should vanish because the CLI
        // was briefly unavailable — the failure the user can't see is the one
        // worth being careful about.
        internal static bool IsLive(Dictionary<string, string>? states, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return true;
            if (states is null) return true;

            // The session id first, because that is the question being asked and
            // a listing answers it directly. The short job id is a fallback for a
            // row that named no session — see Parse.
            if (!states.TryGetValue(sessionId, out var state)
                && !states.TryGetValue(JobIdOf(sessionId), out state))
            {
                return false;
            }

            return !string.Equals(state, "done", StringComparison.OrdinalIgnoreCase);
        }

        // The same lookup as IsLive, answering the question IsLive throws away:
        // not "is this still worth an orb" but "what is it doing".
        //
        // That distinction is the whole of the parked-orb bug. A background
        // session between turns and one mid-turn both answer IsLive true and
        // both write "idle" into their status file, so fifteen parked workers
        // rendered exactly like fifteen agents at work — same fill, same
        // breathing, no badge, nothing to say the difference. The daemon knew
        // all along: "blocked" for the parked ones, "working" for the busy one.
        //
        // Same key fallback as IsLive (session id first, short job id for a row
        // that named no session) and the same fail-open posture, stated the same
        // way: a listing that could not be read answers Unknown, and every rule
        // built on this treats Unknown as "change nothing".
        //
        // A state this build has never heard of reads as Working, which is
        // exactly what IsLive already does with it — "done" is the only word
        // that removes an orb and "blocked" the only one that dims it, so a
        // daemon that grows a sixth state renders as it always did rather than
        // going quietly still.
        internal static JobPhase Phase(Dictionary<string, string>? states, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return JobPhase.Unknown;
            if (states is null) return JobPhase.Unknown;

            if (!states.TryGetValue(sessionId, out var state)
                && !states.TryGetValue(JobIdOf(sessionId), out state))
            {
                return JobPhase.NotAJob;
            }

            if (Is(state, "working")) return JobPhase.Working;
            if (Is(state, "blocked")) return JobPhase.Parked;
            if (Is(state, "done")) return JobPhase.Done;

            return JobPhase.Working;
        }

        private static bool Is(string? state, string word) =>
            string.Equals(state, word, StringComparison.OrdinalIgnoreCase);

        // One listing, for one scan.
        //
        // States() is cached for ten seconds and the scan runs every two, so
        // asking it once per rule is asking a question that can change its
        // answer halfway through a pass: the cache can expire between the
        // superseded check and the reachability one, and a session can be a
        // live job for the first and not for the second. That was survivable
        // while every consumer wanted the same bool. It stops being survivable
        // once the same listing also decides whether an orb is dimmed, because
        // then a single pass can both keep an orb and describe it wrongly.
        //
        // Excluded from coverage for the reason States() is: this is the process
        // launch and the clock, with no decision in it. What is decided with the
        // answer is IsLive and Phase above, both covered against hand-written
        // listings in BackgroundJobsTests and JobPhaseTests.
        [ExcludeFromCodeCoverage]
        internal static Dictionary<string, string>? SnapshotForScan() => States();

        // Excluded from coverage: shells out to the `claude` CLI, and the cache
        // it wraps is keyed on Environment.TickCount64. What it decides with the
        // answer is IsLive above, which is tested; what it decides about the
        // *listing* is Parse below, which is also tested. This is the process
        // launch and the clock, and nothing else.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? States()
        {
            lock (Cache.Gate)
            {
                if (_states is not null && Environment.TickCount64 - _stamp < CacheMs)
                {
                    return _states;
                }
            }

            var fresh = Read();

            lock (Cache.Gate)
            {
                // A failed read doesn't clear a good answer: the listing going
                // missing for one tick is not evidence that anything finished.
                if (fresh is not null)
                {
                    _states = fresh;
                    _stamp = Environment.TickCount64;
                }
                return _states;
            }
        }

        // Every account's listing, merged into one.
        //
        // The daemon is **per Claude Code config directory**, and so is
        // `claude agents`. One invocation therefore answers for one account and
        // says nothing whatever about the others — not "no jobs", but nothing,
        // which reads here as a session absent from a listing that was read
        // successfully, which is to say "not a job at all".
        //
        // That is the whole of the misread-background-job bug. A machine with a
        // second account (`CLAUDE_CONFIG_DIR=~/.claude-board`, the same
        // arrangement ClaudeCodeProfileDirs already exists to describe) runs two
        // daemons, and this asked only the first. Every background job under the
        // second was classified NotAJob, so SessionPresence.ShapeOf fell through
        // to Terminal and the chat panel told the user to reply in the terminal
        // — for a job the daemon runs precisely so that no terminal has to hold
        // it. Observed live: job `e4f5c5e4` ("makayla-case"), `kind: background`,
        // `state: done` under ~/.claude-board and absent from the default
        // account's listing altogether, while its process was alive the whole
        // time in a pty belonging to ~/.claude-board's own daemon.
        //
        // The default account is read exactly as it was before — with no
        // CLAUDE_CONFIG_DIR of this app's own invention — so this can only add
        // rows to the answer it already gave. Which account this app itself runs
        // under stays whatever the environment says it is; forcing ~/.claude
        // here would quietly re-point the read for anyone running the whole app
        // under a non-default config directory.
        //
        // Excluded from coverage: the loop is process launches. What it decides
        // with the answers is Merge and ExtraAccountDirs below, both pure and
        // both covered.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? Read()
        {
            // Invoked directly, not through a shell. The obvious way to reach a
            // binary that isn't on this app's PATH is to ask the user's shell to
            // find it, and that quietly doesn't work: `zsh -lc` reads .zshenv,
            // .zprofile and .zlogin, but *not* .zshrc, which is only for
            // interactive shells — and .zshrc is where a PATH addition for
            // ~/.local/bin normally lives. So the lookup failed with "command
            // not found" for exactly the launch this was written to survive, and
            // because a failed read is treated as "don't hide anything", every
            // finished session and every subagent got an orb again.
            var claude = ClaudeBinary.Path;
            if (claude is null) return null;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // null first: this app's own environment, which is the account it has
            // always read and the only one nearly every machine has.
            var merged = ReadOne(claude, configDir: null);

            foreach (var dir in ExtraAccountDirs(home, ClaudeBuddySettings.ClaudeCodeProfileDirs))
            {
                merged = Merge(merged, ReadOne(claude, dir));
            }

            return merged;
        }

        // The extra accounts to ask, beyond whichever one this app itself runs
        // under.
        //
        // The same list TranscriptReader and the hook installer already walk —
        // ~/.claude plus ClaudeCodeProfileDirs — with ~/.claude itself held out,
        // because Read has already asked it and asking again would be a second
        // subprocess for an answer already in hand. Held out by *path* rather
        // than by name, so a list naming ".claude" explicitly — which the
        // settings UI permits — doesn't double the work.
        //
        // Blank entries are skipped rather than resolving to $HOME, which is not
        // a config directory and whose listing would be some third account's or
        // nobody's.
        internal static List<string> ExtraAccountDirs(string home, IReadOnlyList<string> extras)
        {
            // OrdinalIgnoreCase for the reason OrbPositions is keyed that way:
            // Windows paths are, and one account reached under two
            // capitalizations is still one account.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(home, ".claude")
            };

            var dirs = new List<string>();

            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;

                var full = Path.Combine(home, extra.Trim());
                if (seen.Add(full)) dirs.Add(full);
            }

            return dirs;
        }

        // Two accounts' listings as one, or nothing at all.
        //
        // A null on either side collapses the whole answer to null, and that is
        // the point rather than an oversight. null here means "there was no
        // listing to read", which every rule downstream treats as "change
        // nothing" — orbs stay, phases stay Unknown. A *partial* listing would
        // instead be a confident answer about sessions nobody managed to ask
        // about: the account that answered would be classified correctly, and
        // the account that didn't would have every one of its jobs read as
        // not-a-job — precisely the misclassification this change exists to
        // remove. Better to know nothing for one tick than to be wrong about
        // half the machine.
        //
        // First writer wins on a key collision, which puts the account read
        // first — this app's own — ahead of the rest. Two accounts cannot share
        // a session id, so the only reachable collision is between two short job
        // ids, eight hex characters each, from different daemons. Nothing here
        // could settle such a tie on the merits; preferring the nearer account
        // at least makes it the same tie every time.
        internal static Dictionary<string, string>? Merge(
            Dictionary<string, string>? first, Dictionary<string, string>? second)
        {
            if (first is null || second is null) return null;

            var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
            foreach (var pair in second) merged.TryAdd(pair.Key, pair.Value);

            return merged;
        }

        // One account's listing.
        //
        // configDir null means "leave the environment alone", which is how the
        // default account is read and why this change cannot move it.
        //
        // Both pipes are drained before waiting, which the single-account
        // version did not do — it called ReadToEnd() and then WaitForExit(5000),
        // and a blocking read makes that timeout unreachable while an undrained
        // stderr can deadlock a chatty child. That was survivable at one launch
        // per scan and stops being survivable at one per account, so this now
        // does what AgentRoster.Read already does, for the reasons stated there.
        //
        // Excluded from coverage: starts the `claude` CLI as a real subprocess.
        // The JSON it prints is parsed by Parse below, which is tested against
        // hand-written listings, so what is excluded here is the launch, its
        // timeout, and the kill for a CLI that never answers.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? ReadOne(string claude, string? configDir)
        {
            try
            {
                // --json is documented as not needing a tty, which is what
                // makes it usable from a GUI app at all.
                var psi = new ProcessStartInfo(claude)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("agents");
                psi.ArgumentList.Add("--json");

                if (configDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

                using var process = Process.Start(psi);
                if (process is null) return null;

                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }

                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                if (process.ExitCode != 0) return null;

                return Parse(stdout);
            }
            catch
            {
                return null;
            }
        }

        // `claude agents --json` turned into the states IsLive asks about, keyed
        // by what the caller will name the session as.
        //
        // A background row carries both an `id` — the short job id, which is
        // also the name of the job's directory under ~/.claude/jobs — and a
        // `sessionId`, the full uuid of the session running it. The first
        // version of this keyed the map on the short id and derived the same
        // string from the session id it was asked about, on the assumption that
        // one is the first segment of the other.
        //
        // It usually is, which is what let the assumption survive: a job's id is
        // the first segment of the session id it *started* with. But a job
        // outlives that session — resume it, or let it compact, and it keeps its
        // original id while the work moves on to a session with a new uuid.
        // Observed on a real machine: job `5f6960b2` running session
        // `53bd5d2c-…`, working, with the derived lookup asking about `53bd5d2c`
        // and finding nothing. Absent reads as "not a job at all", so a
        // background session that was busy working had its orb dropped on every
        // scan, and the same miss let SessionManager.Superseded call it stale.
        //
        // So the map is keyed by the session id the row states, rather than by
        // anything derived. The short id is stored only for a row that named no
        // session — an older CLI, or a job the daemon knows about before its
        // session exists — which is what keeps the fallback in IsLive worth
        // having. Storing it as well as the session id would undo the fix by the
        // back door: the *earlier* session of a resumed job would match its own
        // job's row and keep an orb for a conversation that has moved on.
        internal static Dictionary<string, string>? Parse(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                // Interactive sessions carry no id at all — only background
                // ones are jobs — so they simply don't appear here, and
                // nothing downstream ever asks about them.
                if (Text(entry, "id") is not { Length: > 0 } jobId) continue;

                var state = Text(entry, "state") ?? "";

                map[Text(entry, "sessionId") is { Length: > 0 } sessionId ? sessionId : jobId] = state;

                // A row whose current session is this app's own remote-control
                // relay counts for its short id as well. The rule above — never
                // store the short id beside a named session — exists because a
                // resumed job's earlier sessions are conversations the work has
                // moved on from, and matching them would resurrect their orbs.
                // A relay is not that: it is a viewport this app itself opened
                // over the job (claude.ai remote control resumes the job into
                // the relay's session, so the row re-points at it), the scan
                // suppresses the relay's own file as plumbing (IsOwnRelayCwd,
                // same test), and the original conversation is still live and
                // still writing its status file. Without this, connecting
                // remote control to a background job made its orb vanish: the
                // job's one row named a session the scan hides, and the session
                // the user was actually talking to missed the lookup and read
                // as not-a-job. Observed live — job aff9cfe4 re-pointed at
                // relay session a16ff9fb, and the chat being steered through
                // that relay lost its orb mid-conversation.
                //
                // The row's cwd is the relay's by construction (RelayCwd runs
                // every relay from a directory named after itself), so the test
                // is the same pure string check the scan already keys on.
                if (RemoteControlBridge.IsOwnRelayCwd(Text(entry, "cwd")))
                {
                    map.TryAdd(jobId, state);
                }
            }

            return map;
        }

        private static string? Text(JsonElement entry, string name) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // The short form the daemon uses, which is the first segment of the
        // session id a job started with. Only reached for a row that named no
        // session of its own; split rather than a fixed width so an id that
        // isn't a uuid degrades to itself.
        // internal: AgentTeamViewer carries its own copy of this rule, and the
        // two are used together — one decides which pane to reuse and the other
        // whether the job is still alive — so a test asserts they agree. A
        // session that resolved to two different job ids would be adopted into a
        // pane and then have its orb hidden.
        internal static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }
    }
}
