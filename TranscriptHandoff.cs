using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace ClaudeBuddy
{
    // Whether a session's own transcript says its conversation was handed off
    // to a background job, with nothing having happened in the session since.
    //
    // Backgrounding a running turn is the case this exists for, and it was
    // found by counting orbs on a screenshot: the same conversation drawn
    // twice, once green and working and once with the background badge. When
    // the user backgrounds a turn, Claude Code appends a system row to the
    // interactive session's transcript and forks the conversation into a
    // background job (`--fork-session --resume <this transcript>`). The job
    // inherits the title, gets its own session id, and writes its own status
    // file — a second orb wearing the same two letters. The interactive
    // session, meanwhile, never fires another hook: no Stop event ends the
    // turn it handed away, so its status file sits at "generating" forever,
    // its pid (the TUI, which has moved on to other conversations) keeps
    // answering, and no existing rule can touch it. Observed live: session
    // 6d3a9d57 frozen at "generating" in tmux pane %3 while job b1425d42
    // carried the conversation, both titled "Unmerged branches and PRs".
    //
    // The handoff is recorded nowhere convenient. The job's own state.json
    // does not name its parent, and the only process-level link is the
    // worker's argv — so the parent's transcript, which this app already
    // knows how to tail, is the one place the fact lives on disk.
    //
    // Pure half and I/O half, separated the way TranscriptIdentity's are: the
    // thing with a right answer takes rows, so the answer can be asserted
    // against rows captured off a real machine rather than against whatever
    // this machine is doing.
    internal static class TranscriptHandoff
    {
        // The row Claude Code appends when a turn is backgrounded. Observed
        // rather than documented, the same standing PaneTitleGlyph has —
        // captured off a real transcript (CLI 2.1.251):
        //
        //   {"parentUuid":"…","isSidechain":false,"type":"system",
        //    "subtype":"informational",
        //    "content":"Backgrounding after the current tool finishes…",
        //    "isMeta":false,…,"level":"warning",…}
        //
        // Matched as a system row whose content begins "Backgrounding" rather
        // than by the full sentence, because the tail of the sentence
        // describes the trigger ("after the current tool finishes") and a
        // turn backgrounded some other way plausibly words it differently.
        // The direction of failure is what makes the looser match safe to
        // hold: a marker that stops matching brings back the duplicate orb,
        // which is visible and mild, while the guards that stay — a real
        // system row, and nothing conversational after it — keep the false
        // positive (hiding a session someone is typing at) behind two locks.
        //
        // Contains() against the raw row is sound for the reason
        // TranscriptReader's own assistant filter is: inside a JSON string
        // every quote is escaped, so these exact byte sequences cannot occur
        // in message text — only a real top-level record carries them.
        private const string SystemMark = "\"type\":\"system\"";
        private const string BackgroundingMark = "\"content\":\"Backgrounding";

        // The rows that say the conversation is still being had here. Either
        // one after the marker means the session lived on — the user resumed
        // typing, or an answer landed — and the orb must come back. Housekeeping
        // rows (cost-state, bridge-session, and whatever Claude Code appends
        // next year) deliberately clear nothing: they follow the marker in
        // every observed capture and say nothing about anyone being present.
        private const string UserMark = "\"type\":\"user\"";
        private const string AssistantMark = "\"type\":\"assistant\"";

        // How much tail to read. Deliberately smaller than TranscriptReader's
        // 256KB identity window, and a different question entirely: identity
        // has to agree with the hook about which records are visible, while
        // this only has to see past the housekeeping rows that follow a
        // handoff — three rows totalling under 2KB in the real capture. The
        // window is asked about once per transcript change for every session
        // on screen, so it is kept small on purpose; a marker buried deeper
        // than this reads as "not handed off", which is the fail-open
        // direction (a duplicate orb, not a hidden session). Asserted in
        // TranscriptHandoffWindowTests so the limit is stated, not discovered.
        internal const int TailWindowBytes = 32768;

        // In a holder for the reason BackgroundJobs' gate is: it is only taken
        // by the excluded I/O path below, and a static field initializer does
        // not run until some static field is touched, so it reads as an
        // uncovered line belonging to code that is measured.
        [ExcludeFromCodeCoverage]
        private static class Cache
        {
            internal static readonly object Gate = new();
        }

        // Answer per transcript path, keyed by the file's length and mtime so
        // a transcript that has not grown is never re-read. A husk's transcript
        // never grows again, which is the common case this exists for: after
        // the first read, the scan pays one stat per pass for it, forever.
        private static readonly Dictionary<string, (long Length, DateTime Written, bool Answer)>
            _answers = new(StringComparer.Ordinal);

        // The I/O half: stat, consult the cache, read the tail only when the
        // file has changed. Covered by TranscriptHandoffWindowTests against
        // temp files rather than excluded — unlike States() in BackgroundJobs
        // this launches nothing, and the cache-or-reread choice is a real
        // decision worth asserting.
        //
        // A path that cannot be statted answers false, and the direction is
        // deliberate and opposite to BackgroundJobs.IsLive's: that rule
        // decides whether to *hide* an orb on the daemon's say-so, so an
        // unreadable listing must hide nothing. This rule's positive answer
        // is itself the hiding, so an unreadable transcript must assert
        // nothing — the orb stays, which is what the screen showed before
        // this file existed.
        internal static bool EndsBackgrounded(string? transcriptPath)
        {
            if (string.IsNullOrEmpty(transcriptPath)) return false;

            var stat = Stat(transcriptPath);
            if (stat is null) return false;

            lock (Cache.Gate)
            {
                if (_answers.TryGetValue(transcriptPath, out var seen)
                    && seen.Length == stat.Value.Length
                    && seen.Written == stat.Value.Written)
                {
                    return seen.Answer;
                }
            }

            var answer = EndsBackgrounded(
                TranscriptReader.TailLines(transcriptPath, TailWindowBytes));

            lock (Cache.Gate)
            {
                // Sessions come and go and nothing ever unkeys them; a cap that
                // simply starts over is enough for a dictionary this small, and
                // costs one extra read per entry when it fires.
                if (_answers.Count >= 512) _answers.Clear();

                _answers[transcriptPath] = (stat.Value.Length, stat.Value.Written, answer);
            }

            return answer;
        }

        // The decision, over rows. Walked backwards, because the question is
        // about how the transcript *ends*: the first conversational row found
        // means the session lived on past whatever else the tail holds, and
        // the marker only counts while nothing conversational follows it.
        //
        // A row this recognises as neither — cost-state, bridge-session, a
        // summary, a torn first line of the window, a record type invented
        // after this was written — is skipped rather than judged. Skipping is
        // load-bearing in both directions: the housekeeping rows that follow
        // every observed handoff must not hide the marker, and an unknown row
        // must not clear it, or the rule would quietly stop working the first
        // time Claude Code appends something new after the handoff.
        internal static bool EndsBackgrounded(IReadOnlyList<string> lines)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];

                // Conversation first, so a row that somehow carried both — an
                // assistant message quoting a raw transcript, which escaping
                // should already make impossible — reads as conversation. Of
                // the two mistakes, keeping the orb is the recoverable one.
                if (line.Contains(UserMark, StringComparison.Ordinal)
                    || line.Contains(AssistantMark, StringComparison.Ordinal))
                {
                    return false;
                }

                if (line.Contains(SystemMark, StringComparison.Ordinal)
                    && line.Contains(BackgroundingMark, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // Excluded for the reason TranscriptReader's Safe* wrappers are: the
        // file belongs to a session that may be ending, so it can vanish
        // between any two of these calls, and that race is not a state a test
        // can hold still. The decisions made with the answer are above and
        // stay measured.
        [ExcludeFromCodeCoverage]
        private static (long Length, DateTime Written)? Stat(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
