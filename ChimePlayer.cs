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

        // The seam. A scan-level test can assert exactly which path was
        // decided on without a sound card, a process, or even /usr/bin/afplay
        // existing on the runner — the same pattern as
        // SpeechRequest.UtteranceForTests and SpeechSummary.SummarizerForTests.
        // Set and cleared by the test, never by production code.
        internal static Action<string>? PlayForTests;

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
                // QA (CB-167): not interpolated into the command line at
                // all, even quoted. A single-quote escape only guards
                // against U+0027 — PowerShell's tokenizer also accepts the
                // Unicode "smart" apostrophes U+2018 through U+201B as
                // string delimiters, so a path containing one of those
                // (a OneDrive folder renamed with a curly quote, a
                // copy-pasted file name) would close the string early and
                // let whatever followed run as a second command. Passed
                // through the environment instead, where PowerShell never
                // tokenizes it as script text, and read back as a literal
                // value with $env:. TextToSpeech's own PowerShell voice-name
                // escaping has the identical hole and is deliberately left
                // alone here — out of scope for this fix, tracked as its own
                // bug.
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        ArgumentList =
                        {
                            "-NoProfile", "-Command",
                            "(New-Object Media.SoundPlayer $env:CLAUDEBUDDY_CHIME).PlaySync()"
                        },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                proc.StartInfo.EnvironmentVariables["CLAUDEBUDDY_CHIME"] = path;
            }
            else
            {
                return;
            }

            try
            {
                if (!proc.Start())
                {
                    proc.Dispose();
                    return;
                }
            }
            catch
            {
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
                proc.Dispose();
            }
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
