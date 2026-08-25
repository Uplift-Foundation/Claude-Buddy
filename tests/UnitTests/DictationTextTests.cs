using Xunit;

namespace ClaudeBuddy.Tests
{
    // The two text passes that turn what Whisper heard into what gets typed.
    //
    // Both are pure string work and both were private, which is the only reason
    // they had no tests — there is no model file, no microphone and no audio
    // anywhere in this. That matters because these rules are the kind that look
    // right in one example and wrong in the next, and the source's comments
    // record a shipped build that produced "fix this.. then".
    public class SpokenPunctuationTests
    {
        // --- StripNonSpeechTags: annotations are never words the user said ---

        [Theory]
        [InlineData("[BLANK_AUDIO] fix the test", "fix the test")]
        [InlineData("fix the test [inaudible]", "fix the test")]
        [InlineData("(laughter) fix the test", "fix the test")]
        [InlineData("fix (background noise) the test", "fix the test")]
        [InlineData("[MUSIC PLAYING] fix [coughs] the test", "fix the test")]
        public void AnnotationsDescribingTheAudioAreRemoved(string heard, string want)
        {
            // Compared with runs of whitespace collapsed, because what this test
            // is about is that the annotation is gone. The exact spacing left
            // behind is a separate question, pinned in the next test rather than
            // smuggled into every row of this one.
            var got = System.Text.RegularExpressions.Regex.Replace(
                SpeechTranscriber.StripNonSpeechTags(heard).Trim(), @"\s+", " ");

            Assert.Equal(want, got);
        }

        // A wart, recorded rather than asserted away: each annotation is
        // replaced by a single space, so one in the *middle* of a sentence
        // leaves two spaces behind. Nothing downstream collapses them —
        // ApplySpokenPunctuation only tightens whitespace that sits before a
        // punctuation mark — so this reaches the text box as typed here.
        //
        // Harmless enough to leave alone and worth a test either way: it is the
        // sort of thing that gets "fixed" by adding a Trim() somewhere that
        // silently changes the empty-clip case, which callers rely on to mean
        // "nothing was said".
        [Fact]
        public void AnAnnotationMidSentenceLeavesADoubleSpace()
        {
            Assert.Equal(
                "fix  the test",
                SpeechTranscriber.StripNonSpeechTags("fix (background noise) the test").Trim());
        }

        // A clip that was *only* an annotation comes back empty, which the
        // source says is exactly the "nothing was said" signal callers already
        // handle. Asserted so it stays that rather than becoming whitespace
        // somebody later trims into a real empty turn by accident.
        [Theory]
        [InlineData("[BLANK_AUDIO]")]
        [InlineData("(silence)")]
        public void AClipThatWasOnlyAnAnnotationComesBackEmpty(string heard)
        {
            Assert.Equal("", SpeechTranscriber.StripNonSpeechTags(heard).Trim());
        }

        // The bracket contents are bounded to 40 characters and to a limited
        // character set, so a real parenthetical the user actually spoke is left
        // alone rather than silently deleted. This is the boundary that keeps
        // the strip from eating speech.
        [Fact]
        public void ALongParentheticalIsNotMistakenForAnAnnotation()
        {
            var spoken = "the fix (which took rather longer than anyone had hoped, frankly) landed";

            Assert.Equal(spoken, SpeechTranscriber.StripNonSpeechTags(spoken));
        }

        [Fact]
        public void TextWithNoAnnotationsIsUnchanged()
        {
            var spoken = "fix the arrangement test and push it";

            Assert.Equal(spoken, SpeechTranscriber.StripNonSpeechTags(spoken));
        }

        // --- ApplySpokenPunctuation: the mapping dictation engines apply ---

        // The spacing rule, which is the whole reason the leading whitespace is
        // absorbed rather than the trailing: "fix comma then" has to become
        // "fix, then" — not "fix,then" and not "fix , then".
        [Fact]
        public void PunctuationAbsorbsTheSpaceBeforeItAndNotAfter()
        {
            Assert.Equal("fix, then", SpeechTranscriber.ApplySpokenPunctuation("fix comma then"));
        }

        [Theory]
        [InlineData("that works period", "that works.")]
        [InlineData("that works full stop", "that works.")]
        [InlineData("does it question mark", "does it?")]
        [InlineData("yes exclamation point", "yes!")]
        [InlineData("yes exclamation mark", "yes!")]
        [InlineData("wait colon this", "wait: this")]
        [InlineData("wait semicolon this", "wait; this")]
        public void EachSpokenMarkBecomesItsSymbol(string heard, string want)
        {
            Assert.Equal(want, SpeechTranscriber.ApplySpokenPunctuation(heard));
        }

        // Note the space after the opening bracket, which is the leading-space
        // rule showing its edge. Absorbing the space *before* a mark and leaving
        // whatever followed is right for the marks that close a phrase — "fix,
        // then" — and wrong for the ones that open one, because the space that
        // used to separate "paren" from "aside" is exactly the space you do not
        // want. Asserted as it behaves rather than as it ideally reads: this is
        // a real, visible-in-the-textbox blemish, and a test claiming "(aside)"
        // would be describing code nobody has written.
        [Theory]
        [InlineData("open paren aside close paren", "( aside)")]
        [InlineData("open parenthesis aside close parenthesis", "( aside)")]
        public void SpokenBracketsBecomeBrackets(string heard, string want)
        {
            Assert.Equal(want, SpeechTranscriber.ApplySpokenPunctuation(heard));
        }

        // Same edge as the opening bracket above: the break replaces the space
        // in front of it and the one behind it survives, so the next line starts
        // indented by one space.
        [Fact]
        public void NewLineAndNewParagraphBecomeRealBreaks()
        {
            Assert.Equal("one\n two", SpeechTranscriber.ApplySpokenPunctuation("one new line two"));
            Assert.Equal(
                "one\n\n two", SpeechTranscriber.ApplySpokenPunctuation("one new paragraph two"));
        }

        // Longer phrases are tried first, and this is the case that proves it:
        // if "exclamation" were matched before "exclamation point", the word
        // "point" would be left stranded in the output.
        [Fact]
        public void ALongerPhraseIsMatchedBeforeItsPrefix()
        {
            var got = SpeechTranscriber.ApplySpokenPunctuation("stop that exclamation point");

            Assert.Equal("stop that!", got);
            Assert.DoesNotContain("point", got);
        }

        // Whole-word only, so an ordinary word that merely contains a mark's
        // name is untouched. Without the word boundary, "exposition" would come
        // out as "ex:ition" and "periodically" as ".ically" — the pass would be
        // corrupting ordinary speech rather than punctuating it.
        [Theory]
        [InlineData("the exposition was long")]
        [InlineData("a colonial power")]
        [InlineData("periodically checked")]
        public void AWordThatMerelyContainsAMarksNameIsUntouched(string spoken)
        {
            Assert.Equal(spoken, SpeechTranscriber.ApplySpokenPunctuation(spoken));
        }

        // The documented trade-off, pinned so it stays a decision rather than
        // becoming a surprise: "the trial period" said as three words becomes
        // "the trial." like every other dictation system's version of this.
        // There is no way to tell "spoken as punctuation" from "spoken as the
        // actual word" from the audio alone.
        [Fact]
        public void TheWordPeriodSpokenAsAWordStillBecomesAFullStop()
        {
            Assert.Equal("the trial.", SpeechTranscriber.ApplySpokenPunctuation("the trial period"));
        }

        // The bug the collapse exists for. Whisper predicts its own punctuation
        // from the *pause* in the audio, and saying "period" is both a word and
        // a natural place to pause — so it lands a real "." at the same spot the
        // substitution puts one, and the result was doubled marks scattered
        // through the text rather than only at the end.
        [Theory]
        [InlineData("fix this. period then", "fix this. then")]
        [InlineData("fix this . . then", "fix this. then")]
        [InlineData("fix this.. then", "fix this. then")]
        [InlineData("really?? yes", "really? yes")]
        [InlineData("wait,, then", "wait, then")]
        public void ARunOfTheSameMarkCollapsesToOne(string heard, string want)
        {
            Assert.Equal(want, SpeechTranscriber.ApplySpokenPunctuation(heard));
        }

        // Different marks side by side are not a run and must survive — "?!" is
        // something a person means.
        [Fact]
        public void TwoDifferentMarksAreNotCollapsed()
        {
            Assert.Equal("really?!", SpeechTranscriber.ApplySpokenPunctuation("really?!"));
        }

        [Fact]
        public void CaseDoesNotMatter()
        {
            Assert.Equal("that works.", SpeechTranscriber.ApplySpokenPunctuation("that works Period"));
            Assert.Equal("does it?", SpeechTranscriber.ApplySpokenPunctuation("does it QUESTION MARK"));
        }

        [Fact]
        public void TextWithNoSpokenPunctuationIsOnlyTrimmed()
        {
            Assert.Equal(
                "fix the arrangement test",
                SpeechTranscriber.ApplySpokenPunctuation("  fix the arrangement test  "));
        }

        [Fact]
        public void EmptyTextSurvives()
        {
            Assert.Equal("", SpeechTranscriber.ApplySpokenPunctuation(""));
            Assert.Equal("", SpeechTranscriber.ApplySpokenPunctuation("   "));
        }
    }
}
