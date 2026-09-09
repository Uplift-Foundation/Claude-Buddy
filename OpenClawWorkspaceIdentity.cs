namespace ClaudeBuddy
{
    // The gateway's identity is the published, portable answer; these files are
    // a local refinement for people who keep an agent's personality beside its
    // work.  Do not turn the workspace into a second network surface: every
    // path accepted here is proved to remain inside its canonical root.
    internal static class OpenClawWorkspaceIdentity
    {
        internal sealed record Metadata(string? Name, string? Voice, double? Rate, byte[]? Avatar)
        {
            internal bool IsEmpty => Name is null && Voice is null && Avatar is null;
        }

        // The engine can run anywhere from half to double real-time speech
        // without the model itself starting to garble — this is a caution
        // against a profile typo (a rate of "13" meant as "1.3") reaching the
        // engine as a wildly wrong value, not a claim about where it stops
        // sounding good.
        private const double MinRate = 0.5;
        private const double MaxRate = 2.0;

        private const long MaxAvatarBytes = 2 * 1024 * 1024;

        internal static Metadata Read(string? workspace)
        {
            var root = CanonicalDirectory(workspace);
            if (root is null) return new Metadata(null, null, null, null);

            try
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(FileOrder, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string? name = null;
                string? voice = null;
                double? rate = null;
                byte[]? avatar = null;

                foreach (var file in files)
                {
                    var resolved = CanonicalFile(file);
                    if (resolved is null || !IsWithin(root, resolved)) continue;

                    var fields = Parse(File.ReadAllLines(resolved));
                    name ??= fields.Name;
                    voice ??= fields.Voice;
                    rate ??= fields.Rate;
                    avatar ??= AvatarAt(root, fields.Avatar);
                }

                return new Metadata(name, voice, rate, avatar);
            }
            catch (IOException) { return new Metadata(null, null, null, null); }
            catch (UnauthorizedAccessException) { return new Metadata(null, null, null, null); }
            catch (ArgumentException) { return new Metadata(null, null, null, null); }
            catch (NotSupportedException) { return new Metadata(null, null, null, null); }
        }

        // OpenClaw's IDENTITY.md format is deliberately Markdown, not a second
        // config language: `- Name: Aurora`.  Keep this grammar equally small
        // for Voice so prose mentioning "voice:" cannot silently change speech.
        internal static Fields Parse(IEnumerable<string> lines)
        {
            string? name = null;
            string? voice = null;
            double? rate = null;
            string? avatar = null;
            var inFrontMatter = false;
            var sawContent = false;

            var source = lines.ToList();
            for (var index = 0; index < source.Count; index++)
            {
                var line = source[index];
                var trimmed = line.Trim();
                if (!sawContent && trimmed.Length == 0) continue;
                if (!sawContent && trimmed == "---")
                {
                    sawContent = true;
                    inFrontMatter = true;
                    continue;
                }
                sawContent = true;
                if (inFrontMatter && trimmed == "---")
                {
                    inFrontMatter = false;
                    continue;
                }

                if (inFrontMatter && FieldAfterColon(trimmed, out var yamlLabel, out var yamlValue)
                    && VoiceLabel(yamlLabel) && VoiceValue(yamlValue) is var (yamlVoice, yamlRate) && yamlVoice is not null)
                {
                    voice ??= yamlVoice;
                    rate ??= yamlRate;
                    continue;
                }

                if (TableField(trimmed, out var tableLabel, out var tableValue)
                    && (index + 1 >= source.Count || !TableSeparator(source[index + 1]))
                    && VoiceLabel(tableLabel) && VoiceValue(tableValue) is var (tableVoice, tableRate) && tableVoice is not null)
                {
                    voice ??= tableVoice;
                    rate ??= tableRate;
                    continue;
                }

                if (BoldField(trimmed, out var boldLabel, out var boldValue)
                    && VoiceLabel(boldLabel) && VoiceValue(boldValue) is var (boldVoice, boldRate) && boldVoice is not null)
                {
                    voice ??= boldVoice;
                    rate ??= boldRate;
                    continue;
                }

                if (!trimmed.StartsWith("-", StringComparison.Ordinal)) continue;

                // A bullet can carry its own bold label — `- **Voice:** Ava` is
                // exactly as common in a real profile as the bare `- Voice:
                // Ava` below, and every real fixture this parser was built
                // from happens to use it. BoldField understands where the
                // closing `**` actually falls (after the colon, not before
                // it); FieldAfterColon does not, and used to leave it sitting
                // in the value — every bulleted-bold field, not just Voice,
                // read back with a stray "** " on the front of it.
                var rest = trimmed[1..].TrimStart();
                string label, value;
                if (!BoldField(rest, out label, out value))
                {
                    if (!FieldAfterColon(rest, out label, out value) || !Valid(value)) continue;
                }
                else if (!Valid(value)) continue;

                if (name is null && string.Equals(label, "Name", StringComparison.OrdinalIgnoreCase))
                    name = value;
                else if (voice is null && VoiceLabel(label) && VoiceValue(value) is var (bulletVoice, bulletRate) && bulletVoice is not null)
                {
                    voice = bulletVoice;
                    rate ??= bulletRate;
                }
                else if (avatar is null && string.Equals(label, "Avatar", StringComparison.OrdinalIgnoreCase))
                    avatar = value;
            }

            return new Fields(name, voice, rate, avatar);
        }

        internal sealed record Fields(string? Name, string? Voice, double? Rate, string? Avatar);

        private static bool FieldAfterColon(string text, out string label, out string value)
        {
            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                label = value = "";
                return false;
            }

            label = text[..colon].Trim().Trim('*').Trim();
            value = text[(colon + 1)..].Trim();
            return label.Length > 0;
        }

        // A bold standalone field is intentional Markdown metadata; ordinary
        // prose with a colon is not. Both common bold spellings are accepted:
        // **Voice**: Ava and **Voice:** Ava.
        private static bool BoldField(string text, out string label, out string value)
        {
            if (!text.StartsWith("**", StringComparison.Ordinal))
            {
                label = value = "";
                return false;
            }

            var close = text.IndexOf("**", 2, StringComparison.Ordinal);
            if (close < 2) { label = value = ""; return false; }

            var labelInsideBold = text[2..close].Trim();
            label = labelInsideBold.TrimEnd(':').Trim();
            var rest = text[(close + 2)..].TrimStart();
            if (labelInsideBold.EndsWith(":", StringComparison.Ordinal)) value = rest;
            else if (rest.StartsWith(":", StringComparison.Ordinal)) value = rest[1..].Trim();
            else { value = ""; return false; }
            return label.Length > 0;
        }

        private static bool TableField(string text, out string label, out string value)
        {
            var cells = text.Trim().Trim('|').Split('|');
            if (cells.Length != 2) { label = value = ""; return false; }
            label = cells[0].Trim().Trim('*').Trim();
            value = cells[1].Trim();
            return label.Length > 0;
        }

        private static bool TableSeparator(string text)
        {
            var cells = text.Trim().Trim('|').Split('|');
            return cells.Length >= 2 && cells.All(cell =>
            {
                var marker = cell.Trim();
                return marker.Length >= 3 && marker.Contains('-')
                    && marker.All(c => c is '-' or ':');
            });
        }

        private static bool VoiceLabel(string label) =>
            label.Equals("Voice", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Voice Name", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Speech Voice", StringComparison.OrdinalIgnoreCase)
            || label.Equals("TTS Voice", StringComparison.OrdinalIgnoreCase);

        // Profiles often make the engine helpful to readers: `**Voice:**
        // af_bella (Kokoro TTS)`.  The parenthesis is not part of Kokoro's
        // identifier, but parentheses are part of several system-voice names
        // (for example "Ava (Premium)").  Strip only annotations which name an
        // engine, not every parenthesised suffix.
        //
        // A profile is also free to qualify that engine further — `(Kokoro
        // TTS, rate 1.3)` — and that qualifier is exactly as much not-part-
        // of-the-voice-identifier as the engine name itself. Recognising only
        // the token before the first comma means the engine name still has to
        // match the known list; whatever rides along after it is read for a
        // rate but never has to be understood to be stripped.
        private static (string? Voice, double? Rate) VoiceValue(string value)
        {
            var candidate = value.Trim();
            double? rate = null;
            if (candidate.EndsWith(")", StringComparison.Ordinal))
            {
                var open = candidate.LastIndexOf('(');
                if (open > 0)
                {
                    var annotation = candidate[(open + 1)..^1];
                    var engine = annotation.Split(',', 2);
                    if (VoiceEngineAnnotation(engine[0]))
                    {
                        candidate = candidate[..open].TrimEnd();
                        if (engine.Length > 1) rate = RateIn(engine[1]);
                    }
                }
            }

            // A code span around the identifier itself — `` `af_nicole` `` —
            // is real fixture, not a coincidence: every voice line captured
            // from a real profile uses it. Stripped only when both ticks are
            // there and there is something left between them, so a value that
            // is nothing *but* a lone backtick is left alone rather than
            // emptied.
            if (candidate.Length > 2 && candidate.StartsWith('`') && candidate.EndsWith('`'))
                candidate = candidate[1..^1].Trim();

            return Valid(candidate) ? (candidate, rate) : (null, null);
        }

        private static bool VoiceEngineAnnotation(string annotation)
        {
            var normalized = annotation.Trim().ToLowerInvariant();
            return normalized is "kokoro" or "kokoro tts" or "neural" or "neural tts"
                or "system" or "system voice" or "custom" or "custom voice" or "tts";
        }

        // "rate 1.3", "speed 1.3x", "Rate: 1.3" — a keyword, then the first
        // number after it. Out of bounds or missing entirely is not an error:
        // the caller already has a voice, and a rate nobody can parse just
        // means the engine's own default speed, same as no rate at all.
        private static double? RateIn(string qualifier)
        {
            var words = qualifier.Split(new[] { ' ', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].ToLowerInvariant() is not ("rate" or "speed"))
                    continue;

                for (var j = i + 1; j < words.Length; j++)
                {
                    var token = words[j].TrimEnd('x', 'X');
                    if (double.TryParse(token, System.Globalization.NumberStyles.AllowDecimalPoint,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        && parsed >= MinRate && parsed <= MaxRate)
                    {
                        return parsed;
                    }
                }
            }

            return null;
        }

        private static bool Valid(string value) =>
            !string.IsNullOrWhiteSpace(value) && !IsPlaceholder(value);

        private static string FileOrder(string path)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, "IDENTITY.md", StringComparison.OrdinalIgnoreCase)) return "0";
            if (string.Equals(name, "SOUL.md", StringComparison.OrdinalIgnoreCase)) return "1";
            return "2" + name;
        }

        private static bool IsPlaceholder(string value) =>
            value.StartsWith("<", StringComparison.Ordinal) && value.EndsWith(">", StringComparison.Ordinal);

        private static byte[]? AvatarAt(string root, string? avatar)
        {
            if (string.IsNullOrWhiteSpace(avatar) || Path.IsPathRooted(avatar)) return null;

            try
            {
                var combined = Path.GetFullPath(Path.Combine(root, avatar));
                if (!IsWithin(root, combined) || EscapesThroughLink(root, combined)) return null;

                var candidate = CanonicalFile(combined);
                if (candidate is null || !IsWithin(root, candidate)) return null;

                var info = new FileInfo(candidate);
                return info.Length is > 0 and <= MaxAvatarBytes ? File.ReadAllBytes(candidate) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static string? CanonicalDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var directory = new DirectoryInfo(Path.GetFullPath(path));
                if (!directory.Exists) return null;
                return Trim((directory.ResolveLinkTarget(true) ?? directory).FullName);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static string? CanonicalFile(string path)
        {
            try
            {
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists) return null;
                return (file.ResolveLinkTarget(true) ?? file).FullName;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static bool IsWithin(string root, string path) =>
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(root, Trim(path), StringComparison.Ordinal);

        // ResolveLinkTarget on a file only reports a link on that file, not a
        // link in one of its parent directories. Walk those components too: a
        // harmless-looking `avatars/me.png` can otherwise leave the workspace
        // through an `avatars` symlink.
        private static bool EscapesThroughLink(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            var current = root;

            foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                try
                {
                    FileSystemInfo info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    var target = info.ResolveLinkTarget(true);
                    if (target is not null && !IsWithin(root, target.FullName)) return true;
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
                catch (ArgumentException) { return true; }
                catch (NotSupportedException) { return true; }
            }

            return false;
        }

        private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
    }
}
