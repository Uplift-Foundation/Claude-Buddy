using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;

namespace ClaudeBuddy
{
    // The optional high-quality speech engine: a neural TTS model (Kokoro) run by
    // a separate downloaded process. See TextToSpeech, which routes to it when the
    // user has opted in, and tools/ClaudeBuddySpeech for the engine itself.
    //
    // Windows-only, and downloaded rather than shipped, for one reason each:
    //
    // Windows-only because macOS does not need it. Apple's Enhanced and Premium
    // voices are available to any app through `say`, which is what TextToSpeech
    // already uses there. Windows ships comparable voices and reserves them for
    // Narrator — its natural/HD voices register no token in either the SAPI5 or
    // Speech_OneCore hive and are invisible to System.Speech and to WinRT alike
    // (measured), so the only way to sound better on Windows is to bring a model.
    //
    // Downloaded because the engine's dependencies weigh ~82MB on disk, 66MB of
    // it phoneme lexicons for languages this never uses. Referenced from the app
    // that would have taken the installer from 35MB to ~110MB for everyone,
    // enabled or not. As a separate process it costs non-users nothing at all,
    // and it keeps the app's dependency graph and the macOS bundle untouched.
    //
    // It also keeps cancellation honest: speaking is a child process, stopping is
    // killing it, which is exactly the contract TextToSpeech has always had for
    // `say` and PowerShell. Nothing about "is it speaking" had to be rebuilt to
    // accept a completely different engine, and there is no resident memory to
    // reclaim — a process per utterance exits when the utterance does.
    internal static class NeuralSpeech
    {
        // Read from this assembly rather than written down here, so it cannot
        // drift from ClaudeBuddy.csproj's <Version> — which the README calls the
        // single source of truth for the shipped version, and which the packaging
        // scripts and the release workflow all parse out of that one element.
        //
        // The engine ships in the app's own release, under the same tag, so
        // "which engine does this build want" and "which release do I fetch it
        // from" are the same question with the same answer. A hardcoded constant
        // here would have been one more thing to remember at release time, and
        // getting it wrong means a 404 rather than a compile error.
        //
        // AssemblyInformationalVersion carries the "+<commit sha>" suffix that
        // shows up in the version string; the tag does not, so it is cut.
        private static readonly string EngineVersion = ResolveVersion();

        private static string ResolveVersion()
        {
            var informational = typeof(NeuralSpeech).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (string.IsNullOrEmpty(informational)) return "0.0.0";

            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        // The model, pinned to a tag rather than a moving branch: this exact file
        // is the one the quality was judged on, and a silently updated model is a
        // silently changed voice.
        private const string ModelUrl =
            "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx";

        // The engine bundle, built by tools/build-speech-engine.ps1 and published
        // into this version's own release beside the installer — the tag is "v" +
        // the version, which is what .github/workflows/release.yml triggers on.
        //
        // Deliberately the app's release rather than a separate speech-engine tag:
        // one release means the engine a build asks for is always the one shipped
        // alongside it, with no second tag to remember and no window where an app
        // is published against an engine that isn't. It costs re-uploading ~130MB
        // per release, which is cheap next to getting that wrong.
        private static string EngineUrl =>
            "https://github.com/Uplift-Foundation/Claude-Buddy/releases/download/"
            + $"v{EngineVersion}/ClaudeBuddySpeech-{EngineVersion}-win-x64.zip";

        private static string Root => Path.Combine(ClaudeBuddySettings.Directory, "speech-engine");

        // Voices the user added themselves, kept deliberately *outside* Root: an
        // engine upgrade deletes and replaces the whole versioned directory, so
        // anything dropped in beside the bundled voices would vanish at the next
        // release. Nothing creates this directory — it exists if someone made it,
        // and the engine ignores a path that isn't there.
        //
        // A Kokoro voice is a 510KB numpy array of style vectors for the one
        // model, so "adding a voice" really is just putting a file here. The name
        // matters: language and gender come from its prefix (af_ American female,
        // am_ American male, bf_/bm_ British), and a file named without one loads
        // but never appears in the English list.
        public static string UserVoicesDirectory =>
            Path.Combine(ClaudeBuddySettings.Directory, "voices");
        private static string ModelPath => Path.Combine(Root, "kokoro-fp16.onnx");
        private static string EnginePath => Path.Combine(Root, EngineVersion, "ClaudeBuddySpeech.exe");

        // Both halves have to be present, and they arrive separately — the engine
        // is ~150MB of executable and the model another 156MB, so a download
        // interrupted between them must not read as ready.
        public static bool Installed =>
            OperatingSystem.IsWindows() && File.Exists(EnginePath) && File.Exists(ModelPath);

        // What TextToSpeech asks before routing anything here.
        public static bool Available => Installed && ClaudeBuddySettings.NeuralVoiceEnabled;

        public static string DefaultVoiceName => "af_heart";

        private static readonly object DownloadGate = new();
        private static Task? _downloadTask;

        // Mirrors SpeechTranscriber.DownloadModelAsync deliberately, down to the
        // dedup: two quick toggles in Settings must not start two downloads that
        // write the same paths. Exceptions propagate so the caller can say
        // "couldn't download" — everything on the *speaking* path degrades
        // quietly instead, because failing to speak well should still speak.
        public static Task DownloadAsync(IProgress<string>? progress = null)
        {
            if (Installed) return Task.CompletedTask;

            lock (DownloadGate)
            {
                _downloadTask ??= DownloadCoreAsync(progress);
                return _downloadTask;
            }
        }

        private static async Task DownloadCoreAsync(IProgress<string>? progress)
        {
            try
            {
                Directory.CreateDirectory(Root);

                // Model first: it is the larger download and the one that is
                // useless on its own, so an interrupted install leaves the engine
                // missing rather than an engine that starts and finds no model.
                if (!File.Exists(ModelPath))
                {
                    progress?.Report("Downloading voice model (about 156 MB)…");
                    await DownloadFileAsync(ModelUrl, ModelPath);
                }

                if (!File.Exists(EnginePath))
                {
                    progress?.Report("Downloading speech engine (about 150 MB)…");

                    var zip = Path.Combine(Root, $"engine-{EngineVersion}.zip");
                    await DownloadFileAsync(EngineUrl, zip);

                    progress?.Report("Unpacking speech engine…");
                    var target = Path.Combine(Root, EngineVersion);

                    // Extract beside the target and rename, the same
                    // crash-safety the model download and settings writes use: a
                    // half-extracted directory containing ClaudeBuddySpeech.exe
                    // would make Installed true while the engine is unusable.
                    var staging = target + ".tmp";
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    ZipFile.ExtractToDirectory(zip, staging);

                    // Checked before the rename, because the rename is what makes
                    // Installed start returning true. The engine is useless without
                    // its voices, and KokoroSharp's voice files are exactly the
                    // thing a packaging mistake drops silently — `dotnet publish`
                    // leaves them behind entirely, which is why
                    // build-speech-engine.ps1 copies them by hand. If that ever
                    // regresses, the download should fail loudly here rather than
                    // install an engine that refuses to speak.
                    var stagedExe = Path.Combine(staging, "ClaudeBuddySpeech.exe");
                    var stagedVoices = Path.Combine(staging, "voices");

                    if (!File.Exists(stagedExe))
                    {
                        throw new InvalidDataException(
                            "the downloaded speech engine has no ClaudeBuddySpeech.exe");
                    }

                    if (!Directory.Exists(stagedVoices) ||
                        Directory.GetFiles(stagedVoices, "*.npy").Length == 0)
                    {
                        throw new InvalidDataException(
                            "the downloaded speech engine has no voices");
                    }

                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    Directory.Move(staging, target);

                    try { File.Delete(zip); } catch { /* a leftover zip is untidy, not broken */ }
                }

                progress?.Report("High-quality voice ready.");
            }
            finally
            {
                lock (DownloadGate) _downloadTask = null;
            }
        }

        private static async Task DownloadFileAsync(string url, string destination)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var temporary = destination + ".tmp";
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = File.Create(temporary))
            {
                await source.CopyToAsync(file);
            }

            File.Move(temporary, destination, overwrite: true);
        }

        // The engine's own list, asked of the engine rather than hardcoded here,
        // so the two can't drift. Empty when it isn't installed, which is what
        // makes the settings picker fall back to the system voices.
        [SupportedOSPlatform("windows")]
        public static List<string> Voices()
        {
            var voices = new List<string>();
            if (!Installed) return voices;

            try
            {
                var process = Process.Start(new ProcessStartInfo(EnginePath)
                {
                    ArgumentList = { "--list-voices", "--user-voices", UserVoicesDirectory },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (process is null) return voices;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = line.Trim();
                    if (name.Length > 0) voices.Add(name);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Claude Buddy: couldn't list neural voices: {ex.Message}");
            }

            return voices;
        }

        // Starts the engine on this text and hands the process back for
        // TextToSpeech to own — it already knows how to treat a running child as
        // "speaking" and a kill as "stop".
        //
        // Text goes in over stdin, not on the command line. An assistant turn runs
        // to 1500 characters of quotes, apostrophes, newlines and code
        // punctuation; the PowerShell path this replaces has to double every
        // apostrophe to survive being spliced into a script and got it wrong once
        // already. A pipe has no escaping rules to get wrong.
        //
        // onSpeaking fires when the engine reports that audio has actually begun.
        // There is a real wait in front of the first sound — process start, then a
        // model load, then the first segment's synthesis, measured at ~3.3s — so
        // the caller needs to distinguish "preparing" from "speaking" rather than
        // showing a stop button over silence.
        [SupportedOSPlatform("windows")]
        public static Process? Start(string text, string? voice, Action? onSpeaking)
        {
            if (!Installed) return null;

            var startInfo = new ProcessStartInfo(EnginePath)
            {
                ArgumentList =
                {
                    "--model", ModelPath,
                    "--voice", voice ?? DefaultVoiceName,
                    "--user-voices", UserVoicesDirectory
                },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Read the marker asynchronously rather than blocking: this runs on
            // whatever thread asked to speak, and the first line does not arrive
            // for seconds.
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null && e.Data.StartsWith("speaking", StringComparison.Ordinal))
                {
                    onSpeaking?.Invoke();
                }
            };

            // Drained and reported rather than ignored: the engine writes one line
            // here when it fails, and a silent failure is what "the speak button
            // does nothing" is made of. ONNX Runtime's own warnings are already
            // silenced engine-side, so anything arriving here is worth seeing.
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.Error.WriteLine($"Claude Buddy: speech engine: {e.Data}");
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.StandardInput.Write(text);
                process.StandardInput.Close();
                return process;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Claude Buddy: couldn't start the speech engine: {ex.Message}");
                try { process.Dispose(); } catch { }
                return null;
            }
        }
    }
}
