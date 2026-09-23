using System.Threading;

namespace ClaudeBuddy
{
    // The one call SessionManager makes at the end of a scan to turn whatever
    // TurnSignalTracker noticed into an actual sound.
    //
    // Everything that decides *whether* to make a noise lives in
    // TurnSoundPolicy, pure and covered by its own tests with no clock but
    // the one it is handed. This class is the seam between that decision and
    // the world: it reads the settings the decision needs, remembers when
    // this app last actually made a sound, holds the one pending signal a
    // deferred decision leaves waiting on the rate limit, and carries out
    // whichever of Silent/Chime/Summary came back. Kept this thin on purpose
    // — a bug in "should this play" belongs in a pure function with a name,
    // not folded into the one place that also touches a settings file, a
    // timer and a speaker.
    internal static class TurnSounds
    {
        private static readonly object Gate = new();

        // When this app last actually delivered a sound — a chime played, or
        // a summary was handed off to speak. Process-wide rather than
        // per-session, because the rate limit the plan asks for is on the
        // app making noise at all, not on any one orb repeating itself
        // (TurnSignalTracker already owns that half).
        private static DateTime _lastPlayed = DateTime.MinValue;

        // QA's fix for the drop-not-defer bug: the one signal currently
        // waiting out the rate limit, and the timer that will play it. One
        // slot rather than a queue — a later, higher-or-equal-ranked signal
        // replaces whatever is here wholesale, which is what "later signals
        // re-coalesce into it" means. A pending Chime action carries its own
        // path; a pending Summary action carries the session id and needs
        // `_pendingSpeak` to actually reach the orb, the same callback
        // Deliver itself was handed.
        private static SoundAction? _pending;
        private static Func<string, Task<bool>>? _pendingSpeak;
        private static Timer? _pendingTimer;

        // QA (CB-167): playback is chained onto this rather than fired
        // independently per decision. The 2 s rate limit only spaces out
        // when a new sound is *decided*; it says nothing about how long the
        // previous one takes to actually finish, and a user's own chosen
        // file can run the full 5 s cap ChimePlayer allows. Two decisions
        // landing 2.1 s apart — perfectly legal under the rate limit — could
        // otherwise start a second afplay/PlaySync while the first was still
        // running. Chaining makes "the next chime waits for the previous one
        // to actually finish" true regardless of how close together two
        // decisions land; TaskScheduler.Default keeps each link off
        // whatever thread scheduled it.
        private static Task _chimeChain = Task.CompletedTask;

        // Test seam: a scan-level test wants a clean rate-limit clock and no
        // timer left armed from a previous case, without sleeping two real
        // seconds to clear either — the same reason
        // ClaudeBuddySettings.ReloadForTests exists.
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _lastPlayed = DateTime.MinValue;
                _pending = null;
                _pendingSpeak = null;
                _pendingTimer?.Dispose();
                _pendingTimer = null;
                _chimeChain = Task.CompletedTask;
            }
        }

        // QA (CB-167): called from a session's own removal — pruned from
        // the scan (a husk, backgrounded or genuinely gone) or Settled by a
        // manual reset — so a deferred signal for that exact session can
        // never fire late for an orb that has already stopped meaning
        // anything by the time its two seconds are up. Ignored for any
        // other session's pending signal, since the one still-relevant
        // pending slot is the only thing here.
        internal static void CancelPendingFor(string sessionId)
        {
            lock (Gate)
            {
                if (_pending?.SessionId == sessionId) ClearPendingLocked();
            }
        }

        // The Prune-shaped version of the same guard: whatever the scan no
        // longer sees this pass is exactly what Prune is about to drop from
        // the tracker, and a pending signal for any of them is stale for the
        // identical reason. Called with the same `seen` set
        // ScanAndUpdateCore already built, so this costs nothing extra to
        // compute.
        internal static void CancelPendingUnlessSeen(IReadOnlySet<string> seen)
        {
            lock (Gate)
            {
                if (_pending is { } pending && !seen.Contains(pending.SessionId!)) ClearPendingLocked();
            }
        }

        // `trySpeakTurnSummary` both speaks (when it can) and reports
        // whether it did, entirely off whatever thread called Deliver —
        // SessionManager's real callback resolves to
        // OrbWindow.SpeakTurnSummaryAsync, which is where "found no text" or
        // "not an orb kind with a transcript" actually gets decided.
        // TurnSoundPolicy never sees any of this; it only ever names the
        // winning session id.
        internal static void Deliver(
            IReadOnlyList<TurnSoundEvent> events,
            Func<string, Task<bool>> trySpeakTurnSummary,
            DateTime? now = null)
        {
            if (events.Count == 0) return;

            var moment = now ?? DateTime.UtcNow;
            var decision = TurnSoundPolicy.Decide(
                events, Snapshot(), moment, LastPlayed, TextToSpeech.IsSpeaking);

            if (decision.Kind == SoundActionKind.Silent) return;

            if (decision.IsDeferred)
            {
                SchedulePending(decision, trySpeakTurnSummary);
                return;
            }

            // A live decision — the rate limit is open — supersedes
            // anything still waiting out an earlier gap. Without this, a
            // deferred chime whose timer hasn't fired yet could go off right
            // alongside this one, playing a sound that was only ever
            // supposed to happen once twice.
            ClearPending();

            Execute(decision, moment, trySpeakTurnSummary);
        }

        private static DateTime LastPlayed { get { lock (Gate) return _lastPlayed; } }

        private static void SchedulePending(SoundAction decision, Func<string, Task<bool>> trySpeakTurnSummary)
        {
            lock (Gate)
            {
                _pending = decision;
                _pendingSpeak = trySpeakTurnSummary;

                var delay = decision.PlayAt!.Value - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

                // Replaces whatever was armed rather than stacking a second
                // timer beside it — one pending slot, one timer, always.
                _pendingTimer?.Dispose();
                _pendingTimer = new Timer(_ => FirePending(), null, delay, Timeout.InfiniteTimeSpan);
            }
        }

        private static void ClearPending()
        {
            lock (Gate) ClearPendingLocked();
        }

        // The shared body every clearing path uses, callable only while
        // already holding Gate — CancelPendingFor and
        // CancelPendingUnlessSeen both check a condition and clear
        // atomically with it, which a lock-then-call-the-locking-version
        // shape cannot do without either double-locking (fine here, since
        // .NET locks are reentrant, but confusing to read) or duplicating
        // this body.
        private static void ClearPendingLocked()
        {
            _pending = null;
            _pendingSpeak = null;
            _pendingTimer?.Dispose();
            _pendingTimer = null;
        }

        // Runs on the timer's own thread-pool callback thread — never the
        // Avalonia UI thread, so Execute below is free to do exactly what it
        // would from a live Deliver call.
        private static void FirePending()
        {
            SoundAction? action;
            Func<string, Task<bool>>? speak;
            lock (Gate)
            {
                action = _pending;
                speak = _pendingSpeak;
                _pending = null;
                _pendingSpeak = null;
                _pendingTimer?.Dispose();
                _pendingTimer = null;
            }

            if (action is null || speak is null) return;

            Execute(action, DateTime.UtcNow, speak);
        }

        // Carries out a decision that is ready to play right now — never
        // called with a still-deferred one. Fire-and-forget from here down:
        // this is reached from ScanAndUpdateCore, which CB-106 keeps on the
        // Avalonia UI thread, and the whole point of everything below is
        // that nothing may block it. Exceptions are caught and logged rather
        // than thrown, the same reason SpeechSummary.SummarizeOrSayWhyAsync
        // turns a throw into a sentence instead of letting it escape.
        private static void Execute(SoundAction decision, DateTime moment, Func<string, Task<bool>> trySpeakTurnSummary)
        {
            if (decision.Kind == SoundActionKind.Summary)
            {
                _ = SpeakSummaryOrFallbackAsync(decision.SessionId!, moment, trySpeakTurnSummary);
            }
            else
            {
                PlayChimeInBackground(decision.Path!, moment);
            }
        }

        private static void PlayChimeInBackground(string path, DateTime moment)
        {
            // Stamped immediately: once a Chime decision is being carried
            // out, it is certain to play (ChimePlayer's own failure modes —
            // a process that won't start — were already true before this
            // fix and are not this fix's business). The summary path below
            // is the one that has to wait and see.
            lock (Gate) _lastPlayed = moment;

            EnqueueChime(path);
        }

        // Every real chime — the direct path here and the vibe-summary
        // fallback below — goes through this one chain, which is what
        // guarantees the two can never overlap either: a fallback chime
        // starting while an unrelated orb's chime is still mid-playback
        // would be exactly the same rattle two ordinary chimes landing
        // close together would be.
        private static void EnqueueChime(string path)
        {
            lock (Gate)
            {
                _chimeChain = _chimeChain.ContinueWith(_ =>
                {
                    try
                    {
                        ChimePlayer.Play(path);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Claude Buddy: couldn't play a turn sound: {ex.Message}");
                    }
                }, TaskScheduler.Default);
            }
        }

        // Off the UI thread for its whole life: trySpeakTurnSummary walks a
        // transcript file and, for a gateway session, awaits a round trip
        // over the wire — neither belongs on the thread ScanAndUpdateCore
        // runs on. _lastPlayed is only stamped once something is actually
        // going to be heard: a summary attempt that found no text must not
        // spend the rate limit on silence, which is why this waits for the
        // answer before touching it, unlike the chime path above, which
        // already knows it will play the moment it is called.
        private static async Task SpeakSummaryOrFallbackAsync(
            string sessionId, DateTime moment, Func<string, Task<bool>> trySpeakTurnSummary)
        {
            try
            {
                var spoke = await trySpeakTurnSummary(sessionId).ConfigureAwait(false);
                if (spoke)
                {
                    lock (Gate) _lastPlayed = moment;
                    return;
                }

                // No speakable text — a RemoteControl or ClaudeCloud orb, or
                // a local one whose transcript has no assistant turn in it
                // yet — is not "nothing happened." The turn still finished,
                // so this falls back to the ordinary chime rather than the
                // user hearing nothing at all for it.
                var fallback = Snapshot().ResolveFinishedSound(null);
                if (fallback is null) return;   // even the platform default is missing on this machine

                lock (Gate) _lastPlayed = moment;
                EnqueueChime(fallback);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Claude Buddy: couldn't speak a turn summary: {ex.Message}");
            }
        }

        // Read once per scan rather than once per event — the settings file
        // does not change mid-scan, and TurnSoundPolicy.Decide only ever
        // needs one snapshot regardless of how many sessions signalled.
        //
        // The two resolver closures are where "null means the platform
        // default" actually gets resolved into a name: SystemSoundCatalog
        // itself has no opinion on which trigger it is being asked about, so
        // that substitution happens here, once, rather than being duplicated
        // wherever a resolver gets called.
        private static SoundSettingsSnapshot Snapshot()
        {
            var directory = SystemSoundCatalog.DefaultDirectory;
            var extensions = SystemSoundCatalog.DefaultExtensions;

            return new SoundSettingsSnapshot(
                MasterEnabled: ClaudeBuddySettings.TurnSoundsEnabled,
                DefaultFinishedSetting: ClaudeBuddySettings.TurnFinishedSound,
                DefaultAttentionSetting: ClaudeBuddySettings.NeedsAttentionSound,
                OverrideFor: ClaudeBuddySettings.OrbTurnSoundFor,
                ResolveFinishedSound: setting => SystemSoundCatalog.Resolve(
                    setting ?? SystemSoundCatalog.DefaultFinishedSoundName, directory, extensions),
                ResolveAttentionSound: setting => SystemSoundCatalog.Resolve(
                    setting ?? SystemSoundCatalog.DefaultAttentionSoundName, directory, extensions));
        }
    }
}
