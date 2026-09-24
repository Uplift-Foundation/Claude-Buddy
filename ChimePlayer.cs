using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Plays one short sound file to completion, capped at five seconds, as a
    // process of its own — never TextToSpeech's tracked process, and never
    // stoppable by the same Cancel() a speak button reaches for.
    //
    // Play is the single entry point every caller in this app uses to make
    // any kind of chime — a scan chime via TurnSounds, a Settings preview,
    // the summary-fallback chime, whichever — so tracking every live
    // process centrally here, rather than in whichever caller happened to
    // start it, is what lets StopAll below mean all of them at once.
    //
    // That separation is not tidiness. A chime and a spoken reply are allowed
    // to overlap by design — TurnSoundPolicy's busy-speech fallback exists
    // for the one case that would be jarring (a vibe summary cutting in on
    // speech already playing), and everywhere else a Glass chime landing
    // half a second into an unrelated utterance is fine. Sharing
    // TextToSpeech's process slot would make the speak button's stop control
    // silently kill chimes too, which is a coupling nobody asked for and the
    // opposite of what "a chime never cancels speech" means.
    //
    // The child-process shape below is deliberately the same one
    // TextToSpeech.Speak uses — /usr/bin/afplay on macOS, PowerShell's
    // Media.SoundPlayer on Windows, a pid captured before anything that could
    // dispose the Process object out from under it, KillTree rather than a
    // bare Kill(). Copying it rather than inventing a second shape means a
    // fix to one process-management bug here is recognisable as the same fix
    // TextToSpeech already needed, not a new kind of bug to learn.
    internal static class ChimePlayer
    {
        internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(5);

        // Named rather than a literal repeated in two places, and public
        // enough for a test to reference it without duplicating the string.
        internal const string ChimeEnvVar = "CLAUDEBUDDY_CHIME";

        // QA (CB-167): the whole Windows script, as a value rather than
        // built inline inside Play() — that is what lets
        // WindowsStartInfoFor below be asserted on without a Windows
        // machine to run it on. Two things are deliberate about its shape.
        // First, the path is read back only through $env: and is never
        // interpolated into this string — see WindowsStartInfoFor's own
        // comment for what that closes. Second, the empty-variable guard
        // comes before anything else: a bug that ever calls this with
        // nothing to play (the C# side already only calls Play with a
        // resolved path, but the script is its own contract) exits loudly
        // with a real error instead of either doing nothing silently or
        // leaving Media.SoundPlayer's own opaque failure as the only sign
        // anything went wrong.
        internal const string WindowsScript =
            "if ([string]::IsNullOrEmpty($env:CLAUDEBUDDY_CHIME)) { " +
            "Write-Error 'CLAUDEBUDDY_CHIME is not set'; exit 1 }; " +
            "(New-Object Media.SoundPlayer $env:CLAUDEBUDDY_CHIME).PlaySync()";

        // The seam. A scan-level test can assert exactly which path was
        // decided on without a sound card, a process, or even /usr/bin/afplay
        // existing on the runner — the same pattern as
        // SpeechRequest.UtteranceForTests and SpeechSummary.SummarizerForTests.
        // Set and cleared by the test, never by production code.
        internal static Action<string>? PlayForTests;

        // Round 3(d): a second test seam, distinct from PlayForTests above.
        // PlayForTests returns before Play ever reaches the process-
        // tracking code at all — there is no real process behind a
        // substituted chime for it to track — so it has nothing to offer a
        // test of StopAll's own new behaviour: that it kills every
        // concurrently-tracked process, not just one. This registers a real
        // process through the exact same Add path Play uses, so a test can
        // hand it something harmless and long-running (never real audio)
        // and prove StopAll actually kills it.
        internal static void TrackForTests(Process proc)
        {
            lock (PlayingGate) _live.Add(proc);
        }

        // QA (CB-167), reworked in round 2 (finding 6): every process this
        // class currently has running, not just one. A single Process?
        // slot was only ever correct while every caller funnelled through
        // TurnSounds' own _chimeChain, which serialises scan chimes so
        // there is never more than one in flight — but Play is the shared
        // entry point for every kind of chime this app makes, and two of
        // them do not go through that chain at all: a Settings preview
        // (SettingsWindow.cs) fires directly off a UI click, and the
        // summary-fallback chime (TurnSounds.SpeakSummaryOrFallbackAsync)
        // can start while an unrelated scan chime is still mid-playback on
        // the chain. A single slot silently dropped tracking of whichever
        // one wasn't "current" — StopAll on Quit would kill one process and
        // leave the other one making noise. A set is what actually holds
        // "everything Play is running right now," so StopAll can mean all
        // of it regardless of which caller started which process.
        private static readonly HashSet<Process> _live = new();
        private static readonly object PlayingGate = new();

        // QA round 3, finding 4 (LOW): a Settings preview used to go
        // straight through Play() the same as any other chime, so stepping
        // through the sound picker with ordinary, human-paced pauses
        // between choices — well past SettingsWindow's 250ms debounce, each
        // one its own flush — started a fresh, independent process every
        // time with nothing to stop the previous one still playing
        // underneath it. Four choices a person actually paused on sounded
        // like four overlapping chimes in a real run.
        //
        // PlayPreview below is a channel rather than a second Play(): only
        // one worker is ever calling into an actual preview, and a request
        // that arrives while it is busy replaces whatever the worker was
        // going to play next rather than queuing alongside it — so several
        // requests landing while one is playing collapse into "play
        // whichever was requested most recently, the moment the current
        // one is free." When the worker is genuinely blocked inside a real
        // process's WaitForExit, "free" is hurried along: a new request
        // kills the process already playing (KillTree) before the worker
        // moves on, which is also why this still needs its own slot here
        // rather than reusing whichever Process a scan chime happens to be
        // running — the two must never contend to kill each other.
        private static string? _nextPreviewPath;
        private static bool _previewWorkerRunning;
        private static Process? _currentPreview;

        // The one place that both kills a preview already playing and
        // records its replacement, so PlayOnePreview's production path and
        // SetCurrentPreviewForTests below can share it rather than risk the
        // two drifting apart. Still added to _live via the same field
        // every other kind of chime uses, so StopAll reaches a preview
        // exactly as it already reaches a scan chime or the summary
        // fallback — this only adds a second, earlier kill path ahead of
        // it, not a second set to keep in sync.
        //
        // Round 4, item 2: `next` must already have started before this is
        // called — every real caller (PlayOnePreview) now calls this only
        // after its own Start succeeds, and the test seam below hands it
        // an already-started process for the same reason. `previous` is
        // safe to KillTree here precisely because IT went through this
        // same call, after ITS OWN Start, whenever it became current.
        private static void KillPreviousAndTrackNewPreview(Process next)
        {
            lock (PlayingGate)
            {
                if (_currentPreview is { } previous) KillTree(previous);
                _currentPreview = next;
                _live.Add(next);
            }
        }

        // Round 3 finding 4's version of TrackForTests above, for the
        // preview slot specifically: a test hands this a real, harmless,
        // long-running process (never real audio) through the exact path
        // PlayPreview itself uses to decide whether to kill a predecessor,
        // and can then prove that registering a second one kills the
        // first — see ChimePlayerTests.
        internal static void SetCurrentPreviewForTests(Process next) =>
            KillPreviousAndTrackNewPreview(next);

        // Round 4 (CB-167): once StopAll has run, this class must never
        // start anything new — a chime already queued behind it (TurnSounds'
        // own _chimeChain, or a FirePending timer that happens to fire
        // during the app's own unwind) starting a fresh process right after
        // StopAll just killed every existing one would defeat the entire
        // point of finding 5/round 3(d): the app would still be making
        // noise after Quit. Sticky for the rest of the process's life by
        // design — there is no "un-stop" in production, only
        // ResetStoppedForTests below.
        private static bool _stopped;

        // Its own property, kept out of Play's own exclusion, so this one
        // guard is measured directly rather than folded into a method that
        // starts a real subprocess and cannot be run under coverage.
        internal static bool IsStopped { get { lock (PlayingGate) return _stopped; } }

        // Test seam: _stopped is deliberately sticky in production, but a
        // test suite runs many cases in one process, and the next test's
        // Play calls must not silently no-op forever just because an
        // earlier test proved StopAll works.
        internal static void ResetStoppedForTests() { lock (PlayingGate) _stopped = false; }

        // Builds the Windows ProcessStartInfo on its own, callable and
        // assertable from a test even though Play itself is excluded from
        // coverage. The path never appears in ArgumentList or in
        // WindowsScript — it only ever reaches PowerShell through the
        // environment, which is what makes this safe with any character a
        // filesystem allows a path to contain. The interpolated version
        // this replaced escaped a bare U+0027 apostrophe by doubling it, but
        // PowerShell's tokenizer also accepts the Unicode "smart" quotes
        // U+2018 through U+201B as string delimiters — a path containing
        // one of those (a OneDrive folder renamed with a curly quote, a
        // copy-pasted file name) could close the quoted string early and run
        // whatever followed as a second command. A leading '-' is the other
        // shape QA asked this be checked against: because the path is a
        // variable's *value*, substituted after PowerShell has already
        // parsed $env:CLAUDEBUDDY_CHIME as one positional argument token,
        // the runtime value is never re-parsed as a flag the way a literal
        // "-something" written directly in the script text could be.
        // TextToSpeech's own PowerShell voice-name escaping has the
        // identical smart-quote hole and is deliberately left alone here —
        // out of scope for this fix, tracked as its own bug.
        internal static ProcessStartInfo WindowsStartInfoFor(string path)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                ArgumentList = { "-NoProfile", "-Command", WindowsScript },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.EnvironmentVariables[ChimeEnvVar] = path;
            return startInfo;
        }

        // Excluded from coverage along with everything it calls: this starts
        // a real audio subprocess and blocks the calling thread on it for up
        // to five seconds, which is exactly the shape TextToSpeech.Speak and
        // KillTree are already excluded for. The branch that checks
        // PlayForTests is excluded along with the rest rather than split into
        // its own method, per the plan — every scan-level test that exercises
        // it does so through the seam actually firing, which coverage cannot
        // see through an exclusion but a failing assertion still would.
        [ExcludeFromCodeCoverage]
        internal static void Play(string path)
        {
            // Round 4, item 1: the seam path still checks IsStopped on its
            // own — a test proving "Play is a no-op once stopped" has to
            // see that through the same seam every other Play test uses.
            // The real path's own check moved below, into the same lock as
            // _live.Add: reading IsStopped here, releasing that lock, and
            // only then adding to _live left a real gap open — StopAll
            // could run in between, finding _live still empty, and a
            // process added and started right after would never be killed
            // at all despite _stopped already being true when Play started.
            var seam = PlayForTests;
            if (seam is not null)
            {
                if (IsStopped) return;
                seam(path);
                return;
            }

            var proc = BuildProcess(path);
            if (proc is null) return;

            lock (PlayingGate)
            {
                if (_stopped)
                {
                    proc.Dispose();
                    return;
                }

                _live.Add(proc);
            }

            RunAndWait(proc);
            ClearIfCurrent(proc);
            proc.Dispose();
        }

        // Round 3 finding 4: the preview channel. Cheap and quick to call —
        // a lock, at most one KillTree, and either a field assignment or
        // (only the first time in a while) starting the worker — so unlike
        // Play() above there is no need for a caller to wrap this in its
        // own Task.Run the way SettingsWindow used to; see
        // PlayPendingPreview's own comment there.
        [ExcludeFromCodeCoverage]
        internal static void PlayPreview(string path)
        {
            // Round 4: the preview channel is exactly the kind of "queued
            // chime" that must not start anything once StopAll has run —
            // it holds its own request behind a lock the same way
            // TurnSounds' _chimeChain holds a scan chime, and a request
            // queued (or already sitting queued) during the app's own
            // unwind must not still spawn a fresh process afterward.
            if (IsStopped) return;

            // Round 4, item 3: PlayPreview is called straight from
            // SettingsWindow's SelectionChanged/Click, on the UI thread —
            // and KillTree, on Windows, runs taskkill and can wait up to
            // 3 s for it. The lock below only ever swaps state (which
            // process is the victim, whether the worker needs starting);
            // the actual kill is dispatched to a background task so the
            // UI thread is never the one waiting on it.
            Process? victim;
            bool startWorker;
            lock (PlayingGate)
            {
                _nextPreviewPath = path;
                victim = _currentPreview;
                startWorker = !_previewWorkerRunning;
                _previewWorkerRunning = true;
            }

            if (victim is not null) _ = Task.Run(() => KillTree(victim));
            if (startWorker) _ = Task.Run(RunPreviewWorker);
        }

        // The worker: picks up whichever path is currently the most
        // recently requested, plays it to completion (or until the next
        // request kills it early), and loops — stopping only once nothing
        // new arrived while it was busy. Never more than one of these
        // running at a time (PlayPreview only starts it when
        // _previewWorkerRunning was false), which is what makes "only one
        // preview ever calls into ChimePlayer.Play/the test seam at once"
        // true by construction rather than by timing.
        [ExcludeFromCodeCoverage]
        private static void RunPreviewWorker()
        {
            while (true)
            {
                string path;
                lock (PlayingGate)
                {
                    if (_nextPreviewPath is null)
                    {
                        _previewWorkerRunning = false;
                        return;
                    }

                    path = _nextPreviewPath;
                    _nextPreviewPath = null;
                }

                PlayOnePreview(path);
            }
        }

        // One iteration of the worker's loop. Seam-checked the same way
        // Play() is — a test's seam call still runs on this same
        // background worker thread, never the caller's — but with no real
        // process behind it there is nothing for KillPreviousAndTrackNewPreview
        // to register or for a later request to kill; that half of this
        // fix is proven with real processes instead (see
        // ChimePlayerTests.PlayPreviewKillsTheLivePreviewProcessBeforeTrackingTheNext),
        // and SteppingThroughPreviewsDoesNotStackOverlappingPlayback proves
        // the coalescing above, which needs no real process to be true.
        [ExcludeFromCodeCoverage]
        private static void PlayOnePreview(string path)
        {
            var seam = PlayForTests;
            if (seam is not null)
            {
                // Round 4, item 1's seam-side check, same reasoning as Play's.
                if (IsStopped) return;
                seam(path);
                return;
            }

            var proc = BuildProcess(path);
            if (proc is null) return;

            if (!TryStart(proc))
            {
                proc.Dispose();
                return;
            }

            // Round 4, item 2: tracked — and the previous preview killed —
            // only now that Start has actually succeeded. The old order
            // called this (and so set _currentPreview, added to _live)
            // BEFORE Start, so a KillTree landing in that exact gap read a
            // pid off a Process that had never really started at all —
            // Process.Id throws before Start runs, and KillTree's own catch
            // swallows that silently, leaving the "previous" preview to run
            // to completion unkilled.
            KillPreviousAndTrackNewPreview(proc);

            // The other half of item 2: a stop can land in the gap between
            // Start succeeding and the tracking lock just above — checked
            // again right here, rather than trusted from whatever
            // PlayPreview's own entry check saw a moment earlier, so a
            // process that only just started is killed immediately instead
            // of being left to run its full duration.
            if (IsStopped)
            {
                KillTree(proc);
            }
            else
            {
                WaitAndKillIfStillRunning(proc);
            }

            lock (PlayingGate)
            {
                _live.Remove(proc);
                if (ReferenceEquals(_currentPreview, proc)) _currentPreview = null;
            }

            proc.Dispose();
        }

        // The per-OS process shape Play and PlayOnePreview both start —
        // pulled out once there were two call sites, so a fix to one
        // doesn't have to be remembered for the other. Null off any
        // platform this app doesn't make a sound on.
        [ExcludeFromCodeCoverage]
        private static Process? BuildProcess(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/afplay",
                        ArgumentList = { path },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new Process { StartInfo = WindowsStartInfoFor(path) };
            }

            return null;
        }

        // Starts `proc`, waits up to MaxDuration, and kills it if it hasn't
        // exited by then — the half of Play's old body that has nothing to
        // do with which tracking field owns the process, so both call
        // sites share it and only Play/PlayOnePreview's surrounding code
        // differs in how they register and release it.
        [ExcludeFromCodeCoverage]
        private static void RunAndWait(Process proc)
        {
            if (!TryStart(proc)) return;
            WaitAndKillIfStillRunning(proc);
        }

        // Split out of RunAndWait in round 4: PlayOnePreview needs to do
        // something (track the process, re-check _stopped) in the gap
        // between Start succeeding and the wait beginning, which RunAndWait
        // itself has no room for.
        [ExcludeFromCodeCoverage]
        private static bool TryStart(Process proc)
        {
            try
            {
                return proc.Start();
            }
            catch
            {
                return false;
            }
        }

        [ExcludeFromCodeCoverage]
        private static void WaitAndKillIfStillRunning(Process proc)
        {
            if (!proc.WaitForExit((int)MaxDuration.TotalMilliseconds))
            {
                KillTree(proc);
            }
        }

        // Removing from a set rather than nulling a slot means this is safe
        // to call once per process regardless of how many others are still
        // live at the same moment — no "only the current one" ambiguity to
        // get wrong the way a single Process? slot had.
        [ExcludeFromCodeCoverage]
        private static void ClearIfCurrent(Process proc)
        {
            lock (PlayingGate) _live.Remove(proc);
        }

        // QA (CB-167), renamed in round 2 (finding 6) from Cancel to StopAll
        // to say what it now actually does: every process Play currently has
        // running, not one. Still reached from the same place — the app's
        // own Quit path, TrayController.Shutdown — so a chime still
        // mid-playback (a scan chime, a Settings preview, the
        // summary-fallback chime, whichever) does not keep the machine
        // making noise once the app itself is gone. A sudden SIGKILL or a
        // crash cannot be caught here, the same limit TextToSpeech.Cancel
        // already lives with, but an ordinary Quit now can be, for all of
        // them at once.
        [ExcludeFromCodeCoverage]
        internal static void StopAll()
        {
            // Round 4: _stopped is set in its own lock acquisition, ahead of
            // KillEverythingLive's separate one below — safe as two steps
            // rather than one atomic block only because Play and
            // PlayOnePreview (item 1) each check _stopped inside the very
            // same lock they use to add to _live. Once this line has run,
            // nothing can be added to _live that KillEverythingLive's own
            // snapshot might miss; it only ever needs to sweep up whatever
            // was already there before _stopped became true.
            lock (PlayingGate) _stopped = true;

            KillEverythingLive();
        }

        // Round 4, item 4: the app's desktop.ShutdownRequested handler
        // calls this instead of StopAll. ShutdownRequested fires for a
        // quit that can still be cancelled — the event's own Cancel
        // property, or macOS refusing for a reason of its own — unlike
        // Exit, which fires only once shutdown is genuinely proceeding.
        // Killing whatever is currently playing is still the right thing
        // to do here: the user asked to quit, and a chime shouldn't
        // outlive that choice even if the app itself does. But setting the
        // sticky _stopped flag would leave a *cancelled* quit permanently
        // deaf for the rest of the process's life — silencing every chime
        // from then on for a quit that never actually happened — which is
        // wrong in a way StopAll's own sticky contract is not: only a
        // shutdown that actually goes through may make that call.
        [ExcludeFromCodeCoverage]
        internal static void KillCurrentlyPlayingForCancellableShutdown() => KillEverythingLive();

        [ExcludeFromCodeCoverage]
        private static void KillEverythingLive()
        {
            Process[] victims;
            lock (PlayingGate)
            {
                victims = _live.ToArray();
                _live.Clear();
            }

            foreach (var victim in victims) KillTree(victim);
        }

        // Copied from TextToSpeech.KillTree rather than shared with it,
        // because the two now differ in one respect that matters: this one
        // has no _speaking field to clear and no SpeakState to move back to
        // Idle, since a chime was never tracked as "the" utterance in the
        // first place. See that method's own comment for why taskkill /T /F
        // is asked first on Windows and Process.Kill(entireProcessTree:
        // true) is asked regardless — a single standalone Kill(tree: true)
        // was measured to leave one survivor per utterance in a real run of
        // TextToSpeech, and there is no reason to expect afplay or
        // PlaySync's child tree to behave differently.
        [ExcludeFromCodeCoverage]
        private static void KillTree(Process victim)
        {
            int pid;
            try
            {
                pid = victim.Id;
            }
            catch
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo("taskkill")
                    {
                        ArgumentList = { "/PID", pid.ToString(), "/T", "/F" },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    taskkill?.WaitForExit(3000);
                }
                catch { /* fall through to Kill below */ }
            }

            try
            {
                if (!victim.HasExited) victim.Kill(entireProcessTree: true);
            }
            catch { /* already gone, or the object is disposed — both fine here */ }
        }
    }
}
