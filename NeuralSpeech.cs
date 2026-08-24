using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;


namespace ClaudeBuddy
{
    // The optional high-quality speech engine: a neural TTS model (Kokoro) run by
    // a separate downloaded process. See TextToSpeech, which routes to it when the
    // user has opted in, and tools/ClaudeBuddySpeech for the engine itself.
    //
    // Downloaded rather than shipped because the engine's dependencies weigh
    // ~82MB on disk, 66MB of it phoneme lexicons for languages this never uses.
    // Referenced from the app that would have taken the installer from 35MB to
    // ~110MB for everyone, enabled or not. As a separate process it costs
    // non-users nothing at all.
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
        private static readonly string EngineVersion = ResolveVersion(
            typeof(NeuralSpeech).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        // The attribute value is passed in rather than read here, so both arms
        // are reachable: the assembly this runs in always has an informational
        // version, so the missing-attribute fallback could never otherwise be
        // exercised — and that fallback is what decides the URL a build fetches
        // its engine from.
        internal static string ResolveVersion(string? informational)
        {
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

        // Asked of the running process rather than assumed, because this repo
        // ships an osx-x64 DMG as well as an osx-arm64 one: an Intel Mac told to
        // fetch the arm64 engine downloads 130MB and is then killed on exec, and
        // an arm64 Mac running the x64 app under Rosetta needs the x64 engine to
        // match the process it is started from. ProcessArchitecture answers both
        // — it reports what this build actually is, not what the hardware could
        // run — and the release workflow builds the engine in the same
        // rid matrix as the DMGs so both exist for every tag.
        private static string EngineRid =>
            OperatingSystem.IsMacOS()
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : "win-x64";

        private static string EngineUrl =>
            "https://github.com/Uplift-Foundation/Claude-Buddy/releases/download/"
            + $"v{EngineVersion}/ClaudeBuddySpeech-{EngineVersion}-{EngineRid}.zip";

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
        // am_ American male, bf_/bm_ British), all of which the engine lists. A
        // prefix naming some other language is what hides a voice — zf_ is filed
        // under Mandarin and filtered out — while a name with no recognisable
        // prefix falls through to the American English list and shows up
        // normally.
        public static string UserVoicesDirectory =>
            Path.Combine(ClaudeBuddySettings.Directory, "voices");
        private static string ModelPath => Path.Combine(Root, "kokoro-fp16.onnx");
        private static string EngineExeName =>
            OperatingSystem.IsWindows() ? "ClaudeBuddySpeech.exe" : "ClaudeBuddySpeech";

        private static string EnginePath => Path.Combine(Root, EngineVersion, EngineExeName);

        // The engine actually used to speak: the one this build asks for, or
        // failing that the newest other version still on disk.
        //
        // Why a fallback exists at all. The directory is keyed by the app's
        // version, which is right — it guarantees a build runs the engine
        // published beside it, and a shared path would leave an engine from an
        // older release in use forever, since nothing but a missing file ever
        // triggers a download. But keying it that way with no fallback meant a
        // version change *deleted the feature*: a 0.3.0-beta build looked for its
        // own directory, found only the 0.2.0-beta one already on disk, reported
        // "not installed", and dropped every neural voice out of the picker with
        // no explanation and no prompt. Observed on a real machine, and only
        // because someone noticed their voice was gone — the failure is silent by
        // construction, since speaking falls back to a system voice that works.
        //
        // Nobody hit this on a released build: the engine first shipped in
        // 0.3.0-beta, so the only 0.2.0-beta installs were built from source
        // before the release. The upgrade after this one is when it would have
        // started happening to everyone.
        //
        // An engine one release old still speaks. Its contract with the app is
        // four things — text on stdin, "speaking" on stdout, --list-voices,
        // --user-voices — none of which has changed, and if one ever does, the
        // right engine is already downloading by then. So the fallback is the
        // conservative choice, not the risky one: the alternative is silence.
        private static string? UsableEnginePath =>
            File.Exists(EnginePath) ? EnginePath : NewestOtherEngine();

        private static string? NewestOtherEngine()
        {
            try
            {
                if (!Directory.Exists(Root)) return null;

                return Directory.EnumerateDirectories(Root)
                    .Where(directory => File.Exists(Path.Combine(directory, EngineExeName)))
                    .OrderByDescending(directory => Path.GetFileName(directory), VersionOrder)
                    .Select(directory => Path.Combine(directory, EngineExeName))
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                // A directory we cannot enumerate is the same as no fallback:
                // speaking degrades, it does not fail.
                Console.Error.WriteLine($"Claude Buddy: couldn't scan for a fallback engine: {ex.Message}");
                return null;
            }
        }

        // Newest by version number, not by string. An ordinal sort puts
        // "0.10.0-beta" *below* "0.2.0-beta", which would pick a year-old engine
        // over last month's the first time the minor version reaches double
        // digits — a bug that would lie dormant until 0.10 and then look like
        // anything but a sort order.
        internal static readonly IComparer<string> VersionOrder =
            Comparer<string>.Create((left, right) =>
            {
                static Version Parse(string name)
                {
                    var dash = name.IndexOf('-');          // "0.3.0-beta" -> "0.3.0"
                    var core = dash < 0 ? name : name[..dash];
                    return Version.TryParse(core, out var version) ? version : new Version(0, 0);
                }

                var byVersion = Parse(left).CompareTo(Parse(right));

                // Same numeric version, different prerelease tag: fall back to
                // the string so the order is at least stable rather than
                // arbitrary.
                return byVersion != 0
                    ? byVersion
                    : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });

        // Both halves have to be present, and they arrive separately — the engine
        // is ~150MB of executable and the model another 156MB, so a download
        // interrupted between them must not read as ready.
        // "The engine this build wants is on disk." This is the download's
        // question, deliberately still exact-version: a fallback being usable is
        // no reason to stop fetching the right one.
        public static bool Installed =>
            File.Exists(EnginePath) && File.Exists(ModelPath);

        // "Something here can speak." This is the *speaking* question, and the
        // two are different whenever a version has just changed.
        public static bool Usable =>
            UsableEnginePath is not null && File.Exists(ModelPath);

        // An engine is installed, but not the one this build asks for — so a
        // download is worth starting even though nothing looks broken to the
        // user. Distinct from `!Installed` alone, which is also true of a machine
        // that has never enabled the feature and must not be downloaded to
        // unasked.
        public static bool NeedsUpdate => !Installed && Usable;

        // What TextToSpeech asks before routing anything here. Usable rather than
        // Installed, so a version bump costs an older engine for a few minutes
        // instead of costing the user their voice.
        public static bool Available => Usable && ClaudeBuddySettings.NeuralVoiceEnabled;

        public static string DefaultVoiceName => "af_heart";

        private static readonly object DownloadGate = new();
        private static Task? _downloadTask;

        // Mirrors SpeechTranscriber.DownloadModelAsync deliberately, down to the
        // dedup: two quick toggles in Settings must not start two downloads that
        // write the same paths. Exceptions propagate so the caller can say
        // "couldn't download" — everything on the *speaking* path degrades
        // quietly instead, because failing to speak well should still speak.
        // Excluded from coverage: downloads the Kokoro engine over the network.
        [ExcludeFromCodeCoverage]
        public static Task DownloadAsync(IProgress<string>? progress = null)
        {
            if (Installed) return Task.CompletedTask;

            lock (DownloadGate)
            {
                _downloadTask ??= DownloadCoreAsync(progress);
                return _downloadTask;
            }
        }

        // Excluded from coverage: fetches and unpacks an engine archive from a
        // remote host.
        [ExcludeFromCodeCoverage]
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
                    var stagedExe = Path.Combine(staging, EngineExeName);
                    var stagedVoices = Path.Combine(staging, "voices");

                    if (!File.Exists(stagedExe))
                    {
                        throw new InvalidDataException(
                            $"the downloaded speech engine has no {EngineExeName}");
                    }

                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(stagedExe,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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

                    // Only now that the right engine is in place, because until
                    // the Move above an older one was the only thing that could
                    // speak.
                    RemoveSupersededEngines();
                }

                progress?.Report("High-quality voice ready.");
            }
            finally
            {
                lock (DownloadGate) _downloadTask = null;
            }
        }

        // Every engine directory that isn't this build's, deleted once this
        // build's is in place. Nothing did this before, so each release left
        // ~188MB of dead engine in %APPDATA% forever — two of them on the machine
        // where the version bug was found, plus the 157MB model, for 531MB of
        // which 188MB was reachable by nothing.
        //
        // Safe to be indiscriminate here for one reason worth stating: voices the
        // user added live in UserVoicesDirectory, deliberately outside Root. That
        // decision was made so an upgrade couldn't eat them, and this is the code
        // it was made for.
        //
        // Failures are ignored on purpose. A directory that won't delete — a file
        // locked by a speech process that is still exiting, most likely — costs
        // disk, and disk is not worth failing an install that otherwise
        // succeeded. The next update tries again.
        // Excluded from coverage: deletes real engine directories from disk.
        [ExcludeFromCodeCoverage]
        private static void RemoveSupersededEngines()
        {
            try
            {
                if (!Directory.Exists(Root)) return;

                foreach (var directory in Directory.EnumerateDirectories(Root))
                {
                    var name = Path.GetFileName(directory);
                    if (string.Equals(name, EngineVersion, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"Claude Buddy: couldn't remove the old speech engine {name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Claude Buddy: couldn't tidy old speech engines: {ex.Message}");
            }
        }

        // Called at startup. Fetches the engine for this build when an older one
        // is already installed, and does nothing at all otherwise — in
        // particular, nothing on a machine that has never enabled the feature,
        // which must never be handed a 300MB download it didn't ask for.
        //
        // Silent rather than prompted, deliberately. The user opted into this
        // feature and to a larger download than this one; asking again on the
        // launch after an upgrade is a dialog with no decision behind it, and
        // "yes" is the only answer that leaves the feature working. Settings shows
        // progress if it happens to be open, and speech keeps working from the
        // older engine throughout either way.
        //
        // Fire-and-forget by contract: the returned task completes when the
        // download does, and the caller uses that only to refresh the voice list.
        // A failure is logged and left — the older engine still speaks, and the
        // next launch tries again.
        // Excluded from coverage: triggers the network download above.
        [ExcludeFromCodeCoverage]
        public static Task EnsureCurrentAsync()
        {
            if (!ClaudeBuddySettings.NeuralVoiceEnabled) return Task.CompletedTask;

            // Already current: nothing to fetch, but this is the moment to
            // reclaim anything a previous version left behind. Without this the
            // tidy-up only ever runs as part of an install, so a machine that
            // upgraded before this fix existed keeps carrying its orphan until
            // the *next* release happens to download something.
            if (Installed)
            {
                RemoveSupersededEngines();
                return Task.CompletedTask;
            }

            if (!NeedsUpdate) return Task.CompletedTask;

            return DownloadAsync().ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Console.Error.WriteLine(
                        "Claude Buddy: couldn't update the speech engine for this version; "
                        + $"still using an older one. {task.Exception?.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
        }

        // Excluded from coverage: an HTTP GET to a remote host.
        [ExcludeFromCodeCoverage]
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
        // Excluded from coverage: runs the side-car engine to enumerate itself.
        [ExcludeFromCodeCoverage]
        public static List<string> Voices()
        {
            var voices = new List<string>();

            // Whichever engine can run, not only the matching one: this is what
            // the settings picker is built from, so gating it on the exact
            // version is what made the voices vanish on a version bump.
            var engine = UsableEnginePath;
            if (engine is null || !File.Exists(ModelPath)) return voices;

            try
            {
                var process = Process.Start(new ProcessStartInfo(engine)
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
        // Excluded from coverage: starts the side-car engine process.
        [ExcludeFromCodeCoverage]
        public static Process? Start(string text, string? voice, Action? onSpeaking)
        {
            var engine = UsableEnginePath;
            if (engine is null || !File.Exists(ModelPath)) return null;

            var startInfo = new ProcessStartInfo(engine)
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
