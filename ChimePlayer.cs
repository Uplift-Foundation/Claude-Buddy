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
            var seam = PlayForTests;
            if (seam is not null)
            {
                seam(path);
                return;
            }

            Process proc;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                proc = new Process
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
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                proc = new Process { StartInfo = WindowsStartInfoFor(path) };
            }
            else
            {
                return;
            }

            lock (PlayingGate) _live.Add(proc);

            try
            {
                if (!proc.Start())
                {
                    ClearIfCurrent(proc);
                    proc.Dispose();
                    return;
                }
            }
            catch
            {
                ClearIfCurrent(proc);
                proc.Dispose();
                return;
            }

            try
            {
                if (!proc.WaitForExit((int)MaxDuration.TotalMilliseconds))
                {
                    KillTree(proc);
                }
            }
            finally
            {
                ClearIfCurrent(proc);
                proc.Dispose();
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
