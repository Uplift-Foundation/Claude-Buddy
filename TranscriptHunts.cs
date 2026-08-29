using System;
using System.Collections.Generic;

namespace ClaudeBuddy
{
    // A memo of transcript hunts, for status files whose transcript_path names
    // a file that is not there — see SessionManager.WantsTranscriptRepair for
    // where that shape comes from.
    //
    // The hunt itself (TranscriptReader.FindTranscriptFor) is a recursive walk
    // of every projects directory under every Claude Code config root, and the
    // scan runs every two seconds. Unmemoized, a single mislocated status file
    // turns that walk into a permanent per-scan cost, because the scan re-reads
    // the file — wrong path and all — on every pass, so nothing it fixed last
    // time stays fixed. So both answers are remembered, differently:
    //
    //  - A found path is kept for as long as the file it names exists. The
    //    stat guarding it is the same cheap check the caller already makes
    //    against the recorded path, and a transcript that vanishes gets one
    //    fresh hunt rather than a stale answer.
    //  - "Not found" is kept for Retry, then asked again. Forever would be
    //    wrong in the direction that matters: the transcript a brand-new
    //    session is about to write would never be noticed, and its orb would
    //    stay hidden by the nothing-to-show rule after the conversation had
    //    started. Ten seconds mirrors BackgroundJobs.CacheMs, and for the same
    //    reason — long enough to be cheap against a two-second scan, short
    //    enough that the answer changing is noticed while someone is looking.
    //
    // hunt and exists are seams for the reason every seam in the scan is: the
    // real ones are a directory walk and a stat against this machine's own
    // transcripts, and the policy here — when to re-ask — is the part with a
    // decision in it.
    internal sealed class TranscriptHunts
    {
        internal static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

        private readonly Dictionary<string, (DateTime When, string? Path)> _answers =
            new(StringComparer.Ordinal);

        internal string? Locate(
            string sessionId, DateTime now,
            Func<string, string?> hunt, Func<string, bool> exists)
        {
            if (_answers.TryGetValue(sessionId, out var last))
            {
                if (last.Path is not null && exists(last.Path)) return last.Path;
                if (last.Path is null && now - last.When < Retry) return null;
            }

            var found = hunt(sessionId);
            _answers[sessionId] = (now, found);
            return found;
        }
    }
}
