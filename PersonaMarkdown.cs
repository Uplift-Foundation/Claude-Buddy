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
    //   * A **voice** has one bound more than a name, added by CB-136: it may
    //     also be a mixture — `50% sky and 50% nicole` — up to eleven words
    //     and a hundred and twenty characters, admitting `%`, `,` and `+`.
    //     That is a large widening on paper and a narrow one in fact, because
    //     the longer form is accepted only when VoiceBlend can read it as a
    //     mixture, and VoiceBlend insists every part is a single token. See
    //     BoundedVoice.
    //   * A picture is a path whose last token ends in an image extension,
    //     once a code span and a trailing parenthetical note have been taken
    //     off it — `` `avatars/lilibeth.png` (AI-generated) `` is how real
    //     profiles write one, and CB-139 found eighteen lines of it in one
    //     machine's persona.log, portraits that had never drawn because only
    //     the prose arm normalised anything. `AvatarValue` is that
    //     normalisation for a sentence; `ExplicitAvatarValue` is the same
    //     function for a labelled field, and the two disagree on purpose
    //     about one thing — the next paragraph. Whether an absolute path is
    //     *allowed* is not decided here at all: that is a question about
    //     where the file naming it lives, not about the string, and
    //     PersonaFiles answers it by containment once this grammar has
    //     handed back a candidate (CB-140). A colon still refuses
    //     `https://x/y.png` and a `data:` URI before the filesystem is
    //     asked — `ColonIsADriveLetter` carves out only the one shape a
    //     colon legitimately takes in a path, `C:\...` — so those two never
    //     become a read, but `/etc/passwd.png` and
    //     `C:\Users\...\portrait.png` now reach PersonaFiles exactly like a
    //     relative path does, which is the one place equipped to ask whether
    //     the directory they name is the markdown's own.
    //
    //   * **A labelled field is one whole value; a sentence is not.**
    //     Somebody who writes `image:`, `- Avatar:` or `| Photo |` and then a
    //     path has written the whole of it — there is no sentence to find a
    //     filename in, so `ExplicitAvatarValue` never takes a last token off
    //     a labelled value, only the code-span and parenthetical decoration
    //     every arm already stripped. `AvatarValue`, the sentence reader,
    //     still takes the last token, because "the file leota.png" has to
    //     keep naming `leota.png` — that is CB-133's whole reason to exist.
    //     The two still have to agree on one rule: neither reading may turn
    //     an absolute path into a relative one by truncating around a space
    //     in a directory name, which is the defect CB-140 was filed over.
    //     `AvatarValue`'s half of that is to stop tokenizing at all once the
    //     *whole* candidate is rooted, so "Her picture is /Users/w/My
    //     Docs/x.png" is read correctly without touching "the file
    //     leota.png"; where only a *fragment* of a longer sentence is
    //     rooted — "the file /a b/x.png" — refusing outright is the only
    //     answer that neither guesses at a truncated path nor accepts a
    //     filename whose directory is not the one that was written.
    //
    //   * `image_animated` is a second picture *label*, not a second field:
    //     a persona still has one `Avatar`, and the first picture label this
    //     grammar reaches keeps it — see `LocalPersona.Persona`'s own
    //     comment for why a persona is not allowed a byte array to hold a
    //     second picture in. Every generator writes `image:` before
    //     `image_animated:`, so first-wins means the still wins, which is
    //     not a consolation prize: the still clears the byte cap by a wide
    //     margin and the animation frequently does not (see
    //     `PersonaFiles.MaxAvatarBytes`), so reading the still first is the
    //     only choice under which this feature reliably produces a portrait
    //     at all.
    //
    //   * Front matter gets one strip nothing else does: a scalar value is
    //     unquoted once, immediately after the label and the colon are torn
    //     apart, because `name: "Aurora"` is YAML saying a string is a
    //     string — the quotes are the format's syntax, not something the
    //     writer meant to name their agent with — so `Name`, `Voice` and a
    //     picture all lose them before any bound above judges the result. A
    //     bullet, a bold field and a table cell get no such strip: a quote
    //     there is Markdown a person typed, and typed things are read as
    //     written.
    //
    //   * `name:` and `slug:` both name the agent, sharing one bound —
    //     `NameValue`, the same `BoundedWords` a prose or colon-less name
    //     passes — with the bullet `Name` arm and the section arm, under one
    //     list, `NameLabel`. Before CB-140 the bullet arm was the one place
    //     in this whole grammar that read a name with no bound at all, which
    //     is how an ordinary `- **Name**: Jordan Casey, MBA / MSc` — five
    //     words, a comma and a slash, describing the human rather than any
    //     persona — ended up on an orb. `slug:` exists because a generator
    //     without a display name yet still writes one, and `name:` wins the
    //     tie the same way every other field's first statement does.
    //
    //   * **A standalone bold field and a two-cell table row name the agent
    //     too, but only inside a persona section** (CB-142). Before this they
    //     were the two arms that read a voice and a picture and no name at
    //     all, so `**Name:** Leota` — the very spelling the README used as its
    //     example — named nobody, which is the drift at the top of this file
    //     in its purest form: one field, written twice, honoured once.
    //
    //     The bound alone could not fix it. `| Name | string |` in a schema
    //     table is a one-word value, and `NameValue("string")` returns
    //     `"string"` — measured, not read off the regex — so a bound-only arm
    //     would have put the word "string" on an orb the first time anybody
    //     documented a data type. Scope is what refuses that line **outside a
    //     persona section**, and the qualifier is not pedantry: under
    //     `## Persona` the same row still yields `name = "string"`, by design.
    //     A two-cell table under a persona heading is a table of persona
    //     attributes, and "string" is a perfectly ordinary short name — there
    //     is no test here for whether a value looks suspicious, and there
    //     should not be one. The rule is CB-135's: a table of persona
    //     attributes under `## Persona` and a schema table in a design
    //     document are the same shape, and the heading is the only thing that
    //     tells them apart.
    //
    //     The two guards are complementary rather than redundant, which is
    //     why both are here. `**Name**: the value passed to the constructor`
    //     is refused on word count even underneath `## Persona`; `**Name**:
    //     Aurora` in an ordinary paragraph is refused on scope while passing
    //     the bound easily. Neither guard catches both lines.
    //
    //     The bullet arm and the front-matter arm stay unscoped, and that is
    //     not an inconsistency. OpenClaw's `IDENTITY.md` is a bare bulleted
    //     list with no heading anywhere in it, and profile-gen writes YAML in
    //     both of its templates — so scoping either would break every shipped
    //     profile, while no shipped profile names an agent with a bold field
    //     or a table row at all. Voice and picture stay unscoped on these two
    //     arms for exactly that reason in reverse, and the evidence is named
    //     rather than asserted because it has been doubted once already: the
    //     fixture in
    //     `OpenClawWorkspaceIdentityIntegrationTests.ARedactedProfileKokoroVoiceReachesTheMatchingNeuralOption`
    //     is a **redacted real** `IDENTITY.md` whose voice is a bare
    //     `**Voice:** af_bella (Kokoro TTS)` — not a bullet — under a title
    //     that is not a persona heading. Scoping the bold arm's voice would
    //     stop that profile speaking. CB-142 moved a name and nothing else.
    //
    //     CB-142 first gave this arm alone a fence check, and CB-144
    //     replaced it with one gate covering every arm. The narrow version
    //     was wrong in an instructive way: guarding the bold and table arms
    //     does nothing for the bullet arm, which calls `BoldField` and
    //     `FieldAfterColon` itself, so a fenced `- name: Build the thing`
    //     went on renaming orbs after a pasted build step. See the gate in
    //     `Parse`, just below the front-matter arm.
    //   * Nothing inside YAML front matter, a fenced code block, a bullet, a
    //     bold field or a table row reaches the prose arm at all. A fenced
    //     block is where a CLAUDE.md *shows* you what to write, and text being
    //     shown is not text being asserted.
    //
    //     As of CB-144 that second sentence is true of **every** arm rather
    //     than only the prose one: a fenced line is skipped outright, with
    //     exactly one exception, CB-141's marked `yaml` block, which is the
    //     one fence a writer has explicitly declared to be a persona.
    //
    //   * With one exception, which is CB-141: a `yaml`/`yml` fence **inside
    //     a marked persona block** is read by the front-matter arm above,
    //     exactly as if its lines were between two `---` rules. A marked
    //     block is the region between an HTML comment reading
    //     `profile-gen:start` (or the tool-neutral `persona:start`) and its
    //     matching `:end`. Outside such a block a fence is still skipped
    //     whole, so the sentence above keeps meaning what it says for every
    //     ```yaml example in every CLAUDE.md on the machine.
    //
    //     The exception exists because a shipping tool writes exactly that
    //     shape on purpose. profile-gen's `claude-md` output mode wraps the
    //     persona in those markers and puts every field it declares — name,
    //     slug, image, image_animated, voice — inside a ```yaml fence, so the
    //     mode resolved to *no persona at all*: the same requirement-unmet
    //     class as CB-135 and CB-140, one shape further along.
    //
    //     Why the marker rather than the persona section CB-135 already
    //     models, which would be the tidier signal: profile-gen's block opens
    //     with `### <the persona's name>`, and PersonaSection matches only
    //     persona/attributes/identity/character/profile/about me/who i am. A
    //     heading reading `### Aurora Vance` opens no section at all, so
    //     sectionLevel is zero for the whole block and a section-scoped rule
    //     would fix nothing. The marker also has a real end, where a section
    //     has a guessed one. See MarkedBlockMarker.
    //
    // And one shape more, added by CB-135 because the bounds above turned out
    // to refuse a real file: a **colon-less `Label Value` line**, but only
    // underneath a persona heading. This repository's own `.claude/PERSONA.MD`
    // writes its attributes as
    //
    //     ## Attributes
    //     Name Jennifer
    //     Profile Photo cto.png
    //
    // which is neither the explicit grammar (no colon, no bullet) nor a
    // sentence (no verb), so a file naming a name and a picture in a file the
    // resolver provably reads produced no persona at all.
    //
    // The tension that shape raises is the one the header above spends its
    // length on — `Name Jennifer` and "Name resolution is handled by the
    // folder" have the same first word — and it is resolved by **scope, not by
    // widening**. A persona section is a heading whose text mentions persona,
    // attributes, identity, character, profile, about me or who i am, and it
    // runs until the next heading of its own level or higher. Inside one, a
    // line whose first word or two is a recognised label states that field;
    // outside one, this arm does not run at all, so every paragraph in every
    // CLAUDE.md above a session's directory means exactly what it meant
    // before. The value still has to pass the same bounds — which is why the
    // sentence about name resolution is refused even inside an Attributes
    // section, on word count. `Voice is 50% sky and 50% nicole` was refused
    // alongside it until CB-136 gave a blend somewhere to go; it is read now,
    // and the bound it passes is BoundedVoice rather than a wider version of
    // the one that refuses the sentence.
    //
    // Every one of those refusals loses a field rather than guessing at one,
    // which is the right way round: an orb wearing the folder's name is
    // ordinary, and an orb that renamed itself out of a sentence about
    // something else is a bug nobody can find.
    //
    // And then there is the form people actually reach for when they are
    // *listing* attributes rather than addressing Claude: no colon, no verb, no
    // bullet — a label and a value, one per line, under a heading that says
    // what the list is.
    //
    //     ## Attributes
    //     Name Jennifer
    //     Profile Photo cto.png
    //
    // CB-133 read none of that, and this repository's own `.claude/PERSONA.MD`
    // — which names all three fields — resolved to no persona at all while
    // every test in the suite was green. The tension is real, though: `Label
    // Value` with nothing between them is also the shape of the first two words
    // of an enormous number of ordinary sentences, and "Name resolution is
    // handled by the folder" must not rename an orb.
    //
    // It is resolved by **scope, not by widening**. The colon-less form is read
    // only inside a persona section — a heading, at any level, whose text
    // contains one of the words below, running until the next heading of the
    // same or a higher level. Outside such a section the form does not exist,
    // so nothing anywhere else in a CLAUDE.md changes meaning. Inside it the
    // value still has to pass the same bounds a prose value does, which is what
    // stops "Name resolution is handled by the folder" from being read even
    // under `## Attributes`.
    //
    // A heading is what a person writes when they mean "the following is a
    // description of the agent", and there is no cheaper signal of intent
    // available in Markdown. Section-scoping also keeps the promise the bounds
    // above make: a passing mention still cannot set a field, because a passing
    // mention is not written under `## Persona`.
    internal static class PersonaMarkdown
    {
        // RawAvatar is the value as written after the first picture label the
        // explicit grammar recognised, whether or not it read as a path. It is
        // here so that a picture somebody *named* and this parser could not use
        // has somewhere to be reported from. Without it a value that fails
        // normalisation is indistinguishable from a file that names no picture
        // at all, and the resolver has nothing to write down: all twenty-four
        // refusals on the machine CB-139 was filed from were logged as
        // "unreadable — it is missing", and not one of them was about a missing
        // file. A wrong diagnosis with good grammar is exactly what hid them
        // for as long as it did.
        //
        // Only the explicit arms fill it, and that asymmetry is deliberate. An
        // explicit arm has a *label* in front of the value — somebody wrote
        // `Profile picture:` and then wrote something — so a value that reads
        // as nothing is a mistake worth naming. A prose sentence has a noun in
        // a sentence instead, and "her profile picture is lovely" names no file
        // and never meant to; logging that would turn a bound this file's
        // header spends its length defending into a stream of complaints about
        // ordinary English.
        internal sealed record Fields(
            string? Name, string? Voice, double? Rate, string? Avatar, string? RawAvatar = null);

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

        // ...and a voice only, when what it states is a mixture.
        //
        // CB-135 left these bounds alone on purpose and asserted that
        // `Voice is 50% sky and 50% nicole` produced nothing, so that whichever
        // ticket gave a blend somewhere to go had to come here and move them
        // deliberately. This is that move (CB-136).
        //
        // Eleven words is what a four-part blend spelled the long way needs —
        // `25% a and 25% b and 25% c and 25% d` — and a hundred and twenty
        // characters is that same blend with real identifiers in it. Both are
        // far past what a name may be, which is why they are separate
        // constants and why they are reached only through BoundedVoice, which
        // guards them with two conditions rather than one: the value must
        // contain a `%`, and VoiceBlend must be able to read it as a mixture.
        // The second alone is not enough — see the comment in BlendShaped,
        // where a real sentence that satisfies it is named.
        private const int MaxVoiceWords = 11;
        private const int MaxVoiceValueLength = 120;

        internal static Fields Parse(IEnumerable<string> lines)
        {
            string? name = null;
            string? voice = null;
            double? rate = null;
            string? avatar = null;
            string? rawAvatar = null;
            var inFrontMatter = false;
            var inFence = false;
            var sawContent = false;

            // The info string of the fence currently open — "yaml" for
            // "```yaml", the empty string for a bare "```" — and read only
            // while inFence is true. See the fence arm below for why it is
            // cleared on the way out even though nothing can observe that.
            var fenceInfo = "";

            // Whether we are inside a *marked* persona block: the region
            // between a `<!-- profile-gen:start ... -->` line and its matching
            // `:end`, or the tool-neutral `persona:start`/`persona:end`
            // spelling of the same thing. See MarkedBlockMarker for why the
            // marker is the signal and a heading is not.
            var inMarkedBlock = false;

            // Zero when no persona section is open, otherwise the heading
            // level that opened the one we are inside. A level rather than a
            // flag because a section ends at the next heading of its own level
            // *or higher*: a `### Voice` underneath `## Attributes` is still
            // inside the attributes, and a second `## Something else` is not.
            var sectionLevel = 0;

            // One place where a field that has been named actually lands,
            // shared by the two arms that can name one. Written as a local
            // function rather than repeated because the prose arm and the
            // colon-less arm agree about everything after the reading — first
            // statement wins, a voice goes through the shared voice grammar so
            // an engine annotation is stripped the same way — and two copies
            // of that agreement is one copy that can drift.
            void Assign(ProseKind kind, string stated)
            {
                if (kind is ProseKind.Name) name ??= stated;
                else if (kind is ProseKind.Avatar) avatar ??= stated;
                else
                {
                    var (statedVoice, statedRate) = VoiceValue(stated);
                    voice ??= statedVoice;
                    rate ??= statedRate;
                }
            }

            // The one place a picture named by the *explicit* grammar lands,
            // so the four spellings of it cannot disagree — which is precisely
            // the bug CB-139 was. A bullet assigned its value whole while the
            // voice arm three lines above it stripped code spans and the prose
            // arm normalised paths, so every profile that wrote its path in a
            // code span — which is how real ones write it — kept the backticks
            // and failed every path guard downstream in silence.
            //
            // Returns whether the line was *claimed*, not whether it produced a
            // path: a line carrying a recognised picture label has been read
            // whatever the value turned out to be, and offering it on to the
            // prose arm afterwards would be reading it twice.
            bool ExplicitAvatar(string label, string value)
            {
                if (!AvatarLabel(label) || !Valid(value)) return false;

                rawAvatar ??= value;
                avatar ??= ExplicitAvatarValue(value);
                return true;
            }

            // A name written as a standalone bold field or a two-cell table
            // row, which the two arms below share (CB-142). One function
            // rather than two copies, for the reason `ExplicitAvatar` above is
            // one function: the arms agree about every part of this — the
            // label list, the bound, first-statement-wins — and two copies of
            // an agreement is one copy that can drift.
            //
            // Every guard is asked here rather than at the call sites so that
            // neither arm can acquire one and not the other. `sectionLevel`
            // first because it is the cheaper question and the one doing the
            // work: `NameValue` accepts the word "string", so a schema table's
            // `| Name | string |` is refused by scope alone.
            //
            // **There is no `inFence` check here, and there used to be.**
            // CB-142 added one, because a `## Persona` section whose fenced
            // example reads `| Name | Aurora |` must not rename an orb. It was
            // the right rule in the wrong place: a *new* arm guarding itself
            // while every older arm leaked, which CB-144 then measured as four
            // separate holes including the bullet arm this function never
            // touches. The rule now lives once, above, as `if (inFence &&
            // !inMarkedYaml) continue;`, and every arm gets it. Do not
            // reintroduce a copy here — one rule in one place is the whole
            // point, and a second copy is the thing that drifts.
            //
            // Returns whether the line was *claimed*, which unlike
            // `ExplicitAvatar` means "a name came out of it". A recognised
            // label whose value fails the bound falls through to the arms
            // below instead, exactly as the front-matter arm lets it — nothing
            // down there recognises `Name` as a voice or a picture, so the
            // line ends up read by nobody, which is what a refusal means.
            bool ScopedName(string label, string value)
            {
                if (sectionLevel <= 0 || !NameLabel(label)) return false;
                if (NameValue(value) is not { } stated) return false;

                name ??= stated;
                return true;
            }

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
                // explicit arms below go on to read. Only the three arms added
                // for a CLAUDE.md act on it — the prose sentence, the persona
                // heading and the colon-less field — so this changes nothing
                // about what a bullet or a table row means, which is
                // deliberate: what OpenClaw's profiles already parse to is not
                // this feature's to move.
                //
                // CB-141 adds a fourth reader of that state rather than a
                // fifth exception to it: the front-matter arm, which now also
                // runs on a `yaml` fence inside a marked persona block. It is
                // purely additive — a line that arm does not claim falls
                // through to exactly the arms it fell through to before.
                if (Fence(trimmed))
                {
                    // The info string is read only when the fence opens.
                    // CommonMark forbids one on a closing fence, so this loses
                    // nothing.
                    //
                    // Clearing it on the way out is hygiene rather than a load-
                    // bearing rule, and saying so is the point: nothing reads
                    // fenceInfo while inFence is false, and the next opening
                    // fence overwrites it regardless — so a stale value cannot
                    // be observed today. It is cleared anyway because the
                    // invariant "this describes the fence we are in" is the one
                    // a later reader will assume, and leaving a closed fence's
                    // language lying around is how that assumption stops being
                    // true without anybody changing this line.
                    fenceInfo = inFence ? "" : FenceInfo(trimmed);
                    inFence = !inFence;
                    continue;
                }

                // A marked persona block opens and closes here, and the gate
                // is `!inFence` for the same reason the heading arm has one:
                // inside a fence this is not a declaration but somebody's
                // *example* of one, in a CLAUDE.md that documents the format.
                // A marker inside front matter is refused for a duller
                // reason — front matter is already read as fields, so a region
                // opened there would be a region with nothing left to give.
                //
                // The line is consumed rather than offered on. Nothing below
                // reads it today (it is not a bullet, a two-cell table row, a
                // bold field or a sentence the prose regex will start on), so
                // this changes no outcome; it says what the line is.
                if (!inFrontMatter && !inFence && MarkedBlockMarker(trimmed, out var opensBlock))
                {
                    inMarkedBlock = opensBlock;
                    continue;
                }

                // A heading is where a persona section opens and where it
                // closes, and it carries no field of its own — `## Attributes`
                // labels what follows rather than stating it. Consuming the
                // line here loses nothing that was read before: a heading
                // cannot be a bullet, a two-cell table row or a bold field,
                // and the prose regex refuses anything starting with `#`.
                //
                // Inside a fence it is not a heading at all but a comment in
                // somebody's shell example, and inside front matter it is a
                // YAML comment.
                if (!inFrontMatter && !inFence && Heading(trimmed, out var level, out var heading))
                {
                    if (sectionLevel > 0 && level <= sectionLevel) sectionLevel = 0;
                    if (PersonaSection(heading)) sectionLevel = level;
                    continue;
                }

                // Each of the three explicit non-bullet arms now asks two
                // questions rather than one. A voice was the only field they
                // ever read, which was survivable while the bullet arm was the
                // only place a picture could be written and is not survivable
                // now that the bullet arm normalises: a user who writes
                // `avatar: portrait.png` in front matter and
                // `- Avatar: portrait.png` in a bullet has said the same thing
                // twice, and being told only one of them counts is the drift
                // the whole file exists to prevent.
                //
                // The one shape a fence is read through rather than skipped:
                // a `yaml`/`yml` block inside a marked persona block. That is
                // CB-141, and it is deliberately the *same* arm rather than a
                // second grammar — profile-gen's `claude-md` mode writes the
                // identical keys its `file` mode writes into real front
                // matter, so `name:`, `slug:`, `image:`, `image_animated:` and
                // `voice:` have to mean there exactly what they mean here. Two
                // readers of one tool's output is the drift this whole file
                // exists to prevent.
                //
                // All three conditions earn their place. Without `inFence`
                // this is just front matter; without `inMarkedBlock` every
                // ```yaml example on the machine becomes a persona
                // declaration, which is the false positive the ticket's
                // acceptance criterion 2 names; and without the info-string
                // check a ```bash block inside the region would be read as
                // metadata, when what the marker declares is a region that
                // *describes* a persona, not that every fence inside it is
                // YAML.
                var inMarkedYaml = inFence && inMarkedBlock && YamlInfo(fenceInfo);

                if ((inFrontMatter || inMarkedYaml) && FieldAfterColon(trimmed, out var yamlLabel, out var yamlValue))
                {
                    // Unquoted once, before any label asks, so `name:`,
                    // `slug:`, `voice:`, `image:` and `image_animated:` all
                    // get it without four copies of the same two-character
                    // trim. See the header comment for why this runs only
                    // here and not on a bullet, a bold field or a table cell.
                    yamlValue = Unquoted(yamlValue);

                    if (NameLabel(yamlLabel) && NameValue(yamlValue) is { } yamlName)
                    {
                        name ??= yamlName;
                        continue;
                    }

                    if (VoiceLabel(yamlLabel) && VoiceValue(yamlValue) is var (yamlVoice, yamlRate) && yamlVoice is not null)
                    {
                        voice ??= yamlVoice;
                        rate ??= yamlRate;
                        continue;
                    }

                    if (ExplicitAvatar(yamlLabel, yamlValue)) continue;
                }

                // **Everything else inside a fence is an example, not a
                // statement** (CB-144). One gate, here, rather than a
                // condition repeated on each arm below.
                //
                // **What preserves CB-141's marked block is the arm above, not
                // `!inMarkedYaml`.** That arm `continue`s every line it
                // claims, so no field-bearing `key: value` line inside a
                // marked `yaml` fence ever reaches this gate — broadening this
                // to a bare `if (inFence) continue;` passes all 3688 unit and
                // 532 integration tests, which is how that was established
                // rather than argued. The condition stays for two reasons that
                // are smaller than "it is the mechanism" and are the honest
                // ones: it makes the gate state its own rule instead of
                // relying on an invariant two arms away, so reordering the
                // arms cannot silently turn it into a CB-141 regression; and
                // it is load-bearing for exactly one shape, below.
                //
                // That shape is a *bulleted* line inside a marked yaml fence —
                // `- name: Aurora` between the markers. The front-matter arm
                // never claims it (it asks `FieldAfterColon` on the whole
                // line, and a bullet is not that), so it falls through to
                // here, and the two spellings genuinely disagree: with the
                // condition it names Aurora, without it nothing. Reading it is
                // the right answer — the markers are a writer saying this
                // region describes a persona, and `- name:` is a name
                // everywhere else in this grammar — so
                // `ABulletedNameInsideAMarkedYamlBlockIsStillRead` asserts it
                // and this sentence is no longer the only thing holding the
                // distinction.
                //
                // The scattered per-arm version of this was tried first and is
                // why the rule is written once. Guarding the standalone-bold
                // and table arms leaves the bullet arm wide open, because a
                // bullet calls `BoldField`/`FieldAfterColon` itself on
                // `trimmed[1..]` rather than going through them — so
                // `- name: Build the thing`, an ordinary GitHub Actions step
                // pasted into a CLAUDE.md, still renamed the orb after a build
                // step. Measured, on `develop`: `Name = "Build the thing"`.
                // Three more leaked the same way — `- **Voice:** af_bella`,
                // `| Voice | af_bella |`, `**Voice:** af_bella`.
                //
                // This is the file's own rule finally applied uniformly: "a
                // fenced block is where a CLAUDE.md *shows* you what to write,
                // and text being shown is not text being asserted." It was
                // true of the prose arm, the heading arm and the marker arm,
                // and false of every explicit field arm, for as long as those
                // arms have existed.
                //
                // **Do not add a fence check to the front-matter arm above.**
                // It runs while `inFence` is true on purpose; that is the
                // entire mechanism of CB-141's marked block, and a broad sweep
                // that "adds the guard everywhere" silently reverts it while
                // every test but the marked-block ones stays green.
                if (inFence && !inMarkedYaml) continue;

                if (TableField(trimmed, out var tableLabel, out var tableValue)
                    && (index + 1 >= source.Count || !TableSeparator(source[index + 1])))
                {
                    // Asked before the voice and picture arms purely for
                    // symmetry with the front-matter arm above; the three
                    // label lists are disjoint, so the order decides nothing.
                    if (ScopedName(tableLabel, tableValue)) continue;

                    if (VoiceLabel(tableLabel) && VoiceValue(tableValue) is var (tableVoice, tableRate) && tableVoice is not null)
                    {
                        voice ??= tableVoice;
                        rate ??= tableRate;
                        continue;
                    }

                    if (ExplicitAvatar(tableLabel, tableValue)) continue;
                }

                if (BoldField(trimmed, out var boldLabel, out var boldValue))
                {
                    if (ScopedName(boldLabel, boldValue)) continue;

                    if (VoiceLabel(boldLabel) && VoiceValue(boldValue) is var (boldVoice, boldRate) && boldVoice is not null)
                    {
                        voice ??= boldVoice;
                        rate ??= boldRate;
                        continue;
                    }

                    if (ExplicitAvatar(boldLabel, boldValue)) continue;
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

                    // Assign rather than a switch here: everything VoiceValue
                    // rejects — blank, a <placeholder> — is already outside
                    // what a prose value is allowed to contain, so ??= against
                    // a null it cannot produce is the whole of the handling
                    // rather than a missing check.
                    if (ProseField(trimmed, out var proseKind, out var proseValue))
                    {
                        Assign(proseKind, proseValue);
                        continue;
                    }

                    // The loosening, and the scope that keeps it safe. Outside
                    // a persona section this arm does not run, so a paragraph
                    // beginning "Name resolution is handled by the folder"
                    // means what it has always meant; inside one it is still
                    // refused, on the same word count that refuses it as
                    // prose.
                    if (sectionLevel > 0 && SectionField(trimmed, out var sectionKind, out var sectionValue))
                        Assign(sectionKind, sectionValue);

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

                if (name is null && NameLabel(label) && NameValue(fieldValue) is { } bulletName)
                    name = bulletName;
                else if (voice is null && VoiceLabel(label) && VoiceValue(fieldValue) is var (bulletVoice, bulletRate) && bulletVoice is not null)
                {
                    voice = bulletVoice;
                    rate ??= bulletRate;
                }
                else ExplicitAvatar(label, fieldValue);
            }

            return new Fields(name, voice, rate, avatar, rawAvatar);
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
            @"|profile\s+photo|name|voice|picture|portrait|avatar|image|photo)" +
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
                if (!BoundedVoice(stated, words)) return false;
                kind = ProseKind.Voice;
                value = stated;
                return true;
            }

            // The same helper the explicit arms run, not a paraphrase of it.
            // CLAUDE.md states that rule outright — the parsers here are pure
            // and cheap to call precisely so nobody restates their answer — and
            // this arm is where the drift CB-139 fixed started: two readings of
            // "names a picture", each correct on its own, disagreeing about
            // every value a real profile writes.
            var picture = AvatarValue(stated);
            if (picture is null) return false;

            kind = ProseKind.Avatar;
            value = picture;
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

        // A voice value, which is a name or a mixture of them.
        //
        // The whitelist above has no `%` in it, and that — not the word count
        // — is what actually refused this repository's own persona line: a
        // two-word `Voice is 50% sky` was rejected too, well inside the cap.
        // Both barriers had to move and only one of them was ever suspected,
        // which is why the second arm here is written as its own bound rather
        // than as three more characters added to the first.
        //
        // Order matters and only for cost: an ordinary voice name passes the
        // narrow test and never reaches the regex behind VoiceBlend.Parse.
        internal static bool BoundedVoice(string value, string[] words) =>
            BoundedWords(value, words) || BlendShaped(value, words);

        private static bool BlendShaped(string value, string[] words)
        {
            // **A percentage is required, and it is the whole of the intent
            // signal.** Without this line "Her voice is lovely and warm and
            // low" is a three-part equal blend of three single tokens — five
            // words inside the eleven-word cap, every character on the
            // whitelist, and structurally indistinguishable from "sky, nicole
            // and bella". Nothing can tell those two apart without knowing
            // which words are voices, and the parser deliberately does not.
            // A `%` is what nobody writes by accident in a sentence about a
            // voice, so it is what buys the extra eight words.
            //
            // The cost is stated rather than hidden: a *weightless* blend of
            // three or four parts is over the prose word cap and is not read
            // as prose. Two parts still are — `Voice is sky and nicole` fits
            // the narrow bound unchanged and the blend layer claims it
            // downstream — and any number of parts can be written under the
            // explicit `- Voice:` grammar, which has never had a word cap.
            if (!value.Contains('%')) return false;

            if (value.Length > MaxVoiceValueLength) return false;
            if (words.Length > MaxVoiceWords) return false;
            if (value.Contains(':') || value.Contains('/')) return false;
            if (value.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;

            // The same whitelist as a name's, plus the three marks a mixture
            // is written with. Parentheses are deliberately *not* here: an
            // engine annotation belongs to a single voice, and a blend
            // carrying one is a shape nobody writes and this does not have to
            // guess at.
            if (!value.All(c =>
                    char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '\'' or '%' or ',' or '+'))
            {
                return false;
            }

            // The bound that does the real work — see the comment on
            // MaxVoiceWords. Running the grammar rather than paraphrasing it
            // is also the rule CLAUDE.md states outright: the parser is pure
            // and cheap to call precisely so nobody restates its answer here.
            return VoiceBlend.Parse(value) is not null;
        }

        // A picture, as a *sentence* is allowed to name one — "the file
        // leota.png" names the same picture "leota.png" does, because a path
        // is one token and the words in front of it are somebody being
        // polite about it. See Picture for what this and ExplicitAvatarValue
        // share and the one thing they disagree about.
        internal static string? AvatarValue(string value) => Picture(value, whole: false);

        // A picture, as a *labelled* field states one — front matter, a
        // bullet, a bold field, a table cell, and the colon-less section
        // form. The value is the whole of what was written, not a sentence
        // to search: nobody who typed `image:` and then a path meant for
        // only its last word to count.
        internal static string? ExplicitAvatarValue(string value) => Picture(value, whole: true);

        // The one function both readers share, and the one place they part
        // company. See the header comment's two paragraphs on this — the
        // asymmetry is the design, not an inconsistency.
        //
        // The decorations come off first, for both readers alike. A code
        // span around the path itself is real fixture rather than a
        // coincidence, the same as it is for a voice: every picture line
        // captured off the Mac mini in CB-139 wore one, and most of them wore
        // a note after it as well — `` `avatars/annabel-lee.gif` (animated,
        // updated 2026-09-09) ``. The two are stripped in a loop rather than
        // in a fixed order because the real shapes disagree about which is
        // outermost: that line wears the note outside the span, and
        // `` `avatars/annabel-lee.gif (animated)` `` wears it inside. Each
        // strip strictly shortens the string, so the loop cannot fail to end.
        //
        // Then the split. `whole` skips it outright — a labelled field is
        // taken as written — and so does an *un*labelled sentence whose
        // candidate, taken whole, is already rooted: that is what lets
        // "Her picture is /Users/w/My Docs/x.png" read correctly, because
        // splitting it on whitespace first and asking about the last token
        // only would have thrown the directory away. Short of that, a
        // sentence still gives up its last token, so "the file leota.png"
        // keeps meaning `leota.png` — but if any *other* token in that
        // sentence is itself rooted, the value is refused outright rather
        // than truncated to a token after it: "the file /a b/x.png" must not
        // quietly become a search for a relative "b/x.png" that nobody
        // wrote. That fail-open shape, reachable through every one of the
        // seven grammar arms this parser has, was CB-140's sharpest defect.
        //
        // What is deliberately *not* asked here any more is whether the
        // result is rooted. A rooted path used to be refused on sight,
        // before the filesystem was consulted at all — which is also what
        // silently defeated the guard above, because "reject anything
        // rooted" and "take the last whitespace-separated token" fight each
        // other the moment a directory has a space in it. Containment is the
        // property that was ever worth having, and containment is a question
        // about where a file sits relative to another one, which only
        // PersonaFiles can answer — see the header comment's first
        // paragraph on this.
        //
        // A colon still refuses a value outright, because that has nothing
        // to do with rootedness: `https://x/y.png` and a `data:` URI are
        // refused deliberately, not incidentally, per the header comment —
        // except that an absolute path is now legal and Windows spells one
        // with a colon, so ColonIsADriveLetter is asked rather than the bare
        // character test this used to be.
        private static string? Picture(string value, bool whole)
        {
            var candidate = value.Trim();

            while (true)
            {
                var before = candidate;
                candidate = WithoutTrailingParenthetical(candidate);
                candidate = WithoutCodeSpan(candidate);
                if (string.Equals(candidate, before, StringComparison.Ordinal)) break;
            }

            string last;
            if (whole || Path.IsPathRooted(candidate))
            {
                last = candidate;
            }
            else
            {
                var words = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return null;

                // A fragment of the sentence other than the last word is
                // itself a rooted path — refuse rather than guess which word
                // was meant to be the filename.
                if (words.Length > 1 && words.Any(Path.IsPathRooted)) return null;

                last = words[^1];
            }

            // The full stop first and the span again after it: "see
            // `avatars/leota.png`." wears its span around the *token* rather
            // than around the value, so the loop above cannot have reached
            // it, and the stop sits outside the closing tick. Asking twice
            // is a line; teaching the loop about tokens is a second grammar.
            last = WithoutCodeSpan(last.TrimEnd('.'));
            if (last.Length == 0) return null;
            if (!ColonIsADriveLetter(last)) return null;

            return PictureExtensions.Any(
                extension => last.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                ? last
                : null;
        }

        // A colon is legal in exactly one shape a picture path takes: a
        // Windows drive letter, `C:\...` or `C:/...`. Refusing every other
        // colon is what keeps `https://x/y.png` and a `data:` URI out —
        // that refusal is CB-139's and it is deliberate, not incidental: a
        // persona picture is a local path, and the gateway's `avatarUrl` is
        // the only place base64 is accepted, because it arrives over the
        // wire rather than out of a file anybody can commit. PersonaFiles
        // refuses all of it again — this is the cheap half of a check that
        // has to hold in both places, not the only half.
        //
        // `://` is checked before the drive-letter shape is, and has to be:
        // `a://x.png` satisfies "single ASCII letter, colon, then a slash"
        // exactly as `C:/x.png` does, and only the second is a path. A
        // second colon anywhere after the first is never a drive letter
        // either — `C:\a:b.png` — so that is refused outright too.
        private static bool ColonIsADriveLetter(string candidate)
        {
            var colon = candidate.IndexOf(':');
            if (colon < 0) return true;
            if (candidate.Contains("://", StringComparison.Ordinal)) return false;
            if (candidate.IndexOf(':', colon + 1) >= 0) return false;
            return colon == 1 && char.IsAsciiLetter(candidate[0])
                && candidate.Length > 2 && candidate[2] is '\\' or '/';
        }

        // Stripped only when both ticks are there and there is something left
        // between them, so a value that is nothing *but* a lone backtick is
        // left alone rather than emptied — the same rule, character for
        // character, that VoiceValue applies to a voice identifier.
        private static string WithoutCodeSpan(string candidate) =>
            candidate.Length > 2 && candidate.StartsWith('`') && candidate.EndsWith('`')
                ? candidate[1..^1].Trim()
                : candidate;

        // Any parenthesised trailer, not only the engine names VoiceValue
        // knows. A voice annotation has to be recognised because the
        // parenthesis is sometimes part of the identifier ("Ava (Premium)");
        // no filename ends in one, so there is nothing here to protect and a
        // whitelist would only be a list of the notes people had happened to
        // write so far. `open > 0` leaves a value that is *nothing but* a
        // parenthetical alone, which then fails the extension test on its own
        // rather than being emptied first.
        private static string WithoutTrailingParenthetical(string candidate)
        {
            if (!candidate.EndsWith(")", StringComparison.Ordinal)) return candidate;

            var open = candidate.LastIndexOf('(');
            return open > 0 ? candidate[..open].TrimEnd() : candidate;
        }

        // A Markdown ATX heading, and which level it is.
        //
        // Setext headings (a line of `===` or `---` underneath the text) are
        // deliberately not read: the `---` spelling is already front matter's
        // fence in this parser, and a persona file that writes its attributes
        // heading that way is rarer than a file that would have its front
        // matter silently reinterpreted.
        //
        // `#hashtag` is not a heading — CommonMark requires whitespace after
        // the run of hashes — and seven hashes is not one either.
        internal static bool Heading(string trimmed, out int level, out string text)
        {
            level = 0;
            text = "";

            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is 0 or > 6) return false;

            var rest = trimmed[hashes..];
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return false;

            level = hashes;

            // The closing run of an ATX heading (`## Attributes ##`) is
            // decoration rather than text.
            text = rest.Trim().TrimEnd('#').Trim();
            return true;
        }

        // Which headings open a section where a bare `Label Value` line is
        // metadata rather than prose.
        //
        // Contains rather than equals, because nobody writes a heading that is
        // exactly the word: "## Attributes", "## Her persona", "# About me"
        // and "## Agent profile" are all the same declaration, and a rule that
        // reads only one spelling of it is a rule people have to learn.
        // Deliberately a short, closed list — every word here names a section
        // whose whole purpose is to describe the agent, which is what makes
        // reading its lines as fields defensible.
        private static readonly string[] SectionWords =
            { "persona", "attributes", "identity", "character", "profile", "about me", "who i am" };

        internal static bool PersonaSection(string heading) =>
            SectionWords.Any(word => heading.Contains(word, StringComparison.OrdinalIgnoreCase));

        // Every label this grammar knows is one or two words.
        private const int MaxLabelWords = 2;

        // `Name Jennifer`, `Profile Photo cto.png` — a recognised label, a
        // space, and a value that has to survive the same bounds every other
        // arm applies.
        //
        // Longest label first, so `Profile Photo cto.png` is read as a photo
        // rather than as an unrecognised "Profile" — the same reason the prose
        // regex spells its two-word nouns before its one-word ones.
        //
        // A label followed by a colon is not this shape and never reaches
        // here as one: "Name:" is not "Name", so `Name: prose is not metadata`
        // is refused by the label match itself and keeps meaning what
        // OnlyExplicitBulletFieldsAreReadAndPlaceholdersDoNotWin has said it
        // means since before prose was read at all.
        internal static bool SectionField(string trimmed, out ProseKind kind, out string value)
        {
            kind = ProseKind.None;
            value = "";

            var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return false;

            for (var length = Math.Min(MaxLabelWords, words.Length - 1); length >= 1; length--)
            {
                if (!SectionLabel(string.Join(' ', words.Take(length)), out var labelled)) continue;

                var rest = words[length..];

                if (labelled is ProseKind.Avatar)
                {
                    // A label and a value, not a sentence — the same reason
                    // the bullet and front-matter arms take a picture whole
                    // rather than as a last token to search for.
                    var picture = ExplicitAvatarValue(string.Join(' ', rest));
                    if (picture is null) return false;
                    kind = labelled;
                    value = picture;
                    return true;
                }

                // A voice gets the wider bound here for the same reason it
                // gets it in the prose arm: `Voice 50% sky and 50% nicole`
                // under `## Attributes` is the same statement as the sentence
                // with "is" in it, and a grammar that reads one and not the
                // other is a rule people have to learn.
                var stated = string.Join(' ', rest);
                var bounded = labelled is ProseKind.Voice
                    ? BoundedVoice(stated, rest)
                    : BoundedWords(stated, rest);

                if (!bounded) return false;

                kind = labelled;
                value = stated;
                return true;
            }

            return false;
        }

        // The label lists the explicit grammar already keeps, asked as one
        // question. Nothing new is recognised here that a bullet would not
        // recognise — which is the point: a user who writes `- Name: Jennifer`
        // in one file and `Name Jennifer` under `## Attributes` in another has
        // said the same thing twice.
        internal static bool SectionLabel(string label, out ProseKind kind)
        {
            if (NameLabel(label))
            {
                kind = ProseKind.Name;
                return true;
            }

            if (VoiceLabel(label))
            {
                kind = ProseKind.Voice;
                return true;
            }

            if (AvatarLabel(label))
            {
                kind = ProseKind.Avatar;
                return true;
            }

            kind = ProseKind.None;
            return false;
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

        // ...except that the language *is* examined now, on the way in, for
        // the one case in Parse that reads a fenced block rather than skipping
        // it. Only the first word is taken: CommonMark lets a fence carry
        // attributes after its language ("```yaml {.wrap}", "```yaml title=x")
        // and the language is the part that says what the block holds.
        //
        // Callers get "" for a bare fence, which is not a language any caller
        // recognises — so an undeclared block is skipped exactly as it always
        // was.
        private static string FenceInfo(string trimmed)
        {
            var marker = trimmed[0];
            var i = 0;
            while (i < trimmed.Length && trimmed[i] == marker) i++;

            var info = trimmed[i..].Trim();
            var space = info.IndexOfAny(InfoBreak);
            return space < 0 ? info : info[..space];
        }

        private static readonly char[] InfoBreak = { ' ', '\t' };

        // Which fenced blocks hold YAML. Both spellings, because a person
        // writing one by hand picks whichever they learned and a tool picks
        // the other, and a grammar that reads only one of them is a rule
        // people have to learn — the same argument NameLabel makes for
        // `name:` and `slug:`.
        private static bool YamlInfo(string info) =>
            info.Equals("yaml", StringComparison.OrdinalIgnoreCase)
            || info.Equals("yml", StringComparison.OrdinalIgnoreCase);

        // The two spellings of the marker that delimits a persona block.
        //
        // `profile-gen` is the tool that actually emits one today: its
        // `claude-md` output mode wraps the whole persona between
        // `<!-- profile-gen:start slug=... -->` and `<!-- profile-gen:end
        // slug=... -->`, with the fields inside a ```yaml fence in between.
        // `persona` is the same shape with the tool's name taken out of it,
        // and it is here so that what this repository promises to read is "a
        // marked persona block" rather than "whatever profile-gen happens to
        // emit". A second tool — or a person writing one by hand — should not
        // have to spell another project's name to be understood.
        private static readonly string[] BlockMarkerTools = { "profile-gen", "persona" };

        // `<!-- profile-gen:start slug=aurora-vance -->` — an HTML comment
        // whose first word is `<tool>:start` or `<tool>:end`. Whatever follows
        // it (profile-gen writes a `slug=`) is the tool's own bookkeeping and
        // is deliberately not read: this parser has no use for a slug it did
        // not ask for, and a marker whose trailing attributes it refused to
        // understand would be a marker that stops working the next time the
        // tool adds one.
        //
        // Why a marker at all, when CB-135 already models a persona *section*
        // and a section would be the tidier signal: profile-gen's block opens
        // with `### <the persona's name>`, and PersonaSection matches only
        // persona/attributes/identity/character/profile/about me/who i am. A
        // heading reading `### Aurora Vance` opens no section, so sectionLevel
        // is zero for the whole block and every section-scoped rule is dead
        // there. Measured by running PersonaSection against the real
        // template's real output, not by reading the list.
        //
        // What the marker buys over a section is also a genuine *end*. A
        // section ends at the next heading of its own level or higher, which
        // is a guess about where the author stopped; `:end` is the author
        // saying so.
        private static bool MarkedBlockMarker(string trimmed, out bool opens)
        {
            opens = false;

            // Length before the slice: `<!-->` satisfies both ends at once by
            // overlapping in the middle, and `trimmed[4..^3]` on five
            // characters throws rather than returning nothing.
            if (trimmed.Length < 7
                || !trimmed.StartsWith("<!--", StringComparison.Ordinal)
                || !trimmed.EndsWith("-->", StringComparison.Ordinal))
            {
                return false;
            }

            var content = trimmed[4..^3].Trim();
            var space = content.IndexOfAny(InfoBreak);
            var directive = space < 0 ? content : content[..space];

            var colon = directive.IndexOf(':');
            if (colon <= 0) return false;

            var tool = directive[..colon];
            if (!BlockMarkerTools.Any(known => tool.Equals(known, StringComparison.OrdinalIgnoreCase)))
                return false;

            var action = directive[(colon + 1)..];
            if (action.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                opens = true;
                return true;
            }

            return action.Equals("end", StringComparison.OrdinalIgnoreCase);
        }

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

        // `name:` and `slug:` both name the agent, shared by the front-matter
        // arm, the bullet arm and SectionLabel so the three cannot drift the
        // way the picture readers once did. `slug:` is what a generator
        // writes when it has no display name yet; `name:` wins the tie
        // wherever both are present, because it is asked first and every
        // assignment here is `??=`.
        internal static bool NameLabel(string label) =>
            label.Equals("Name", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Slug", StringComparison.OrdinalIgnoreCase);

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
        //
        // `image_animated` (and its spaced spelling, for a bullet or a bold
        // field) is a *label* like any other here, not a different field —
        // see the header comment for why recognising it is safe without
        // giving a persona a second picture to hold.
        internal static bool AvatarLabel(string label) =>
            label.Equals("Avatar", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Picture", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Pic", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Image", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Profile Photo", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Picture", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Photo", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Portrait", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Image", StringComparison.OrdinalIgnoreCase)
            || label.Equals("image_animated", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Image Animated", StringComparison.OrdinalIgnoreCase);

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

        // A name, wherever the front-matter arm or the bullet arm found one —
        // one function so the two cannot judge the same value differently.
        // The bound is `BoundedWords`, the same one a prose or a colon-less
        // name passes: before CB-140 the bullet arm was the only place in
        // this grammar that read a name with no bound of its own, which is
        // how an ordinary `- **Name**: Jordan Casey, MBA / MSc` — five
        // words, a comma and a slash — was read as a persona's.
        internal static string? NameValue(string value)
        {
            var candidate = value.Trim();
            var words = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return Valid(candidate) && BoundedWords(candidate, words) ? candidate : null;
        }

        // A YAML scalar's own quoting, stripped once and only for a
        // front-matter value: `name: "Aurora"` is YAML saying a string is a
        // string, and the quotes are the format's syntax rather than
        // something the writer meant to name their agent with. A matched
        // pair is required — an unclosed quote is left exactly as written,
        // the same "read what is there rather than guess what was meant"
        // rule AvatarValue's code-span strip already follows — and a bullet,
        // a bold field or a table cell never reaches this at all: a quote
        // there is Markdown a person typed, not YAML.
        private static string Unquoted(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length < 2) return trimmed;

            var first = trimmed[0];
            var last = trimmed[^1];
            return (first == '"' && last == '"') || (first == '\'' && last == '\'')
                ? trimmed[1..^1].Trim()
                : trimmed;
        }
    }
}
