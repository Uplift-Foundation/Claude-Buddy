using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.Ggml;

namespace ClaudeBuddy
{
    // Local, offline speech-to-text for the orb's voice-dictation mic — see
    // OrbWindow and VoiceRecorder. Whisper.net (whisper.cpp bindings) against
    // a GGML model file on disk, so there is no Anthropic API call, no
    // third-party cloud STT, and no recurring cost: transcription is free
    // beyond the CPU it runs on, which is the whole point given this feature
    // has to work on a Claude subscription rather than API billing.
    //
    // English-only per the user's choice when this was designed — ggml-base.en
    // is smaller and faster than the multilingual models at the same accuracy
    // for English speech.
    internal static class SpeechTranscriber
    {
        private const GgmlType Model = GgmlType.BaseEn;

        // Cached beside settings.json rather than bundled in the installer:
        // ~150MB is too large to ship to everyone when most people will never
        // turn this on. See DownloadModelAsync for when it actually arrives.
        private static string ModelPath => Path.Combine(ClaudeBuddySettings.Directory, "ggml-base.en.bin");

        public static bool ModelDownloaded => File.Exists(ModelPath);

        private static readonly object FactoryGate = new();
        private static WhisperFactory? _factory;

        // Guards against a quick off/on/off toggle in Settings starting a
        // second download that writes the same temp file as the first —
        // later callers just await whichever download is already in flight.
        private static readonly object DownloadGate = new();
        private static Task? _downloadTask;

        // whisper.cpp's native context isn't safe for concurrent inference,
        // and every WhisperProcessor built from one factory shares that one
        // context — so only one transcription runs at a time app-wide, no
        // matter how many orbs have a mic recording right now.
        private static readonly SemaphoreSlim TranscribeGate = new(1, 1);

        // Only ever called from the Settings toggle when the user turns voice
        // input on — never from the mic click path — so a multi-hundred-MB
        // download is always something the user just asked for, not a
        // surprise the first time they hover an orb.
        public static Task DownloadModelAsync(IProgress<string>? progress = null)
        {
            if (ModelDownloaded) return Task.CompletedTask;

            lock (DownloadGate)
            {
                _downloadTask ??= DownloadModelCoreAsync(progress);
                return _downloadTask;
            }
        }

        private static async Task DownloadModelCoreAsync(IProgress<string>? progress)
        {
            try
            {
                Directory.CreateDirectory(ClaudeBuddySettings.Directory);
                progress?.Report("Downloading voice model (about 150 MB)…");

                var tempPath = ModelPath + ".tmp";
                await using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(Model))
                await using (var file = File.Create(tempPath))
                {
                    await modelStream.CopyToAsync(file);
                }

                // Rename over the target, same crash-safety reasoning as
                // ClaudeBuddySettings.Save: a process killed mid-download must
                // never leave a half-written file that ModelDownloaded reports
                // as present.
                File.Move(tempPath, ModelPath, overwrite: true);
                progress?.Report("Voice model ready.");
            }
            finally
            {
                // Failed or not, the next toggle-on should get a fresh
                // attempt rather than being stuck awaiting a dead task.
                lock (DownloadGate) _downloadTask = null;
            }
        }

        // Empty string if nothing was recognized (silence, background noise,
        // or any failure along the way) — OrbWindow treats that as a no-op
        // rather than typing nothing into someone's terminal. Never throws:
        // this runs off an async-void click handler, where an escaping
        // exception would take the whole app down over a bad audio frame.
        public static async Task<string> TranscribeAsync(float[] samples)
        {
            if (samples.Length == 0 || !ModelDownloaded) return "";

            await TranscribeGate.WaitAsync();
            try
            {
                var factory = GetFactory();
                if (factory is null) return "";

                using var processor = factory.CreateBuilder()
                    .WithLanguage("en")
                    .Build();

                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(samples))
                {
                    text.Append(segment.Text);
                }

                return ApplySpokenPunctuation(StripNonSpeechTags(text.ToString()).Trim());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Claude Buddy: voice transcription failed: {ex.Message}");
                return "";
            }
            finally
            {
                TranscribeGate.Release();
            }
        }

        // Whisper doesn't return an empty string for audio with no speech in it
        // — it returns a literal annotation token, most often "[BLANK_AUDIO]",
        // and also emits bracketed non-speech tags like "(clears throat)" or
        // "[MUSIC]" mid-transcript. Confirmed by transcribing four seconds of
        // an ordinary quiet room: the result was the six characters that would
        // otherwise have been typed straight into someone's terminal, since
        // OrbWindow's only guard on the way out is IsNullOrWhiteSpace and
        // "[BLANK_AUDIO]" passes it.
        //
        // Dropped rather than translated to anything: these are the model
        // describing the audio, never words the user said, so there is nothing
        // here a caller could want. A clip that was *only* an annotation comes
        // back empty, which is exactly the "nothing was said" signal callers
        // already handle.
        private static string StripNonSpeechTags(string text) =>
            Regex.Replace(text, @"\s*[\[\(][A-Za-z0-9 _'’-]{0,40}[\]\)]", " ");

        // Whisper is a general speech-recognition model, not a dictation
        // system — it has no notion of "spoken punctuation commands" the way
        // Apple/Windows/Dragon's dictation engines do, so saying "period"
        // just transcribes the literal word "period". This is the same
        // mapping those systems apply under the hood, done as a plain text
        // pass afterward since Whisper itself has no instruction-following
        // to lean on here — a WithPrompt() context nudges decoding style,
        // it doesn't substitute words.
        //
        // Longer phrases first: "exclamation point" has to be tried before
        // a hypothetical shorter "exclamation" entry would eat part of it
        // and leave "point" behind. Case-insensitive, whole-word only, so
        // "exposition" or "the trial period" aren't touched mid-word — but
        // "the trial period" *said as three separate words* still becomes
        // "the trial." like every other dictation system's version of this
        // trade-off; there's no way to tell "spoken as punctuation" from
        // "spoken as the actual word" from the audio alone.
        private static readonly (string Spoken, string Symbol)[] PunctuationWords =
        {
            ("question mark", "?"),
            ("exclamation point", "!"),
            ("exclamation mark", "!"),
            ("open paren", "("),
            ("close paren", ")"),
            ("open parenthesis", "("),
            ("close parenthesis", ")"),
            ("new paragraph", "\n\n"),
            ("new line", "\n"),
            ("full stop", "."),
            ("period", "."),
            ("comma", ","),
            ("colon", ":"),
            ("semicolon", ";")
        };

        // Absorbs the whitespace *before* each spoken punctuation word, not
        // after: "fix comma then" has to become "fix, then", not "fix,then"
        // or "fix , then" — dropping the leading space and leaving whatever
        // followed untouched is what gets the spacing right on both sides
        // without a separate cleanup pass.
        private static string ApplySpokenPunctuation(string text)
        {
            foreach (var (spoken, symbol) in PunctuationWords)
            {
                text = Regex.Replace(text, @$"\s*\b{Regex.Escape(spoken)}\b", symbol, RegexOptions.IgnoreCase);
            }

            // Whisper predicts its own punctuation from the *pause* in the
            // audio independently of what word was actually said there —
            // saying "period" is both a word (substituted above) and a
            // natural place to pause, so it's common for Whisper to also
            // land a real "." of its own at the same spot. The result
            // before this: doubled-up symbols scattered through the text
            // ("fix this.. then" or "fix this . . then"), not just at the
            // very end. Collapse a run of the same mark (whitespace between
            // them and all) down to one, and drop any space left dangling
            // right before a mark — leftover from Whisper's own token
            // spacing once the word that used to fill that gap is gone.
            text = Regex.Replace(text, @"\s+([.,!?;:])", "$1");
            text = Regex.Replace(text, @"([.,!?;:])(?:\s*\1)+", "$1");

            return text.Trim();
        }

        private static WhisperFactory? GetFactory()
        {
            lock (FactoryGate)
            {
                if (_factory is not null) return _factory;
                if (!ModelDownloaded) return null;

                try
                {
                    _factory = WhisperFactory.FromPath(ModelPath);
                    return _factory;
                }
                catch (Exception ex)
                {
                    // A corrupt or partial model file (e.g. a download that
                    // got interrupted before the rename-over-target above)
                    // must not crash the app — just no transcription until
                    // the user re-downloads it.
                    Console.Error.WriteLine($"Claude Buddy: couldn't load the voice model: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
