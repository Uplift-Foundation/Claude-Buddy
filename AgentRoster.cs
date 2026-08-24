using System.Diagnostics;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Which of this machine's Claude Code sessions is the one another machine
    // knows by a given name.
    //
    // This exists to answer one question exactly, where getting it nearly right
    // would be worse than not answering. A peer row on the far side carries a
    // *name* — `job-hunter` — and nothing else identifying: no pid, no session
    // id, no machine (`ListAgents` was asked directly and said
    // `"machine":"unknown"`; see docs/remote-control-findings.md). Buddy's own
    // status files are keyed by session id and carry a Title that is whatever
    // /rename last set, which is frequently not that name — the names Claude
    // Code registers look derived from the working directory (`placement-41`,
    // `evidence`) unless someone changed them. So matching a peer name against
    // Title, or against the folder, is guesswork, and a wrong match here means
    // showing one session's private conversation on another session's panel.
    //
    // `claude agents --json` removes the guess. It is the same registry the peer
    // list is drawn from, it runs locally against the same profile, and it
    // prints the session id alongside the name — so name → session id → status
    // file is an exact join rather than a resemblance. Shape, captured from a
    // real run rather than remembered:
    //
    //     [
    //       {
    //         "pid": 77492,
    //         "cwd": "/Users/warrenthompson/Documents/GTD/Evidence",
    //         "kind": "interactive",
    //         "startedAt": 1787191603916,
    //         "sessionId": "bd79c1fb-a5a9-4691-90e3-45b927c44c4e",
    //         "name": "job-lawyer",
    //         "status": "idle"
    //       },
    //       …
    //     ]
    //
    // Parsing is split from running for the reason everything else in this app
    // is: the parser is pure and has a fixture, and the process call is the part
    // a headless test must not make.
    internal static class AgentRoster
    {
        // One registration, with only the fields anything here uses. `kind` and
        // `status` are deliberately not carried: this is an identity lookup, and
        // liveness is already answered better by the status file the session
        // itself wrote.
        internal readonly record struct Entry(string Name, string SessionId, int Pid);

        // Empty for anything unreadable, which is the same rule ParseAgents next
        // door follows: a registry we cannot read means no session can be
        // matched, and no match means the panel says it has no live view rather
        // than showing the wrong one.
        public static IReadOnlyList<Entry> ParseAgentsJson(string? json)
        {
            var found = new List<Entry>();
            if (string.IsNullOrWhiteSpace(json)) return found;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;

                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;

                    var name = Str(row, "name");
                    var sessionId = Str(row, "sessionId");

                    // Both halves are the point. A row missing either cannot
                    // complete the join it exists for, so it is dropped rather
                    // than half-kept.
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (string.IsNullOrWhiteSpace(sessionId)) continue;

                    var pid = 0;
                    if (row.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number)
                        p.TryGetInt32(out pid);

                    found.Add(new Entry(name!, sessionId!, pid));
                }
            }
            catch
            {
                // Not JSON, or not this shape. Nothing to match against.
            }

            return found;
        }

        // The one session registered under this name, or null.
        //
        // **Null when two sessions share the name**, which is the whole reason
        // this is a method rather than a dictionary lookup. Two sessions in one
        // account can absolutely carry one name — the same person working in two
        // checkouts of one repository gets it without trying — and there is
        // nothing in a peer row that distinguishes them. Picking either would be
        // right half the time, and the failure is silent and private: someone's
        // other conversation, mirrored onto the wrong panel. Refusing is the
        // only honest answer, and the caller turns it into "no live view".
        public static Entry? Resolve(IReadOnlyList<Entry> entries, string name)
        {
            Entry? only = null;

            foreach (var entry in entries)
            {
                if (!entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (only is not null) return null;

                only = entry;
            }

            return only;
        }

        // Asks this machine's Claude Code what it has registered, under a
        // specific profile.
        //
        // CLAUDE_CONFIG_DIR is set for the same reason RemoteControlBridge sets
        // it when launching a relay: the registry is per-account, and reading
        // the wrong account's would answer confidently about sessions this relay
        // cannot see.
        public static IReadOnlyList<Entry> Read(string profileDir, int timeoutMs = 5000)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return Array.Empty<Entry>();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configDir = Path.Combine(home, profileDir);

            try
            {
                var psi = new ProcessStartInfo(claude)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("agents");
                psi.ArgumentList.Add("--json");
                psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

                using var process = Process.Start(psi);
                if (process is null) return Array.Empty<Entry>();

                // Both pipes drained before waiting, same as
                // RemoteControlBridge.Run and for the same two reasons: a
                // blocking read makes the timeout unreachable, and an undrained
                // stderr can deadlock a chatty child.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return Array.Empty<Entry>();
                }

                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0 ? ParseAgentsJson(stdout) : Array.Empty<Entry>();
            }
            catch
            {
                return Array.Empty<Entry>();
            }
        }

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
