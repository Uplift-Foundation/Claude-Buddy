using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace ClaudeBuddySpeech
{
    // Speaks text aloud with Kokoro, a local neural TTS model, and exits.
    //
    // Deliberately a short-lived process with no protocol of its own: the app
    // starts it, it speaks, it exits. Cancelling is the parent killing it, which
    // is exactly how TextToSpeech has always cancelled `say` and PowerShell, so
    // nothing about the app's speaking/stopping model has to change to accept a
    // completely different engine.
    //
    //   ClaudeBuddySpeech.exe --model <path> --voice <name>   < text on stdin
    //   ClaudeBuddySpeech.exe --list-voices
    //
    // Text arrives on stdin rather than as an argument. That is not a style
    // choice: an assistant turn runs to TranscriptReader.MaxSpokenChars (1500)
    // and contains quotes, apostrophes, newlines and code punctuation. The
    // PowerShell path this replaces has to double every apostrophe to survive
    // being spliced into a script, and got that wrong once already. A stdin pipe
    // has no escaping rules to get wrong and no length limit to hit.
    //
    // Exit codes matter to the caller: 0 means it spoke, anything else means the
    // app should fall back to the system voice within the same click, so a
    // missing model or a corrupt download degrades to worse speech rather than
    // to silence.
    internal static class Program
    {
        private const int ExitSpoke = 0;
        private const int ExitUsage = 2;
        private const int ExitNoModel = 3;
        private const int ExitFailed = 4;

        private static int Main(string[] args)
        {
            // The app pipes UTF-8 and reads UTF-8 back; being explicit avoids
            // inheriting whatever code page a console happens to have.
            Console.OutputEncoding = new UTF8Encoding(false);

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                // Everything the caller needs to diagnose goes to stderr, which
                // it captures. Never a dialog: this process has no UI and may be
                // running while the user is looking at something else.
                Console.Error.WriteLine($"ClaudeBuddySpeech: {ex.GetType().Name}: {ex.Message}");
                return ExitFailed;
            }
        }

        private static int Run(string[] args)
        {
            var listVoices = false;
            string? modelPath = null;
            string? voiceName = null;
            string? userVoicesPath = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--list-voices":
                        listVoices = true;
                        break;
                    case "--model" when i + 1 < args.Length:
                        modelPath = args[++i];
                        break;
                    case "--voice" when i + 1 < args.Length:
                        voiceName = args[++i];
                        break;
                    case "--user-voices" when i + 1 < args.Length:
                        userVoicesPath = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine($"ClaudeBuddySpeech: unexpected argument '{args[i]}'");
                        return ExitUsage;
                }
            }

            // Voices live beside the executable, loaded from disk rather than
            // from the package's own copy-to-output: KokoroSharp ships them via
            // an MSBuild Copy that produces no items, so `dotnet publish` leaves
            // them behind entirely. Measured — a published build has no voices/
            // directory at all, which would make this exit non-zero forever
            // while every `dotnet run` looked perfect. build-speech-engine.ps1
            // copies them next to the exe on purpose.
            var voicesPath = Path.Combine(AppContext.BaseDirectory, "voices");
            if (!Directory.Exists(voicesPath))
            {
                Console.Error.WriteLine($"ClaudeBuddySpeech: no voices directory at {voicesPath}");
                return ExitNoModel;
            }

            KokoroVoiceManager.LoadVoicesFromPath(voicesPath);
            LoadUserVoices(userVoicesPath);

            if (listVoices)
            {
                foreach (var voice in EnglishVoices()) Console.Out.WriteLine(voice.Name);
                return ExitSpoke;
            }

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                Console.Error.WriteLine($"ClaudeBuddySpeech: no model at '{modelPath}'");
                return ExitNoModel;
            }

            var text = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return ExitSpoke;   // nothing to say is not a failure

            var chosen = ResolveVoice(voiceName);
            if (chosen is null)
            {
                Console.Error.WriteLine("ClaudeBuddySpeech: no English voice available");
                return ExitNoModel;
            }

            // macOS deliberately does not go through KokoroSharp's own playback.
            // SpeakThroughAfplay carries the whole story of why.
            return OperatingSystem.IsMacOS()
                ? SpeakThroughAfplay(modelPath, chosen, text)
                : Speak(modelPath, chosen, text);
        }

        // Voices the user dropped in themselves, from a directory outside the
        // engine's own — an upgrade replaces the versioned engine folder wholesale,
        // so anything added beside the bundled voices would be deleted by the next
        // release. The app passes the path (see NeuralSpeech) rather than this
        // process working it out, so the engine stays ignorant of where the app
        // keeps its data.
        //
        // Added one by one through KokoroVoice.FromPath instead of a second
        // LoadVoicesFromPath call, because whether that method merges with what is
        // already loaded or replaces it is undocumented, and "replaces" would
        // silently cost the user all 54 bundled voices the moment they added one
        // of their own.
        //
        // A Kokoro voice is a 510KB numpy array of style vectors for this one
        // model — not an engine, not an audio file — so this is genuinely just
        // reading a file. Language and gender come from the name prefix (af_ for
        // American female, am_ American male, bf_/bm_ British), which is why a
        // file named without one loads but never shows up in an English list.
        private static void LoadUserVoices(string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            foreach (var file in Directory.GetFiles(path, "*.npy"))
            {
                try
                {
                    KokoroVoiceManager.Voices.Add(KokoroVoice.FromPath(file));
                }
                catch (Exception ex)
                {
                    // One unreadable file must not cost the whole feature: report
                    // it and carry on with the voices that did load.
                    Console.Error.WriteLine(
                        $"ClaudeBuddySpeech: ignoring {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        // 20 American English voices, which is two fewer than KokoroSharp reports
        // when it loads voices its own way. The missing pair, af_maple and af_sol,
        // simply have no .npy file in the package's voices directory — checked —
        // so a folder-based load like this one cannot see them wherever they do
        // come from. Recorded because "20 or 22?" otherwise looks like a filtering
        // bug in here, and it isn't one; a user who wants either can drop the file
        // into the user voices directory (see LoadUserVoices).
        private static List<KokoroVoice> EnglishVoices() =>
            KokoroVoiceManager.GetVoices(KokoroLanguage.AmericanEnglish);

        // Falls back to any English voice rather than failing, so a name that has
        // been renamed upstream, or a settings file edited by hand, still speaks.
        private static KokoroVoice? ResolveVoice(string? name)
        {
            var voices = EnglishVoices();

            if (!string.IsNullOrEmpty(name))
            {
                foreach (var voice in voices)
                {
                    if (string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase)) return voice;
                }
                Console.Error.WriteLine($"ClaudeBuddySpeech: voice '{name}' not found, using default");
            }

            return voices.Count > 0 ? voices[0] : null;
        }

        // The same streaming synthesis as Speak, with macOS playing the samples
        // instead of KokoroSharp.
        //
        // KokoroSharp plays through NAudio on Windows and falls back to OpenTK's
        // OpenAL binding everywhere else. That fallback is where macOS dies:
        // alcOpenDevice segfaults inside Apple's OpenAL framework
        // (OALDeviceMap::Add, KERN_INVALID_ADDRESS at 0x8) on a .NET worker
        // thread, deterministically, before one sample is played — and because
        // the crash is in *opening the device*, the "speaking" marker went out
        // over silence and every utterance cost ~12s of synthesis to say
        // nothing.
        //
        // Not Apple's framework being broken, which was the obvious suspicion
        // and is wrong: a C harness opens the device happily with NULL, by name,
        // from a pthread, and as the first call in the process; OpenTK opens it
        // from .NET on the main thread, on a thread-pool thread and on a
        // dedicated thread, on both 4.9.4 and the 5.0.0-pre.13 that KokoroSharp
        // actually resolves. Only KokoroSharp's own playback path reproduces it.
        // Chasing a prerelease binding through a framework Apple deprecated in
        // 10.15 buys nothing here — macOS has afplay, and "speaking is a child
        // process" is the shape this engine already has everywhere else.
        //
        // Cancelling still works because TextToSpeech kills the whole tree
        // rather than this process alone. KillTree's comment explains why it was
        // written that way, having already been bitten by a .cmd wrapper whose
        // grandchild kept talking after the tracked child died; an afplay
        // started here is exactly that grandchild, and dies with us.
        private static int SpeakThroughAfplay(string modelPath, KokoroVoice voice, string text)
        {
            using var synth = KokoroWavSynthesizer.LoadModel(modelPath, SessionOptionsForBackgroundUse());

            // Same reasoning as Speak: a whole turn is up to ~100 seconds of
            // audio synthesised at roughly half real time, so waiting for all of
            // it would mean the better part of a minute of silence after the
            // click. Measured on this path: the first segment arrives at 989ms
            // carrying 2.02s of audio, which is comfortably ahead of playback.
            var config = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig
            {
                MaxFirstSegmentLength = 60
            });

            // Synthesis hands segments over as they finish; one consumer plays
            // them in order. A queue rather than playing from the callback
            // because afplay is synchronous — playing inline would block the
            // synthesiser between segments and give away the head start that
            // streaming just bought.
            var segments = new BlockingCollection<float[]>(new ConcurrentQueue<float[]>());
            var announced = 0;
            var failed = false;

            var player = Task.Run(() =>
            {
                var index = 0;

                foreach (var samples in segments.GetConsumingEnumerable())
                {
                    // Written to a file rather than piped to afplay's stdin: it
                    // reads a *file*, and a WAV header has to state its data
                    // length up front, which a stream being synthesised does not
                    // know yet.
                    var path = Path.Combine(
                        Path.GetTempPath(),
                        $"claudebuddy-speech-{Environment.ProcessId}-{index++}.wav");

                    try
                    {
                        File.WriteAllBytes(path, WavBytes(samples));

                        using var afplay = Process.Start(new ProcessStartInfo("/usr/bin/afplay")
                        {
                            ArgumentList = { path },
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        });

                        if (afplay is null)
                        {
                            Console.Error.WriteLine("ClaudeBuddySpeech: could not start afplay");
                            failed = true;
                            return;
                        }

                        // Announced here, not when synthesis starts: this is the
                        // first moment sound is actually being made, and the
                        // marker existing at all is so the orb can tell
                        // "preparing" from "speaking". Printing it any earlier is
                        // what made the OpenAL crash look like a silent success.
                        if (Interlocked.Exchange(ref announced, 1) == 0)
                        {
                            Console.Out.WriteLine("speaking");
                            Console.Out.Flush();
                        }

                        afplay.WaitForExit();

                        if (afplay.ExitCode != 0)
                        {
                            Console.Error.WriteLine(
                                $"ClaudeBuddySpeech: afplay exited {afplay.ExitCode}");
                            failed = true;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"ClaudeBuddySpeech: playback failed: {ex.Message}");
                        failed = true;
                        return;
                    }
                    finally
                    {
                        // A kill from the parent takes the whole tree and skips
                        // this, so the temp directory can collect one file per
                        // interrupted segment. They are named per pid and reaped
                        // by macOS; leaking a few WAVs is better than holding the
                        // audio in memory to avoid them.
                        try { File.Delete(path); } catch { /* untidy, not broken */ }
                    }
                }
            });

            // This overload returns as soon as the job is queued — the segments
            // arrive later on KokoroSharp's own engine thread — so the wait has
            // to be on the completion callback. Closing the queue on the way out
            // of the call instead marks it complete before the first segment is
            // ever produced, and the synthesiser then throws into a thread
            // nobody is catching on. Same blocking shape as Speak: the process
            // exists to say one thing and die, and a kill from the parent is
            // what interrupts it.
            using var finished = new ManualResetEventSlim(false);

            synth.Synthesize(text, voice, AcceptSegment, () => finished.Set(), config);
            finished.Wait();

            // Guarded rather than handing `segments.Add` over directly. Closing
            // the queue below assumes no segment arrives after OnComplete, which
            // is what was observed — but this callback runs on KokoroSharp's own
            // thread, where an exception is nobody's to catch and would take the
            // process down mid-sentence. If that assumption is ever wrong, losing
            // a trailing segment is the better way to be wrong.
            void AcceptSegment(float[] samples)
            {
                try { segments.Add(samples); }
                catch (InvalidOperationException) { /* queue already closed */ }
            }

            // Only now: OnComplete fires after the last segment has been handed
            // over, so this cannot cut one off.
            segments.CompleteAdding();
            player.Wait();

            Console.Out.Flush();
            Console.Error.Flush();

            // Left through _exit rather than by returning, because shutting this
            // process down cleanly is what kills it. Once every sample has been
            // played, tearing the synthesiser and its ONNX session down throws
            // out of native code — libc++abi reports "mutex lock failed: Invalid
            // argument" — and the runtime turns that into exit 134. The caller
            // reads any non-zero exit as "the engine failed" and speaks the whole
            // turn again in the system voice, so a crash on the way out is heard
            // by the user as the paragraph twice.
            //
            // Environment.Exit is not enough: it still runs managed shutdown,
            // which is where the abort happens. Traced to be sure of the
            // ordering rather than assumed — OnComplete, then every afplay
            // exiting 0, then the player thread joining, and only then the
            // abort. So the work is genuinely finished and the only thing left
            // is to stop existing.
            //
            // _exit(2) is the syscall that does exactly that: no atexit
            // handlers, no finalizers, no native destructors. Safe precisely
            // because this process is a short-lived side-car — both streams are
            // flushed above, the temp WAVs are deleted, afplay has exited, and
            // the model is memory the kernel is about to reclaim wholesale.
            // Fixing the disposal itself belongs upstream in KokoroSharp.
            SysExit(failed ? ExitFailed : ExitSpoke);
            return failed ? ExitFailed : ExitSpoke;   // unreachable; keeps the compiler happy
        }

        // Terminates immediately, skipping every exit handler and destructor the
        // runtime would otherwise run. See the call site for why that is the
        // right way out of a finished utterance on macOS.
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
        private static extern void SysExit(int status);

        // Kokoro emits 24kHz mono float samples; afplay wants a container. The
        // header is 44 bytes of well-specified WAV and writing it by hand is
        // smaller than taking a dependency to do it — KokoroWavSynthesizer's own
        // SaveAudioToFile is no use here because the streaming callback hands
        // over float[] rather than the byte[] that method expects. (Its
        // *blocking* overload returns headerless PCM despite the name, which is
        // worth knowing before trusting it: measured, the bytes begin with
        // samples, not "RIFF".)
        private static byte[] WavBytes(float[] samples)
        {
            const int SampleRate = 24000;
            const int Channels = 1;
            const int BitsPerSample = 16;

            var dataBytes = samples.Length * sizeof(short);

            using var buffer = new MemoryStream(44 + dataBytes);
            using var writer = new BinaryWriter(buffer);

            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);                                             // PCM chunk size
            writer.Write((short)1);                                       // PCM, uncompressed
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * Channels * BitsPerSample / 8);      // byte rate
            writer.Write((short)(Channels * BitsPerSample / 8));          // block align
            writer.Write((short)BitsPerSample);
            writer.Write("data"u8);
            writer.Write(dataBytes);

            // Clamped before scaling. Kokoro occasionally returns samples a
            // shade outside [-1, 1], and letting those wrap round the short
            // boundary turns a loud syllable into a burst of noise.
            foreach (var sample in samples)
            {
                var clamped = Math.Clamp(sample, -1f, 1f);
                writer.Write((short)(clamped * short.MaxValue));
            }

            writer.Flush();
            return buffer.ToArray();
        }

        private static int Speak(string modelPath, KokoroVoice voice, string text)
        {
            using var tts = KokoroTTS.LoadModel(modelPath, SessionOptionsForBackgroundUse());

            // Segmented, streaming synthesis. Not an optimisation — a whole
            // 1500-character turn is up to ~100 seconds of audio, and measured
            // synthesis runs at roughly half real time, so synthesising it all
            // before making a sound would mean the better part of a minute of
            // silence after the button press. A short first segment starts the
            // audio in well under a second; the rest is synthesised while it
            // plays.
            var config = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig
            {
                MaxFirstSegmentLength = 60
            });

            // Nothing here is async-await: the process exists to do one thing and
            // then die, so blocking the main thread until the callbacks fire is
            // both the simplest and the most predictable shape. A kill from the
            // parent is what interrupts it.
            using var finished = new ManualResetEventSlim(false);
            var canceled = false;

            var handle = tts.SpeakFast(text, voice, config);

            // One line on stdout the moment audio actually begins. The caller
            // watches for it to tell "still preparing" from "now speaking" —
            // there is a real wait in front of the first sound (process start,
            // then a model load, then the first segment's synthesis) and the
            // orb's speak button has to be able to show which of the two states
            // it is in. Flushed explicitly because stdout to a pipe is buffered,
            // and a marker that arrives with the process exit is worthless.
            handle.OnSpeechStarted += _ =>
            {
                Console.Out.WriteLine("speaking");
                Console.Out.Flush();
            };

            handle.OnSpeechCompleted += _ => finished.Set();
            handle.OnSpeechCanceled += _ => { canceled = true; finished.Set(); };

            finished.Wait();

            // StopPlayback before Dispose: the audio device is drained by the
            // playback thread, and disposing the engine out from under it is how
            // a clipped final word happens.
            tts.StopPlayback();

            return canceled ? ExitFailed : ExitSpoke;
        }

        // ONNX Runtime uses every core it can find by default. Measured on a
        // 16-core machine, six seconds of audio cost 16.7 core-seconds spread
        // over 7.6 cores — for a background utility reading a paragraph, that is
        // a machine-wide stutter nobody asked for. Capped, the same work is 5.9
        // core-seconds over 2 cores and 0.7s slower, which is invisible because
        // synthesis is streamed and overlaps playback anyway.
        //
        // Two threads specifically, not a fraction of the core count. Measured at
        // two: 0.99 core-seconds per second of audio, synthesising at roughly
        // half real time — comfortably ahead of playback, so streaming never
        // starves. A first attempt scaled with the machine and gave four threads
        // on this 16-core box, which cost 21 core-seconds for one utterance
        // against 11 for the same work at two, and bought only a little latency
        // that streaming already hides. One thread below four cores, because
        // taking half of a dual-core machine for a background utility is worse
        // than starting a moment later.
        private static SessionOptions SessionOptionsForBackgroundUse() => new()
        {
            IntraOpNumThreads = Environment.ProcessorCount >= 4 ? 2 : 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,

            // Errors only. At its default level ONNX Runtime writes a warning per
            // graph node it cannot constant-fold while loading this model —
            // measured at over 20KB of stderr for a single utterance, all of it
            // describing an optimisation it declined to make. The parent captures
            // stderr to diagnose a failed engine, so leaving this at the default
            // would bury the one line that matters under hundreds that don't.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
    }
}
