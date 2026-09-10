using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // "50% sky and 50% nicole" — a voice written as a mixture of two or more
    // of the engine's own, which is what this repository's `.claude/PERSONA.MD`
    // has said since the day it was written and what nothing could read
    // (CB-136).
    //
    // Pure, and separated from everything that touches a disk for the reason
    // every parser in this repository is: the grammar has a right answer per
    // input and the only way to keep it having one is to be able to ask it
    // without an engine installed. VoiceBlends, below in its own file, is the
    // half that turns an answer into a file.
    //
    // The grammar, in full:
    //
    //     50% sky and 50% nicole      weights first
    //     sky 50% and nicole 50%      weights last
    //     sky and nicole              no weights at all — equal shares
    //     sky, nicole, bella          comma, plus and "plus" separate too
    //     50% sky                     one part is just that voice
    //
    // and the bounds, each of which is a way a sentence lies about being a
    // blend:
    //
    //   * **Every part is one token.** A voice identifier is `af_sky` or
    //     `sky`, never a phrase — so "a matter of taste, plus tone" is not two
    //     voices, and is refused here rather than downstream where it would
    //     have become a persona holding a junk voice string. This bound is
    //     what makes it safe for PersonaMarkdown to widen a voice value's
    //     character whitelist to admit `%`, `,` and `+` at all: the widening
    //     admits exactly what parses here.
    //   * **Two to four parts.** One is a voice, not a blend, and is collapsed
    //     to that voice. Five is somebody generating text.
    //   * **Weights are all or none.** A blend where one part carries a
    //     percentage and another does not is a sentence that happens to
    //     contain a number, not a mixture whose shares anybody stated.
    //   * **Stated weights total 100, give or take one.** 99 and 101 are what
    //     thirds and sevenths round to and are normalised without comment; 120
    //     is not a rounding error, it is somebody who meant something else, and
    //     guessing which half they meant is worse than their own global voice.
    //
    // Everything refused here ends up at the user's global voice, never at
    // silence. That is the rule the whole feature is written around and the
    // reason no arm of this throws.
    internal static class VoiceBlend
    {
        // Four, because that is where a mixture stops being a choice. Two
        // voices is a blend anybody can hear; four is already past what the
        // style vectors distinguish, and a cap is what stops a generated
        // paragraph of comma-separated words from being read as one.
        internal const int MaxParts = 4;

        // What the stated weights may total and still be read as a rounding
        // error rather than a different intention.
        internal const int MinTotal = 99;
        internal const int MaxTotal = 101;

        // A part of a blend before anything has looked at whether the machine
        // has such a voice: the name as written, and its share as a whole
        // percentage. Percent rather than a double so that the share, the slug
        // and the arithmetic can never disagree — see Shares.
        //
        // Deliberately no `Weight` here, only on the resolved part below. An
        // unresolved part's share is never multiplied by anything — nothing
        // can be averaged until the voices are known — so a fraction on this
        // record would be a second spelling of Percent that no caller wanted
        // and no test could reach except by asking for it.
        internal sealed record Part(string Voice, int Percent);

        internal sealed record Blend(IReadOnlyList<Part> Parts);

        // A part whose voice the machine actually has, and the option that
        // will speak it. This is where the share becomes a fraction, because
        // this is where something multiplies by it.
        internal sealed record ResolvedPart(TextToSpeech.VoiceOption Option, int Percent)
        {
            internal double Weight => Percent / 100.0;
        }

        // A blend every part of which resolved, and the name the result is
        // known by: the single voice's own name when there is only one part,
        // and the slug of a file that has to exist otherwise.
        internal sealed record Resolved(IReadOnlyList<ResolvedPart> Parts, string Name)
        {
            internal bool IsSingleVoice => Parts.Count == 1;
        }

        // One part, with its percentage in front of it or behind it but never
        // both. The token is what a voice identifier is made of — letters,
        // digits, and the marks that appear inside real ones — and nothing
        // else, which is the single-token bound the header describes.
        //
        // Compiled for the same reason PersonaMarkdown's is: this runs over
        // every voice value of every CLAUDE.md up a session's tree, whenever
        // any of them changes.
        private static readonly Regex PartShape = new(
            @"^(?:(?<before>\d{1,3})\s*%\s*)?(?<voice>[A-Za-z0-9_'\-]+)(?:\s*(?<after>\d{1,3})\s*%)?$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // The words that separate one part from the next, as whole words. A
        // voice called `af_alexander` contains "and" and is one token, so this
        // is a token test and never a substring one.
        private static readonly string[] SeparatorWords = { "and", "plus" };

        // The punctuation that does the same job. Deliberately these two and
        // no more: `/` and `&` were considered and left out, because `/` is
        // how a path is spelled and this grammar is reached from an explicit
        // `- Voice:` bullet, which does not refuse one the way a prose value
        // does.
        private static readonly char[] SeparatorMarks = { ',', '+' };

        // Null means "this is not a blend", which is a different answer from
        // "this is a broken blend" only in what the caller does next: the first
        // falls through to the ordinary single-voice match, the second falls
        // through to the user's global voice. Both are handled by the caller
        // returning null from its own resolution, so the two collapse — and
        // that is deliberate rather than a shortcut. A value this cannot read
        // is a value nobody should be guessing at.
        internal static Blend? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var trimmed = value.Trim();
            var pieces = Split(trimmed);

            // Nothing here says "blend": one piece, no percentage. That is an
            // ordinary voice name and this grammar has no business claiming
            // it, so the caller's existing single-voice match sees it
            // unchanged. `Ava (Premium)` lands here, parentheses intact.
            if (pieces.Count == 1 && !trimmed.Contains('%')) return null;

            if (pieces.Count is 0 or > MaxParts) return null;

            var voices = new List<string>(pieces.Count);
            var stated = new List<int?>(pieces.Count);

            foreach (var piece in pieces)
            {
                var match = PartShape.Match(piece);
                if (!match.Success) return null;

                var before = match.Groups["before"];
                var after = match.Groups["after"];

                // A share written at both ends is two claims about one part
                // and there is no reading that honours both.
                if (before.Success && after.Success) return null;

                voices.Add(match.Groups["voice"].Value);

                var weight = before.Success ? before.Value : after.Success ? after.Value : null;
                stated.Add(weight is null
                    ? null
                    : int.Parse(weight, NumberStyles.None, CultureInfo.InvariantCulture));
            }

            // A single part is that voice, whatever share was written on it —
            // `50% sky` is somebody starting to write a blend and stopping,
            // and half of one voice is the same voice.
            if (voices.Count == 1) return new Blend(new[] { new Part(voices[0], 100) });

            var weighted = stated.Count(w => w is not null);
            if (weighted != 0 && weighted != stated.Count) return null;

            IReadOnlyList<int> raw;
            if (weighted == 0)
            {
                raw = Enumerable.Repeat(1, stated.Count).ToList();
            }
            else
            {
                var total = stated.Sum(w => w!.Value);
                if (total is < MinTotal or > MaxTotal) return null;
                raw = stated.Select(w => w!.Value).ToList();
            }

            var shares = Shares(raw);
            return new Blend(voices.Select((voice, i) => new Part(voice, shares[i])).ToList());
        }

        // The written shares as whole percentages that total exactly 100.
        //
        // Largest remainder, which matters more than it sounds: the percentages
        // are not a display of the weights, they *are* the weights — the
        // arithmetic and the file's own name are both derived from them — so
        // three equal parts have to come out as some fixed 34/33/33 rather than
        // as three copies of 0.3333 that sum to 0.9999 and a slug that rounds
        // them differently. Ties go to the earlier part, so the answer depends
        // on nothing but the input.
        internal static IReadOnlyList<int> Shares(IReadOnlyList<int> raw)
        {
            var total = raw.Sum();
            var shares = new int[raw.Count];
            var remainders = new (int Index, double Remainder)[raw.Count];

            var assigned = 0;
            for (var i = 0; i < raw.Count; i++)
            {
                var exact = raw[i] * 100.0 / total;
                shares[i] = (int)Math.Floor(exact);
                remainders[i] = (i, exact - shares[i]);
                assigned += shares[i];
            }

            foreach (var (index, _) in remainders
                         .OrderByDescending(r => r.Remainder)
                         .ThenBy(r => r.Index)
                         .Take(100 - assigned))
            {
                shares[index]++;
            }

            return shares;
        }

        // The parts, in the order they were written, split on any of the
        // separators this grammar knows.
        //
        // Marks first and then words, so `sky, nicole and bella` — which mixes
        // both — is three parts rather than two. Empty pieces are dropped,
        // which is what makes a trailing "and" harmless rather than a fourth
        // part of nothing.
        private static IReadOnlyList<string> Split(string value)
        {
            var pieces = new List<string>();
            var current = new List<string>();

            void Flush()
            {
                if (current.Count > 0) pieces.Add(string.Join(' ', current));
                current.Clear();
            }

            foreach (var chunk in value.Split(SeparatorMarks, StringSplitOptions.None))
            {
                foreach (var token in chunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (SeparatorWords.Contains(token, StringComparer.OrdinalIgnoreCase)) Flush();
                    else current.Add(token);
                }

                Flush();
            }

            return pieces;
        }

        // Every part resolved against the voices this machine actually has,
        // or nothing at all.
        //
        // Neural only, and that is the whole of the gate the ticket asks for.
        // A blend is a Kokoro style-vector average; there is no such operation
        // on a macOS system voice or on somebody's own `speak` script, and
        // AllVoiceOptions offers no neural options at all when the engine is
        // switched off — so a persona naming a blend on a machine with the
        // engine off resolves to nothing here and speaks in the user's own
        // voice, which is exactly what it should do.
        //
        // One unresolvable part rejects the whole blend rather than dropping
        // that part. Speaking a two-voice mixture as one of its halves is a
        // voice nobody asked for, and it would be indistinguishable from the
        // blend having worked.
        internal static Resolved? Resolve(Blend blend, IEnumerable<TextToSpeech.VoiceOption> options)
        {
            var neural = options
                .Where(option => option.Engine == TextToSpeech.SpeakEngine.Neural)
                .ToList();

            var parts = new List<ResolvedPart>(blend.Parts.Count);

            foreach (var part in blend.Parts)
            {
                var match = TextToSpeech.MatchVoiceOption(part.Voice, neural);
                if (match is null) return null;
                parts.Add(new ResolvedPart(match, part.Percent));
            }

            return new Resolved(parts, parts.Count == 1 ? parts[0].Option.Name : Slug(parts));
        }

        // The name a materialised blend is known by, on disk and in the
        // engine's own listing.
        //
        // It has to be deterministic, because it is the cache: the same blend
        // written in the same words must name the file that already exists
        // rather than a second copy of it. And it has to survive the engine's
        // language filter, which is the part that is not obvious — the engine
        // reads a voice's language off a two-letter prefix, files `zf_` under
        // Mandarin and drops it from an English picker. Measured against the
        // real engine on this machine: `zz_probe_y.npy` was dropped from
        // `--list-voices` while `af_blend_sky50-nicole50.npy` and a
        // prefix-less name were both listed.
        //
        // So the blend wears its first part's prefix, which is also the honest
        // answer — a mixture of two American female voices is an American
        // female voice — and each part after that is spelled with its own
        // prefix stripped only when it matches. `af_sky` and `bf_isabella`
        // mixed together is `af_blend_sky50-bf_isabella50`: unambiguous by
        // construction, and short in the case everybody actually writes.
        internal static string Slug(IReadOnlyList<ResolvedPart> parts)
        {
            var lead = TextToSpeech.LocalePrefix(parts[0].Option.Name);

            var body = parts.Select(part =>
            {
                var name = part.Option.Name.Trim();
                if (lead.Length > 0 && name.StartsWith(lead, StringComparison.Ordinal))
                    name = name[lead.Length..];
                return name + part.Percent.ToString(CultureInfo.InvariantCulture);
            });

            return lead + "blend_" + string.Join('-', body);
        }
    }
}
