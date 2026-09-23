using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Plays one short sound file to completion, capped at five seconds, as a
    // process of its own — never TextToSpeech's tracked process, and never
    // stoppable by the same Cancel() a speak button reaches for.
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

        // QA (CB-167): the process currently playing, if there is one.
        // Tracked so Cancel below — called once, from the app's own Quit
        // path — can stop it, the same guarantee OrbWindow's Closed handler
        // already gives TextToSpeech's process. Nothing but Play (setting
        // it) and Cancel (reading and clearing it) touches this.
        private static Process? _playing;
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

            lock (PlayingGate) _playing = proc;

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

        // Only clears the tracked process if it is still the one this call
        // started — Cancel can have already claimed and cleared it
        // concurrently (a KillTree in flight while this finally block also
        // runs), and a later Play's own process must never be nulled out by
        // an earlier one's cleanup racing behind it.
        [ExcludeFromCodeCoverage]
        private static void ClearIfCurrent(Process proc)
        {
            lock (PlayingGate)
            {
                if (ReferenceEquals(_playing, proc)) _playing = null;
            }
        }

        // QA (CB-167): the app's own Quit path (TrayController.Shutdown)
        // calls this so a chime still mid-playback does not keep the
        // machine making noise once the app itself is gone — a sudden
        // SIGKILL or a crash cannot be caught here, the same limit
        // TextToSpeech.Cancel already lives with, but an ordinary Quit now
        // can be.
        [ExcludeFromCodeCoverage]
        internal static void Cancel()
        {
            Process? victim;
            lock (PlayingGate)
            {
                victim = _playing;
                _playing = null;
            }

            if (victim is not null) KillTree(victim);
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
