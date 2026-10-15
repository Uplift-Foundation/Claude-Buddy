using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClaudeBuddy
{
    // What Claude Desktop remembers about one Code-tab chat, read back out of its
    // per-chat record (CB-266). Only the fields Orbweaver acts on; the record
    // carries more (cwd, title, lastActivityAt) and none of it is needed here.
    internal sealed record DesktopSessionRecord(
        string SessionId,
        string? CliSessionId,
        IReadOnlyList<string> BridgeSessionIds,
        bool IsArchived,
        bool RemoteControlAutoEligible,
        bool SteeredByRemoteClient);

    // The pure half of finding a Desktop chat's Remote Control bridge.
    //
    // Desktop writes one JSON record per chat at
    // <profile dir>/claude-code-sessions/<uuid>/<uuid>/local_<id>.json (measured
    // on a Mac, docs/claude-desktop-sessions-findings.md). `bridgeSessionIds`
    // there is the claude.ai session the chat is bridged to once Remote Control
    // is on for it, which is the id the orb's chat panel needs to send into.
    //
    // Everything that touches a disk is a parameter, the way
    // ClaudeConfigRoots takes `home`, so each rule has a case per outcome with
    // no profile on the machine. DesktopBridgeLookup below is the stateful
    // wrapper the scan holds.
    internal static class DesktopSessionRecords
    {
        internal const string RecordsFolder = "claude-code-sessions";

        // The shape Desktop gives its own id: "local_" and a uuid. An allow-list
        // rather than a deny-list because the id becomes part of a file name.
        // The hook already reduces it to [A-Za-z0-9_-]; this is the second lock,
        // and it also refuses "local_" alone, "..", and anything with a dot or
        // a separator in it.
        internal static bool IsWellFormedHostId(string? id)
        {
            const string prefix = "local_";
            if (string.IsNullOrEmpty(id)) return false;
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) return false;
            if (id.Length <= prefix.Length) return false;

            for (var i = prefix.Length; i < id.Length; i++)
            {
                var c = id[i];
                var ok = c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z'
                         || c is >= '0' and <= '9' || c == '-';
                if (!ok) return false;
            }

            return true;
        }

        internal static string FileNameFor(string hostId) => hostId + ".json";

        // Null for anything that is not a record: no text, text that is not
        // JSON, JSON that is not an object, or an object with no sessionId.
        // A field of the wrong type reads as absent rather than throwing — the
        // format is Desktop's, it can change, and a scan tick must not.
        internal static DesktopSessionRecord? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JsonDocument document;
            try { document = JsonDocument.Parse(json); }
            catch (JsonException) { return null; }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var sessionId = StringOf(root, "sessionId");
                if (string.IsNullOrEmpty(sessionId)) return null;

                var bridges = new List<string>();
                if (root.TryGetProperty("bridgeSessionIds", out var array)
                    && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                        {
                            bridges.Add(id);
                        }
                    }
                }

                return new DesktopSessionRecord(
                    sessionId,
                    StringOf(root, "cliSessionId"),
                    bridges,
                    BoolOf(root, "isArchived"),
                    BoolOf(root, "remoteControlAutoEligible"),
                    BoolOf(root, "steeredByRemoteClient"));
            }
        }

        private static string? StringOf(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool BoolOf(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        // The bridge to send into, or null. Three ways to be null that each
        // protect a send from going to the wrong place:
        //  - an archived chat is over, and its bridge with it;
        //  - a record whose cliSessionId is not the session asking belongs to a
        //    different Claude Code process (a resumed chat gets a new one), so
        //    its bridge reaches the wrong conversation;
        //  - no id that passes the one allow-list every write path shares.
        // The last well-formed id wins when there are several: reconnecting
        // appends, so the newest is last. Measured live chats carry exactly one;
        // the multi-id order is assumed.
        internal static string? BridgeIdOf(DesktopSessionRecord? record, string cliSessionId)
        {
            if (record is null || record.IsArchived) return null;
            if (!string.Equals(record.CliSessionId, cliSessionId, StringComparison.Ordinal)) return null;

            return record.BridgeSessionIds.LastOrDefault(ClaudeCloudRoster.IsWellFormedId);
        }

        // Where the record for a host id would be, across every profile: exactly
        // two directory levels (the two uuids) below the records folder, then the
        // file. Two levels and no deeper keeps this a handful of directory reads
        // per profile instead of a tree walk. An ill-formed id yields nothing, so
        // it can never reach Path.Combine.
        internal static IReadOnlyList<string> RecordPathsFor(
            IEnumerable<string> profileDirs,
            string hostId,
            Func<string, IEnumerable<string>> subdirectories,
            Func<string, bool> fileExists)
        {
            var found = new List<string>();
            if (!IsWellFormedHostId(hostId)) return found;

            var name = FileNameFor(hostId);
            foreach (var profile in profileDirs)
            {
                var records = System.IO.Path.Combine(profile, RecordsFolder);
                foreach (var outer in subdirectories(records))
                {
                    foreach (var inner in subdirectories(outer))
                    {
                        var candidate = System.IO.Path.Combine(inner, name);
                        if (fileExists(candidate)) found.Add(candidate);
                    }
                }
            }

            return found;
        }

        // The fallback for a status file that predates the host_session_id field:
        // read every record and find the one naming this Claude Code session.
        // `files` lists the record files under a records folder. Archived
        // records do not count as matches, so an old chat that shared the id
        // with a live one cannot make the live one ambiguous. Two live matches
        // is a refusal, not a pick: sending into the wrong chat is worse than
        // sending into none.
        internal static string? FindByCliSessionId(
            IEnumerable<string> profileDirs,
            string cliSessionId,
            Func<string, IEnumerable<string>> files,
            Func<string, string?> read)
        {
            if (string.IsNullOrEmpty(cliSessionId)) return null;

            string? match = null;
            foreach (var profile in profileDirs)
            {
                foreach (var path in files(System.IO.Path.Combine(profile, RecordsFolder)))
                {
                    var record = Parse(read(path));
                    if (record is null || record.IsArchived) continue;
                    if (!string.Equals(record.CliSessionId, cliSessionId, StringComparison.Ordinal)) continue;

                    if (match is not null) return null;
                    match = path;
                }
            }

            return match;
        }
    }

    // Answers "which claude.ai session is this Desktop chat bridged to?" for the
    // scan, which asks once per Desktop-hosted status every couple of seconds.
    //
    // Memoised so that is a stat, not a read: per Claude Code session id, the
    // record's path, its mtime and the answer taken from it, re-read only when
    // the mtime moves (Desktop rewrites the record when Remote Control is
    // switched on or off, and that has to show up while the session runs). A
    // session whose record has not been found is looked for again at most every
    // few seconds, so a Desktop chat that has not been bridged does not cost a
    // directory walk per tick.
    //
    // Profile directories come from Desktop's own discovery, never from the host
    // process's --user-data-dir: Desktop's updater can relaunch a cloned profile
    // onto the default one (CB-7), after which that argument names the wrong
    // directory while the record stays where the chat was made.
    internal sealed class DesktopBridgeLookup
    {
        internal static readonly TimeSpan RelocateAfter = TimeSpan.FromSeconds(5);

        private readonly Func<IEnumerable<string>> _profileDirs;
        private readonly Func<string, DateTime?> _mtime;
        private readonly Func<string, string?> _read;
        private readonly Func<string, IEnumerable<string>> _subdirectories;
        private readonly Func<string, bool> _fileExists;
        private readonly Func<string, IEnumerable<string>> _files;

        private sealed record Memo(string? Path, DateTime? Mtime, string? Bridge, DateTime NextLocateUtc);

        private readonly Dictionary<string, Memo> _memo = new(StringComparer.Ordinal);

        internal DesktopBridgeLookup(
            Func<IEnumerable<string>>? profileDirs = null,
            Func<string, DateTime?>? mtime = null,
            Func<string, string?>? read = null,
            Func<string, IEnumerable<string>>? subdirectories = null,
            Func<string, bool>? fileExists = null,
            Func<string, IEnumerable<string>>? files = null)
        {
            _profileDirs = profileDirs ?? (() => ClaudeDesktopManager.Discover().Select(p => p.Directory));
            _mtime = mtime ?? DiskMtime;
            _read = read ?? DiskRead;
            _subdirectories = subdirectories ?? DiskSubdirectories;
            _fileExists = fileExists ?? System.IO.File.Exists;
            _files = files ?? DiskRecordFiles;
        }

        internal string? BridgeFor(SessionStatus status, DateTime nowUtc)
        {
            if (!status.IsDesktopHosted || string.IsNullOrEmpty(status.SessionId)) return null;

            var key = status.SessionId;
            _memo.TryGetValue(key, out var memo);

            if (memo?.Path is { } known)
            {
                var mtime = _mtime(known);
                if (mtime is not null && mtime == memo.Mtime) return memo.Bridge;

                // Changed: re-read it. Gone: fall through to look again.
                if (mtime is not null)
                {
                    var refreshed = Answer(known, mtime, status.SessionId, nowUtc);
                    _memo[key] = refreshed;
                    return refreshed.Bridge;
                }
            }
            else if (memo is not null && nowUtc < memo.NextLocateUtc)
            {
                return null;
            }

            var path = Locate(status);
            var found = path is null
                ? new Memo(null, null, null, nowUtc + RelocateAfter)
                : Answer(path, _mtime(path), status.SessionId, nowUtc);
            _memo[key] = found;
            return found.Bridge;
        }

        private Memo Answer(string path, DateTime? mtime, string cliSessionId, DateTime nowUtc)
        {
            var bridge = DesktopSessionRecords.BridgeIdOf(
                DesktopSessionRecords.Parse(_read(path)), cliSessionId);
            return new Memo(path, mtime, bridge, nowUtc + RelocateAfter);
        }

        private string? Locate(SessionStatus status)
        {
            var profiles = _profileDirs().ToList();

            if (DesktopSessionRecords.IsWellFormedHostId(status.HostSessionId))
            {
                return DesktopSessionRecords
                    .RecordPathsFor(profiles, status.HostSessionId, _subdirectories, _fileExists)
                    .FirstOrDefault();
            }

            return DesktopSessionRecords.FindByCliSessionId(profiles, status.SessionId, _files, _read);
        }

        // ---- the real disk ---------------------------------------------------
        // Each swallows an IO failure into "nothing there": a profile Desktop is
        // rewriting, a folder that does not exist on this machine, a record
        // locked by antivirus on Windows. None of those is the scan's problem.

        // Internal so the file-backed tests can drive them directly.

        internal static DateTime? DiskMtime(string path)
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }

        internal static string? DiskRead(string path)
        {
            try { return System.IO.File.ReadAllText(path); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return null; }
        }

        internal static IEnumerable<string> DiskSubdirectories(string directory)
        {
            try { return System.IO.Directory.GetDirectories(directory); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        internal static IEnumerable<string> DiskRecordFiles(string recordsRoot)
        {
            var found = new List<string>();
            foreach (var outer in DiskSubdirectories(recordsRoot))
            {
                foreach (var inner in DiskSubdirectories(outer))
                {
                    found.AddRange(DiskFilesIn(inner));
                }
            }

            return found;
        }

        internal static IEnumerable<string> DiskFilesIn(string directory)
        {
            try { return System.IO.Directory.GetFiles(directory, "local_*.json"); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
