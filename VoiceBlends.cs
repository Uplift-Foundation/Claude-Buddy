using System.Diagnostics.CodeAnalysis;

namespace ClaudeBuddy
{
    // The half of a blended voice that touches a disk: reading the constituent
    // voice tensors, averaging them, and writing the result into the directory
    // the engine already loads user voices from.
    //
    // **Why the app builds the file rather than the engine mixing on the fly.**
    // The engine is version-pinned per app version and downloaded, so a new
    // engine command reaches a user only on the release after the one that
    // needs it — and every older app on every machine still could not speak a
    // blend. Nothing about the mixing needs the engine, though: a Kokoro voice
    // is a plain `.npy` of style vectors sitting in a directory, the engine
    // already takes `--user-voices <dir>` and already loads every `*.npy` it
    // finds there, and Warren had already proved the whole path by hand with
    // an `annabel_mix.npy` he built himself. So the blend is a file, written
    // once, and the engine is not asked to learn anything.
    //
    // **Written at most once per distinct blend, and never on a tick.** The
    // slug is derived from the resolved voices and their whole-percentage
    // shares, so the same blend written in the same words names the same file
    // — a file that already exists is reused without being read, and the
    // in-process table below means a repeated speak does not even stat it.
    // Nothing here runs from the persona scan; the only callers are the two
    // speak paths, which is what keeps a half-megabyte write off the UI
    // thread's two-second timer.
    //
    // **Every failure ends at the user's global voice.** A missing constituent
    // file, a directory that cannot be written, a tensor of the wrong shape —
    // all of them return null, which the caller already treats as "this
    // persona names no usable voice". A line goes into persona.log saying
    // which and why, for the same reason CB-135 put picture refusals there: a
    // blend that silently becomes the global voice is indistinguishable from a
    // persona that named no voice at all.
    internal static class VoiceBlends
    {
        // Where the constituent tensors are read from and where the result is
        // written. A record rather than two parameters threaded through
        // everything, and overridable as one, because a test that wants to
        // materialise a blend wants both pointed at its own scratch directory
        // and neither pointed at the real one.
        internal sealed record Paths(IReadOnlyList<string> Search, string Target);

        private static readonly object Gate = new();

        // Slug to the file that holds it. Keyed on the slug rather than on the
        // written text, so "50% sky and 50% nicole" and "sky 50%, nicole 50%"
        // are one entry and one file.
        private static readonly Dictionary<string, string> Made =
            new(StringComparer.OrdinalIgnoreCase);

        private static Paths? _forTests;

        // The real directories, in the order a constituent voice is looked
        // for: the engine's own bundled voices first, then anything the user
        // dropped in themselves — which is also where the result is written,
        // deliberately, because that directory is the one NeuralSpeech keeps
        // outside the versioned engine folder precisely so an upgrade does not
        // delete what is in it.
        //
        // A blend of a blend is therefore possible and is not a special case:
        // the search path includes the target, so a previously materialised
        // slug is just another voice.
        // Excluded from coverage: reads the machine's real engine layout. The
        // decision this feeds — which file a part is read from, and what
        // happens when it is not there — is Materialise, which takes its paths
        // as an argument and is tested against a scratch tree.
        [ExcludeFromCodeCoverage]
        private static Paths Production()
        {
            var search = new List<string>();

            if (NeuralSpeech.UsableEnginePath is { } engine
                && Path.GetDirectoryName(engine) is { Length: > 0 } directory)
            {
                search.Add(Path.Combine(directory, "voices"));
            }

            search.Add(NeuralSpeech.UserVoicesDirectory);

            return new Paths(search, NeuralSpeech.UserVoicesDirectory);
        }

        internal static Paths Current
        {
            get { lock (Gate) return _forTests ?? Production(); }
        }

        // The seam, matching LocalPersonas.SetForTests and
        // OpenClawSessions.SetIdentitiesForTests: without it the only place a
        // blend could be written is the user's real voices directory, which
        // holds voices they made by hand and which the engine they are
        // actually listening to reads from.
        //
        // Clearing the table is part of setting the paths rather than a second
        // call to remember: a slug cached against one scratch directory names
        // a file that is not in the next one.
        internal static void SetPathsForTests(Paths? paths)
        {
            lock (Gate)
            {
                _forTests = paths;
                Made.Clear();
            }
        }

        // The option a persona's blend speaks with, or null for the user's own
        // voice.
        //
        // A single-part blend is not materialised at all — `50% sky` is
        // `af_sky`, and writing a file that is a byte-for-byte copy of one the
        // engine already has would be a second name for one voice.
        internal static TextToSpeech.VoiceOption? Option(
            VoiceBlend.Blend blend, IEnumerable<TextToSpeech.VoiceOption> options)
        {
            var resolved = VoiceBlend.Resolve(blend, options);
            if (resolved is null) return null;
            if (resolved.IsSingleVoice) return resolved.Parts[0].Option;

            if (Materialise(resolved, Current) is null) return null;

            // Labelled as a blend rather than as a Kokoro voice, because this
            // string is what the settings picker would show beside 54 names
            // the engine shipped and one the app built.
            return new TextToSpeech.VoiceOption(
                TextToSpeech.SpeakEngine.Neural, resolved.Name, $"{resolved.Name} (Kokoro blend)");
        }

        // The file holding this blend, written if it is not there already.
        //
        // Takes its paths rather than asking for them, so the whole of the
        // decision — where a part is read from, what a missing one costs, that
        // the second call does not rewrite the file — is a test over a
        // temporary directory.
        internal static string? Materialise(VoiceBlend.Resolved resolved, Paths paths)
        {
            lock (Gate)
            {
                if (Made.TryGetValue(resolved.Name, out var already)) return already;
            }

            var destination = Path.Combine(paths.Target, resolved.Name + ".npy");

            // The cheap half of the cache, and the one that survives a
            // restart. Checked before anything is read, so the ordinary case
            // — an orb speaking for the hundredth time with a blend built
            // weeks ago — is one stat.
            if (File.Exists(destination))
            {
                Remember(resolved.Name, destination);
                return destination;
            }

            var tensors = new List<(NumpyVoices.Tensor Tensor, double Weight)>(resolved.Parts.Count);

            foreach (var part in resolved.Parts)
            {
                var source = Find(part.Option.Name, paths.Search);
                if (source is null)
                {
                    Refuse(resolved.Name, $"no voice file for {part.Option.Name}");
                    return null;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Refuse(resolved.Name, $"couldn't read {Path.GetFileName(source)}: {ex.Message}");
                    return null;
                }

                var tensor = NumpyVoices.Read(bytes, out var unreadable);
                if (tensor is null)
                {
                    Refuse(resolved.Name, $"{Path.GetFileName(source)}: {unreadable}");
                    return null;
                }

                tensors.Add((tensor, part.Weight));
            }

            var averaged = NumpyVoices.Average(tensors, out var refusal);
            if (averaged is null)
            {
                Refuse(resolved.Name, refusal!);
                return null;
            }

            try
            {
                Directory.CreateDirectory(paths.Target);

                // Written beside the destination and renamed, the same
                // crash-safety the engine download and the settings write use.
                // A half-written `.npy` in the user voices directory is not an
                // error the engine reports — it is a voice that loads and
                // speaks noise — and the file-exists check above would call it
                // done forever.
                var staging = destination + ".tmp";
                File.WriteAllBytes(staging, NumpyVoices.Write(averaged));
                File.Move(staging, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Refuse(resolved.Name, $"couldn't write {resolved.Name}.npy: {ex.Message}");
                return null;
            }

            Remember(resolved.Name, destination);
            return destination;
        }

        // The first directory that has this voice. Ordered rather than
        // merged, so a user who drops their own `af_sky.npy` into the voices
        // directory changes what a blend of it means — which is the same rule
        // the engine itself follows when it loads user voices after the
        // bundled ones.
        private static string? Find(string voice, IReadOnlyList<string> search)
        {
            foreach (var directory in search)
            {
                var candidate = Path.Combine(directory, voice + ".npy");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static void Remember(string slug, string path)
        {
            lock (Gate) Made[slug] = path;
        }

        // Not cached as a failure. A blend that could not be built because the
        // engine was mid-download should build the next time somebody clicks
        // speak, rather than staying broken for the life of the process —
        // and PersonaLog's own dedupe already stops the line being written
        // twice.
        private static void Refuse(string slug, string because) =>
            PersonaLog.Record($"voice blend {slug} not built: {because}");
    }
}
