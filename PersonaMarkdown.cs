using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // The Markdown grammar an agent's identity is written in, shared by the two
    // places that read one: an OpenClaw workspace's IDENTITY.md, and a local
    // session's CLAUDE.md.
    //
    // Lifted out of OpenClawWorkspaceIdentity rather than copied. Two parsers
    // for one file format is two grammars that drift, and the drift is
    // invisible — a field a user writes once, sees honoured on one orb and
    // ignored on another, reads as a bug in the app, which it would be.
    //
    // OpenClaw's IDENTITY.md format is deliberately Markdown, not a second
    // config language: `- Name: Aurora`. That is still the whole of the
    // explicit grammar — a bullet, a bold field, a table row, a front-matter
    // key — and it is still deliberately small.
    //
    // What has changed, and the reason this file has a regex in it: a CLAUDE.md
    // is not a profile. People do not write `- Name: Leota` in one; they write
    // "Her name is Leota", because they are addressing Claude rather than
    // filling in a form. So prose is now read — but only in one bounded
    // sentence shape, and only when what it names could not be anything else.
    //
    // The bounds are what keep "so prose mentioning voice: cannot silently
    // change speech" true, and each is there for a specific way prose lies:
    //
    //   * The sentence must be the whole line, subject first: an optional
    //     possessive, one of a fixed list of nouns, then "is"/"should be"/
    //     "will be". "Name: prose is not metadata" has no verb and is not read;
    //     nor is a noun buried mid-sentence, which is where a passing mention
    //     of a voice lives.
    //   * A name or a voice is one to three words of at most forty characters,
    //     drawn from letters, digits and the handful of marks real voice
    //     identifiers use (`af_bella`, `Ava (Premium)`, `O'Brien`). "The name
    //     is derived from the folder unless the user renames it" is a sentence
    //     about naming, not a name, and the word count is what tells them
    //     apart. A colon, a slash or an "http" is a URL or a second clause, and
    //     either way not a voice.
    //   * A picture is a relative path whose last token ends in an image
    //     extension. Rooted paths and anything with a colon in it are refused
    //     before the filesystem is asked, so `/etc/passwd` and
    //     `https://x/y.png` never become a read.
    //   * Nothing inside YAML front matter, a fenced code block, a bullet, a
    //     bold field or a table row reaches the prose arm at all. A fenced
    //     block is where a CLAUDE.md *shows* you what to write, and text being
    //     shown is not text being asserted.
    //
    // Every one of those refusals loses a field rather than guessing at one,
    // which is the right way round: an orb wearing the folder's name is
    // ordinary, and an orb that renamed itself out of a sentence about
    // something else is a bug nobody can find.
    internal static class PersonaMarkdown
    {
        internal sealed record Fields(string? Name, string? Voice, double? Rate, string? Avatar);

        // Which field a prose sentence named, if it named one at all.
        internal enum ProseKind { None, Name, Voice, Avatar }

        // The engine can run anywhere from half to double real-time speech
        // without the model itself starting to garble — this is a caution
        // against a profile typo (a rate of "13" meant as "1.3") reaching the
        // engine as a wildly wrong value, not a claim about where it stops
        // sounding good.
        private const double MinRate = 0.5;
        private const double MaxRate = 2.0;

        // A name or a voice, as prose is allowed to state one.
        private const int MaxProseWords = 3;
        private const int MaxProseValueLength = 40;

        internal static Fields Parse(IEnumerable<string> lines)
        {
            string? name = null;
            string? voice = null;
            double? rate = null;
            string? avatar = null;
            var inFrontMatter = false;
            var inFence = false;
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

                // Fence state is tracked for every line, including the ones the
                // explicit arms below go on to read. Only the prose arm acts on
                // it — see the header — so this changes nothing about what a
                // bullet or a table row means, which is deliberate: what
                // OpenClaw's profiles already parse to is not this ticket's to
                // move.
                if (Fence(trimmed))
                {
                    inFence = !inFence;
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

                if (!trimmed.StartsWith("-", StringComparison.Ordinal))
                {
                    // Everything that is not a bullet, and that the explicit
                    // arms above did not claim, gets one look as a sentence.
                    // Reaching here already rules out a table row and a bold
                    // field carrying a voice; the regex itself rules out the
                    // rest, since a line beginning `**`, `|` or `#` cannot
                    // start with one of its nouns.
                    if (inFrontMatter || inFence) continue;
                    if (!ProseField(trimmed, out var kind, out var value)) continue;

                    switch (kind)
                    {
                        case ProseKind.Name:
                            name ??= value;
                            break;
                        case ProseKind.Voice:
                            // No "did it survive VoiceValue" arm here, unlike
                            // the explicit fields above: everything VoiceValue
                            // rejects — blank, a <placeholder> — is already
                            // outside what a prose value is allowed to contain,
                            // so ??= against a null it cannot produce is the
                            // whole of the handling rather than a missing check.
                            var (proseVoice, proseRate) = VoiceValue(value);
                            voice ??= proseVoice;
                            rate ??= proseRate;
                            break;
                        case ProseKind.Avatar:
                            avatar ??= value;
                            break;
                    }

                    continue;
                }

                // A bullet can carry its own bold label — `- **Voice:** Ava` is
                // exactly as common in a real profile as the bare `- Voice:
                // Ava` below, and every real fixture this parser was built
                // from happens to use it. BoldField understands where the
                // closing `**` actually falls (after the colon, not before
                // it); FieldAfterColon does not, and used to leave it sitting
                // in the value — every bulleted-bold field, not just Voice,
                // read back with a stray "** " on the front of it.
                var rest = trimmed[1..].TrimStart();
                string label, fieldValue;
                if (!BoldField(rest, out label, out fieldValue))
                {
                    if (!FieldAfterColon(rest, out label, out fieldValue) || !Valid(fieldValue)) continue;
                }
                else if (!Valid(fieldValue)) continue;

                if (name is null && string.Equals(label, "Name", StringComparison.OrdinalIgnoreCase))
                    name = fieldValue;
                else if (voice is null && VoiceLabel(label) && VoiceValue(fieldValue) is var (bulletVoice, bulletRate) && bulletVoice is not null)
                {
                    voice = bulletVoice;
                    rate ??= bulletRate;
                }
                else if (avatar is null && AvatarLabel(label))
                    avatar = fieldValue;
            }

            return new Fields(name, voice, rate, avatar);
        }

        // One sentence, subject first. The optional possessive is there because
        // that is how people actually write it — "Her name is Leota", not "Name
        // is Leota" — and the verb is required because a noun with no verb
        // after it ("Name: ...", "the picture in the header") is prose about
        // the field rather than a statement of it.
        //
        // Compiled: this runs over every non-bullet line of every CLAUDE.md up
        // a session's directory tree, and the scan does it whenever those files
        // change. Interpreted, that is the same pattern re-parsed thousands of
        // times for no reason.
        private static readonly Regex Prose = new(
            @"^(?:(?:this\s+agent's|the\s+agent's|her|his|their|its|the|my|your|agent)\s+)?" +
            @"(?<noun>speaking\s+voice|tts\s+voice|profile\s+picture|profile\s+pic|profile\s+image" +
            @"|name|voice|picture|portrait|avatar|image)" +
            @"\s+(?:is|should\s+be|will\s+be)\s*:?\s+(?<value>.+?)\.?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        internal static bool ProseField(string trimmed, out ProseKind kind, out string value)
        {
            kind = ProseKind.None;
            value = "";

            var match = Prose.Match(trimmed);
            if (!match.Success) return false;

            var noun = Collapse(match.Groups["noun"].Value);
            var stated = match.Groups["value"].Value.Trim();
            var words = stated.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // "Her name is" followed by nothing but whitespace still matches
            // the shape — the value group is happy to be a tab — and there is
            // no field in it.
            if (words.Length == 0) return false;

            if (noun is "name")
            {
                if (!BoundedWords(stated, words)) return false;
                kind = ProseKind.Name;
                value = stated;
                return true;
            }

            if (noun is "voice" or "speaking voice" or "tts voice")
            {
                if (!BoundedWords(stated, words)) return false;
                kind = ProseKind.Voice;
                value = stated;
                return true;
            }

            // The last token, so "the file leota.png" names the same picture
            // "leota.png" does. A path is one token; the words in front of it
            // are somebody being polite about it.
            var path = words[^1];

            if (path.Contains(':')) return false;
            if (Path.IsPathRooted(path)) return false;
            if (!PictureExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                return false;

            kind = ProseKind.Avatar;
            value = path;
            return true;
        }

        // A name or a voice as prose may state one. Deliberately not "looks
        // like a name" — there is no such test — but "short enough, and made of
        // the characters a real identifier is made of". Everything longer or
        // stranger than that is a sentence.
        private static bool BoundedWords(string value, string[] words)
        {
            if (value.Length > MaxProseValueLength) return false;
            if (words.Length > MaxProseWords) return false;
            if (value.Contains(':') || value.Contains('/')) return false;
            if (value.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;

            return value.All(c =>
                char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '\'' or '(' or ')');
        }

        // The noun as the alternation spells it, with whatever whitespace the
        // writer used between its words flattened to one space, so "profile
        // \tpicture" and "profile picture" are the same noun.
        private static string Collapse(string noun) =>
            string.Join(' ', noun.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // A fence opens and closes with three or more backticks or tildes. The
        // info string after an opening fence ("```bash") is not examined —
        // toggling on either spelling is enough, and a closing fence never has
        // one.
        private static bool Fence(string trimmed) =>
            (trimmed.StartsWith("```", StringComparison.Ordinal)
             || trimmed.StartsWith("~~~", StringComparison.Ordinal));

        internal static bool FieldAfterColon(string text, out string label, out string value)
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
        internal static bool BoldField(string text, out string label, out string value)
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

        internal static bool TableField(string text, out string label, out string value)
        {
            var cells = text.Trim().Trim('|').Split('|');
            if (cells.Length != 2) { label = value = ""; return false; }
            label = cells[0].Trim().Trim('*').Trim();
            value = cells[1].Trim();
            return label.Length > 0;
        }

        internal static bool TableSeparator(string text)
        {
            var cells = text.Trim().Trim('|').Split('|');
            return cells.Length >= 2 && cells.All(cell =>
            {
                var marker = cell.Trim();
                return marker.Length >= 3 && marker.Contains('-')
                    && marker.All(c => c is '-' or ':');
            });
        }

        internal static bool VoiceLabel(string label) =>
            label.Equals("Voice", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Voice Name", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Speech Voice", StringComparison.OrdinalIgnoreCase)
            || label.Equals("TTS Voice", StringComparison.OrdinalIgnoreCase);

        // The same set of words the prose arm accepts for a picture, as an
        // explicit field label. One list rather than two: a user who writes
        // "Her profile picture is leota.png" in one file and
        // `- Profile picture: leota.png` in another has said the same thing
        // twice, and being told only one of them counts is the drift this
        // whole file exists to prevent.
        internal static bool AvatarLabel(string label) =>
            label.Equals("Avatar", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Picture", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Pic", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Image", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Picture", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Portrait", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Image", StringComparison.OrdinalIgnoreCase);

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
        internal static (string? Voice, double? Rate) VoiceValue(string value)
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

        internal static bool Valid(string value) =>
            !string.IsNullOrWhiteSpace(value) && !IsPlaceholder(value);

        internal static bool IsPlaceholder(string value) =>
            value.StartsWith("<", StringComparison.Ordinal) && value.EndsWith(">", StringComparison.Ordinal);
    }
}
