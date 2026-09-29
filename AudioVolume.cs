using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeBuddy
{
    // CB-200: the two volume levels Buddy applies to the audio it makes — the
    // Speech level for spoken replies (TextToSpeech) and the Alert level for
    // turn-sound chimes (ChimePlayer) — and every rule about turning one of
    // those levels into the units a particular backend takes.
    //
    // Pure and window-free for OrbSizing's reason: the settings slider, the
    // speak path and the chime path all have to agree about what a level
    // means, and the only honest way to test "50% reaches SAPI as 50" is to
    // call the function that decides it rather than to listen.
    //
    // A level is a linear multiplier from 0 (silent) to 1 (exactly what every
    // build before this one played). 1 is the default and it is special on
    // purpose: at full volume every backend below gets *exactly* the argv,
    // script and text it got before CB-200, so nobody who never touches the
    // sliders is running a code path that did not exist last release.
    //
    // Each mechanism was checked against the real tool before being relied on
    // (2026-09-29, recorded in the PR). The shape of what came back matters
    // for reading the numbers below:
    //
    // - `say` has no volume flag, but honours the [[volm N]] embedded speech
    //   command. Rendered to a file with and without it, four voices went
    //   silent at 0 and quieter at 0.5 and 0.1 — linearly for Ava (Premium),
    //   and on a steeper curve for Susan (Enhanced), Karen and Samantha, where
    //   0.5 measured about a quarter of the amplitude. Monotonic everywhere,
    //   so the slider moves the right way; not linear everywhere, so "50%" is
    //   a position on a slider rather than a promise of -6 dB.
    // - SAPI's SpeechSynthesizer.Volume is an int 0-100 and *throws* outside
    //   that range (150 and -1 both raised SetValueInvocationException on
    //   Windows PowerShell 5.1), which is why SapiVolume clamps before it
    //   rounds rather than trusting the caller. Rendered to a file on the
    //   Windows box, 0 was silent and 50/10 were quieter on SAPI's own curve.
    // - afplay's -v takes a float and parses it leniently: `-v abc` exits 0
    //   and plays, which in practice means it read a zero. A level formatted
    //   in the user's culture ("0,5") is exactly that failure, so every
    //   number here goes out through InvariantCulture.
    // - Windows chimes go through Media.SoundPlayer, which has no volume at
    //   all. Rather than reach for the process's mixer session (which Windows
    //   remembers per executable, and powershell.exe is also what speaks), the
    //   chime's own samples are scaled into a cached copy — see ScaleWav.
    internal static class AudioVolume
    {
        public const double Default = 1.0;
        public const double Min = 0.0;
        public const double Max = 1.0;

        // The slider's granularity. ScaledCopy names a copy by whole percent,
        // so a chime has at most 100 scaled copies however a level was set,
        // and 20 at the slider's own steps (full volume never needs one).
        public const double Step = 0.05;

        // How the Speech level reaches the Kokoro side-car. An environment
        // variable rather than a --volume argument, deliberately: the engine
        // is downloaded separately and an app can end up running an older one
        // (NeuralSpeech.UsableEnginePath's fallback), and an older engine
        // answers an argument it does not know with a usage error — which is
        // silence. It ignores an environment variable it does not know, and
        // speaks at full volume instead. Worse volume beats no voice.
        // The name itself lives in SpeechEngineContract, compiled into the engine
        // as well, so the two sides spell it from one place.
        internal const string SpeechVolumeEnvVar = SpeechEngineContract.VolumeEnvVar;

        // Where Windows keeps scaled chime copies. Under the temp directory
        // because every one of them can be rebuilt from its source on demand.
        internal static string ChimeCacheDirectory =>
            Path.Combine(Path.GetTempPath(), "ClaudeBuddy-chimes");

        // NaN is what a corrupt or hand-edited number turns into after
        // arithmetic, and Math.Clamp passes NaN straight through — so it is
        // answered here, as the default rather than as silence, because a
        // setting nobody meant should not be the reason Buddy goes quiet.
        public static double Clamp(double level) =>
            double.IsNaN(level) ? Default : Math.Clamp(level, Min, Max);

        // "At full volume", which is the case every backend below leaves
        // byte-for-byte alone.
        public static bool IsFull(double level) => Clamp(level) >= Max;

        // Every engine is told the level now — `say` through [[volm]], SAPI
        // through Volume, Kokoro through SpeechVolumeEnvVar, and (since
        // CB-200's second review, Warren's call) a custom speak command through
        // the same variable. So the Speech slider is never greyed out. What
        // the row still has to be honest about is the two cases where telling
        // is not the same as hearing, and it says so in a note under the
        // slider rather than by disabling it:
        //
        // - A custom command gets the level only if it reads the variable.
        //   The contract is text on stdin and an exit code; the variable is
        //   an optional extra, and a command that ignores it speaks exactly
        //   as it did before.
        // - Kokoro speaking through an older engine
        //   (NeuralSpeech.SpeaksWithFallbackEngine) — just after an upgrade,
        //   before this build's engine has downloaded, and indefinitely on a
        //   dev build whose engine was never published. An engine from before
        //   CB-200 ignores the variable. "Installed" rather than "downloaded"
        //   because the dev-build case never downloads.
        //
        // The slider stays live in both: the level is saved, and applies the
        // moment the command reads it or the right engine lands.
        public const string CustomCommandNote =
            "Sent to your command as " + SpeechEngineContract.VolumeEnvVar + "; applies only if the command reads it";

        public const string FallbackEngineNote = "Takes effect once the updated voice engine is installed";

        // The same two caveats for an orb whose own voice (a persona's) runs
        // on a different engine from the global one — which is how Warren's
        // vibe summaries went through his F5-TTS command while the global
        // engine said "system" and the row said nothing at all.
        public const string OrbCustomCommandNote =
            "An orb with its own custom-command voice gets it only if that command reads "
            + SpeechEngineContract.VolumeEnvVar;

        public const string OrbFallbackEngineNote =
            "An orb with its own Kokoro voice also waits for the updated voice engine";

        // When orbs have voices of their own but which engines they resolve to
        // is not known without enumerating every voice on the machine — two
        // process launches the settings window will not make just to draw a
        // row. Both caveats, stated as rules rather than as facts about any
        // particular orb.
        public const string OrbVoicesUnknownNote =
            "Orbs with their own voice follow the same rules: a custom command must read "
            + SpeechEngineContract.VolumeEnvVar + ", and Kokoro needs the updated voice engine";

        // What the Speech row says under its slider, or null for nothing.
        //
        // `globalEngine` is the engine the user's own voice will actually use
        // (TextToSpeech.EngineThatWillSpeak). `orbEngines` is the set of
        // engines orbs' own voices resolve to — empty when no orb has a voice
        // of its own, null when some do but the engines are not known (see
        // SessionIdentity.OrbEngines). An orb on the global engine adds
        // nothing: the global line already covers it. One line per caveat,
        // global first, so the most common case reads first.
        public static string? SpeechVolumeNote(TextToSpeech.SpeakEngine globalEngine, bool usingFallbackEngine,
            IReadOnlyCollection<TextToSpeech.SpeakEngine>? orbEngines)
        {
            var lines = new List<string>();

            if (Caveat(globalEngine, usingFallbackEngine, CustomCommandNote, FallbackEngineNote) is { } global)
            {
                lines.Add(global);
            }

            if (orbEngines is null)
            {
                lines.Add(OrbVoicesUnknownNote);
            }
            else
            {
                foreach (var engine in orbEngines.Where(e => e != globalEngine).Distinct().OrderBy(e => e))
                {
                    if (Caveat(engine, usingFallbackEngine, OrbCustomCommandNote, OrbFallbackEngineNote) is { } orb)
                    {
                        lines.Add(orb);
                    }
                }
            }

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private static string? Caveat(TextToSpeech.SpeakEngine engine, bool usingFallbackEngine,
            string customNote, string fallbackNote) =>
            engine switch
            {
                TextToSpeech.SpeakEngine.Custom => customNote,
                TextToSpeech.SpeakEngine.Neural when usingFallbackEngine => fallbackNote,
                _ => null
            };

        // The Alert volume row's description. Windows gets one more sentence
        // (CB-200 PM review): its chimes are made quieter by scaling a copy of
        // the WAV's samples, which only works for uncompressed audio, so a
        // compressed file a user picked themselves plays at full volume
        // there. Said where the slider is rather than only in the README.
        // macOS's afplay applies its own gain to any format, so the sentence
        // would be false there. A parameter rather than an OS check so both
        // arms are tested on either CI leg.
        public static string AlertVolumeHelp(bool windows) =>
            "How loud both sounds above play, previews included. Separate from the "
            + "speech volume, so a quiet chime never means a quiet voice."
            + (windows
                ? " A compressed WAV file you chose yourself can't be made quieter and plays at full volume."
                : "");

        // What the settings window shows beside a slider.
        public static string Percent(double level) =>
            ((int)Math.Round(Clamp(level) * 100)).ToString(CultureInfo.InvariantCulture) + "%";

        // A level as text for a command line or an environment variable:
        // invariant, and short. "0.5", never "0,5" — see the afplay note above
        // for what the other spelling costs.
        public static string Format(double level) =>
            Clamp(level).ToString("0.###", CultureInfo.InvariantCulture);

        // `say`'s text with the level embedded in front of it. Unchanged at
        // full volume, so nothing about today's speech moves for anyone who
        // never touches the slider.
        public static string SayText(string text, double level) =>
            IsFull(level) ? text : $"[[volm {Format(level)}]] {text}";

        // SAPI's units: an integer 0-100 that throws when out of range.
        public static int SapiVolume(double level) => (int)Math.Round(Clamp(level) * 100);

        // afplay's arguments ahead of the file — empty at full volume, so the
        // argv is exactly what it was before CB-200.
        public static IReadOnlyList<string> AfplayArguments(double level) =>
            IsFull(level) ? Array.Empty<string>() : new[] { "-v", Format(level) };

        // What the Kokoro side-car is handed, or null for "set nothing" — an
        // unset variable is how the engine has always been run.
        public static string? EngineEnvironmentValue(double level) =>
            IsFull(level) ? null : Format(level);

        // A WAV file's bytes with every sample multiplied by `level`, or null
        // when the encoding is one this does not know how to scale.
        //
        // Scaled here rather than asked of Windows because Media.SoundPlayer
        // has no volume, and the other lever — the process's own mixer session
        // via waveOutSetVolume — is remembered by Windows per executable. That
        // executable is powershell.exe, which is also what speaks, so a quiet
        // chime would have been a quiet voice for the rest of the session.
        //
        // Only the sample data changes: every header and every other chunk is
        // copied byte-for-byte, so whatever could open the original can open
        // this. Surveyed on the Windows box, all 80 files under
        // C:\Windows\Media are 16-bit PCM (79 stereo, one mono); 8/24/32-bit
        // PCM and 32-bit float are handled too because a user's own
        // "Choose file…" pick can be any of them. Anything compressed comes
        // back null and the caller plays the original — see ChimePlayer.
        public static byte[]? ScaleWav(byte[] wav, double level)
        {
            if (wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE")) return null;

            int? format = null;
            var bits = 0;
            var dataStart = -1;
            var dataLength = 0;

            // Chunks are walked rather than assumed at fixed offsets: real
            // files carry LIST/fact/bext chunks between the header and the
            // data, and a fixed offset would scale a header.
            //
            // In long, because a chunk's size is whatever the file says it is.
            // CB-200 QA: a size near int.MaxValue wrapped `body + size + pad`
            // negative in int, and the next read threw ArgumentOutOfRange out
            // of a chime — nothing above caught it. In long the walk simply
            // steps past the end of the file, finds no data chunk, and the
            // file plays unscaled like any other WAV this cannot read.
            // Offsets inside the loop are only ever used once they are known
            // to be inside the array, which is what makes the casts safe.
            long at = 12;
            while (at + 8 <= wav.Length)
            {
                var size = BitConverter.ToInt32(wav, (int)at + 4);
                if (size < 0) return null;
                var body = at + 8;

                if (Tag(wav, (int)at, "fmt ") && size >= 16 && body + 16 <= wav.Length)
                {
                    format = BitConverter.ToUInt16(wav, (int)body);
                    bits = BitConverter.ToUInt16(wav, (int)body + 14);

                    // WAVE_FORMAT_EXTENSIBLE carries the real format in the
                    // first two bytes of its SubFormat GUID.
                    if (format == 0xFFFE && size >= 40 && body + 26 <= wav.Length)
                    {
                        format = BitConverter.ToUInt16(wav, (int)body + 24);
                    }
                }
                else if (Tag(wav, (int)at, "data"))
                {
                    dataStart = (int)body;
                    dataLength = (int)Math.Min(size, wav.Length - body);
                    break;
                }

                // Chunks are word-aligned: an odd size carries a pad byte.
                at = body + size + (size & 1);
            }

            if (format is null || dataStart < 0) return null;

            var gain = Clamp(level);
            var scaled = (byte[])wav.Clone();

            switch (format, bits)
            {
                case (1, 8):
                    // 8-bit WAV is unsigned around 128.
                    for (var i = dataStart; i < dataStart + dataLength; i++)
                    {
                        scaled[i] = (byte)Math.Clamp((int)Math.Round((wav[i] - 128) * gain) + 128, 0, 255);
                    }
                    return scaled;

                case (1, 16):
                    for (var i = dataStart; i + 2 <= dataStart + dataLength; i += 2)
                    {
                        var sample = (short)Math.Round(BitConverter.ToInt16(wav, i) * gain);
                        BitConverter.TryWriteBytes(scaled.AsSpan(i, 2), sample);
                    }
                    return scaled;

                case (1, 24):
                    for (var i = dataStart; i + 3 <= dataStart + dataLength; i += 3)
                    {
                        var sample = (wav[i] | (wav[i + 1] << 8) | (wav[i + 2] << 16)) << 8 >> 8;
                        var value = (int)Math.Round(sample * gain);
                        scaled[i] = (byte)value;
                        scaled[i + 1] = (byte)(value >> 8);
                        scaled[i + 2] = (byte)(value >> 16);
                    }
                    return scaled;

                case (1, 32):
                    for (var i = dataStart; i + 4 <= dataStart + dataLength; i += 4)
                    {
                        var sample = (int)Math.Round(BitConverter.ToInt32(wav, i) * gain);
                        BitConverter.TryWriteBytes(scaled.AsSpan(i, 4), sample);
                    }
                    return scaled;

                case (3, 32):
                    for (var i = dataStart; i + 4 <= dataStart + dataLength; i += 4)
                    {
                        var sample = (float)(BitConverter.ToSingle(wav, i) * gain);
                        BitConverter.TryWriteBytes(scaled.AsSpan(i, 4), sample);
                    }
                    return scaled;

                default:
                    return null;
            }
        }

        // The file a Windows chime should actually play at `level`: the
        // source itself at full volume, a scaled copy in `cacheDirectory`
        // otherwise, or null when no copy could be made (an encoding ScaleWav
        // does not know, or a file that could not be read or written).
        //
        // Cached, and named for the source's path, size, write time and the
        // level, so a repeat chime costs a File.Exists rather than a rewrite,
        // and an edited source can never be answered from a stale copy.
        // Written beside its final name and moved into place, so a chime
        // starting while another is still writing the same copy never opens a
        // half-written file.
        public static string? ScaledCopy(string sourcePath, double level, string cacheDirectory)
        {
            if (IsFull(level)) return sourcePath;

            try
            {
                var info = new FileInfo(sourcePath);
                var identity = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
                var target = Path.Combine(cacheDirectory, $"{hash}-{SapiVolume(level)}.wav");

                if (File.Exists(target)) return target;

                var scaled = ScaleWav(File.ReadAllBytes(sourcePath), level);
                if (scaled is null) return null;

                Directory.CreateDirectory(cacheDirectory);
                var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(staging, scaled);
                File.Move(staging, target, overwrite: true);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Claude Buddy: couldn't prepare a quieter copy of a chime: {ex.Message}");
                return null;
            }
        }

        private static bool Tag(byte[] bytes, int at, string tag) =>
            at + 4 <= bytes.Length
            && bytes[at] == tag[0] && bytes[at + 1] == tag[1]
            && bytes[at + 2] == tag[2] && bytes[at + 3] == tag[3];
    }
}
