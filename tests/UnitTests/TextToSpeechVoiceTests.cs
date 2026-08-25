using Xunit;
using static ClaudeBuddy.TextToSpeech;

namespace ClaudeBuddy.Tests
{
    // Which voice speaks, and which voices are offered.
    //
    // None of this launches a process. Enumerating the machine's voices does —
    // `say -v ?` on macOS, PowerShell on Windows, the user's own listing command
    // — and that half stays excluded; what is here is the parsing of what those
    // print, the order they are offered in, and the resolution of a saved choice
    // against what is actually available.
    //
    // The resolution is the part worth having. A saved selection can name an
    // engine that has since been uninstalled or a voice that no longer exists,
    // and the rule is that speaking in the wrong voice beats not speaking — so
    // every fallback below is deliberate, and a "fix" that returned null instead
    // would silently stop the app talking.
    [Collection("Settings")]
    public class TextToSpeechVoiceTests
    {
        // A real `say -v ?` answer. The alignment is genuine: the columns are
        // space-padded to different widths per row, which is exactly why the
        // parser cannot split on a fixed column.
        private const string SayOutput = """
            Albert              en_US    # Hello! My name is Albert.
            Ava (Premium)       en_US    # Hello! My name is Ava.
            Alice               it_IT    # Ciao! Mi chiamo Alice.
            Daniel (Enhanced)   en_GB    # Hello! My name is Daniel.
            Zoe (Premium)       en_US    # Hello! My name is Zoe.
            Amélie              fr_CA    # Bonjour! Je m’appelle Amélie.
            """;

        // --- ParseSayVoices ---

        [Fact]
        public void EnglishVoicesAreTakenFromTheListing()
        {
            var voices = ParseSayVoices(SayOutput);

            Assert.Equal(
                new[] { "Albert", "Ava (Premium)", "Daniel (Enhanced)", "Zoe (Premium)" }, voices);
        }

        // Non-English voices are dropped, which is the `en_` filter. Worth
        // asserting rather than assuming: a picker offering Amélie for English
        // text produces confident nonsense rather than an error.
        [Fact]
        public void VoicesForOtherLanguagesAreLeftOut()
        {
            var voices = ParseSayVoices(SayOutput);

            Assert.DoesNotContain("Alice", voices);
            Assert.DoesNotContain("Amélie", voices);
        }

        // The name keeps its parenthesised quality tier, because that is part of
        // the name `say -v` accepts. Dropping it would produce a name the engine
        // then refuses.
        [Fact]
        public void TheQualityTierStaysPartOfTheName()
        {
            Assert.Contains("Ava (Premium)", ParseSayVoices(SayOutput));
        }

        // The sample sentence after '#' is stripped *first*, which matters
        // because it contains the voice's own name and, in some locales, tokens
        // that look like a locale. Parsing the locale before stripping it would
        // read the wrong token.
        [Fact]
        public void TheSampleSentenceCannotBeMistakenForTheLocale()
        {
            var voices = ParseSayVoices(
                "Fred                en_US    # I live in en_GB now and my name is en_AU.");

            Assert.Equal(new[] { "Fred" }, voices);
        }

        [Theory]
        [InlineData("")]
        [InlineData("\n\n")]
        [InlineData("Albert")]                              // no locale column
        [InlineData("en_US    # orphaned locale")]          // no name before it
        public void ALineThatIsNotAVoiceIsSkipped(string output)
        {
            Assert.Empty(ParseSayVoices(output));
        }

        // A line with no sample text at all still parses — `say -v ?` has been
        // seen without one, and a voice is not worth losing over a missing
        // comment.
        [Fact]
        public void ALineWithNoSampleTextStillParses()
        {
            Assert.Equal(new[] { "Albert" }, ParseSayVoices("Albert   en_US"));
        }

        // --- OrderVoices ---

        // Premium and Enhanced first, because they are what people want, then
        // alphabetical within each tier so the list is predictable rather than
        // in whatever order the OS happened to answer.
        [Fact]
        public void TheBetterVoicesAreOfferedFirst()
        {
            var voices = new List<string>
            {
                "Zoe", "Albert (Enhanced)", "Ava (Premium)", "Bob", "Zara (Premium)",
            };

            OrderVoices(voices);

            Assert.Equal(
                new[] { "Ava (Premium)", "Zara (Premium)", "Albert (Enhanced)", "Bob", "Zoe" },
                voices);
        }

        [Fact]
        public void VoicesInOneTierAreAlphabeticalRegardlessOfCase()
        {
            var voices = new List<string> { "zoe", "Albert", "bob" };

            OrderVoices(voices);

            Assert.Equal(new[] { "Albert", "bob", "zoe" }, voices);
        }

        // --- SelectedFrom: resolving a saved choice ---

        private static readonly VoiceOption System1 =
            new(SpeakEngine.System, "Susan (Enhanced)", "Susan (Enhanced) (system)");
        private static readonly VoiceOption System2 =
            new(SpeakEngine.System, "Albert", "Albert (system)");
        private static readonly VoiceOption Neural1 =
            new(SpeakEngine.Neural, "af_bella", "af_bella (Kokoro)");
        private static readonly VoiceOption Custom1 =
            new(SpeakEngine.Custom, "narrator", "narrator (custom)");

        private static List<VoiceOption> Options() => new() { System1, System2, Neural1, Custom1 };

        [Fact]
        public void NoVoicesAtAllResolvesToNothing()
        {
            Assert.Null(SelectedFrom(new List<VoiceOption>()));
        }

        [Fact]
        public void TheSavedSystemVoiceIsChosen()
        {
            ClaudeBuddySettings.SpeakEngine = "system";
            ClaudeBuddySettings.SpeakVoice = "Albert";

            Assert.Equal(System2, SelectedFrom(Options()));
        }

        [Fact]
        public void TheSavedNeuralVoiceIsChosen()
        {
            ClaudeBuddySettings.SpeakEngine = "neural";
            ClaudeBuddySettings.NeuralVoice = "af_bella";

            Assert.Equal(Neural1, SelectedFrom(Options()));
        }

        [Fact]
        public void TheSavedCustomVoiceIsChosen()
        {
            ClaudeBuddySettings.SpeakEngine = "custom";
            ClaudeBuddySettings.SpeakCommandVoice = "narrator";

            Assert.Equal(Custom1, SelectedFrom(Options()));
        }

        // A voice name is matched case-insensitively, because it is a name a
        // person or a settings file may have typed rather than an identifier.
        [Fact]
        public void AVoiceNameIsMatchedWithoutRegardToCase()
        {
            ClaudeBuddySettings.SpeakEngine = "neural";
            ClaudeBuddySettings.NeuralVoice = "AF_BELLA";

            Assert.Equal(Neural1, SelectedFrom(Options()));
        }

        // First fallback: the voice is gone but the engine is still here, so
        // take that engine's first voice. This is the uninstalled-voice case.
        [Fact]
        public void AMissingVoiceFallsBackWithinItsEngine()
        {
            ClaudeBuddySettings.SpeakEngine = "system";
            ClaudeBuddySettings.SpeakVoice = "a voice that was uninstalled";

            Assert.Equal(System1, SelectedFrom(Options()));
        }

        // Second fallback: the whole engine is gone — Kokoro deleted, the custom
        // command unset — so anything at all rather than silence.
        [Fact]
        public void AMissingEngineFallsBackToWhateverIsAvailable()
        {
            ClaudeBuddySettings.SpeakEngine = "neural";
            ClaudeBuddySettings.NeuralVoice = "af_bella";

            var withoutNeural = new List<VoiceOption> { System1, System2 };

            Assert.Equal(System1, SelectedFrom(withoutNeural));
        }

        // An engine name the app does not recognise reads as the system engine,
        // which is the one that always exists. A settings file from a later
        // version must not stop the app speaking.
        [Theory]
        [InlineData("")]
        [InlineData("something-from-a-later-version")]
        public void AnUnknownEngineNameFallsBackToTheSystemEngine(string engine)
        {
            ClaudeBuddySettings.SpeakEngine = engine;
            ClaudeBuddySettings.SpeakVoice = "Albert";

            Assert.Equal(System2, SelectedFrom(Options()));
        }

        // --- SelectVoice: recording the choice ---

        // The per-engine keys are kept separate so that switching away from an
        // engine and back remembers what was chosen there. This is the test of
        // that: choosing a neural voice must not overwrite the saved system one.
        [Fact]
        public void ChoosingOneEnginesVoiceLeavesTheOthersRemembered()
        {
            SelectVoice(System2);
            SelectVoice(Neural1);

            Assert.Equal("neural", ClaudeBuddySettings.SpeakEngine);
            Assert.Equal("af_bella", ClaudeBuddySettings.NeuralVoice);
            Assert.Equal("Albert", ClaudeBuddySettings.SpeakVoice);

            // ...and back again, which is the point of keeping them apart.
            SelectVoice(System2);
            Assert.Equal("system", ClaudeBuddySettings.SpeakEngine);
            Assert.Equal(System2, SelectedFrom(Options()));
        }

        [Fact]
        public void ChoosingACustomVoiceRecordsBothTheEngineAndTheName()
        {
            SelectVoice(Custom1);

            Assert.Equal("custom", ClaudeBuddySettings.SpeakEngine);
            Assert.Equal("narrator", ClaudeBuddySettings.SpeakCommandVoice);
        }

        // A command that speaks but lists nothing still needs to be selectable,
        // and it is offered with an empty name standing for "whatever that
        // command decides". Selecting it must round-trip that empty name rather
        // than falling back to a previous one.
        [Fact]
        public void ACommandThatListsNothingIsStillSelectable()
        {
            SelectVoice(new VoiceOption(SpeakEngine.Custom, "narrator", "narrator (custom)"));
            SelectVoice(new VoiceOption(SpeakEngine.Custom, "", "Custom command"));

            Assert.Equal("custom", ClaudeBuddySettings.SpeakEngine);
            Assert.Equal("", ClaudeBuddySettings.SpeakCommandVoice);
        }

        [Fact]
        public void ACustomCommandIsConfiguredOnlyWhenItIsNotBlank()
        {
            ClaudeBuddySettings.SpeakCommand = "";
            Assert.False(CustomCommandConfigured);

            ClaudeBuddySettings.SpeakCommand = "   ";
            Assert.False(CustomCommandConfigured);

            ClaudeBuddySettings.SpeakCommand = "/usr/local/bin/speak";
            Assert.True(CustomCommandConfigured);
        }

        // --- Enter: the speak/stop state machine ---

        // No event when the state has not actually changed. Without this a
        // button watching StateChanged would be told "Idle" repeatedly during a
        // cancel and flicker between "speak" and "stop".
        [Fact]
        public void ARepeatedStateRaisesNoEvent()
        {
            var seen = new List<SpeakState>();
            void Watch(SpeakState s) => seen.Add(s);

            StateChanged += Watch;
            try
            {
                Enter(SpeakState.Idle);        // already Idle at rest
                Enter(SpeakState.Preparing);
                Enter(SpeakState.Preparing);
                Enter(SpeakState.Speaking);
                Enter(SpeakState.Idle);
            }
            finally
            {
                StateChanged -= Watch;
                Enter(SpeakState.Idle);
            }

            Assert.Equal(
                new[] { SpeakState.Preparing, SpeakState.Speaking, SpeakState.Idle }, seen);
        }

        // Preparing counts as speaking, because pressing the button again while
        // an utterance is being prepared must cancel it rather than start a
        // second one.
        [Fact]
        public void PreparingCountsAsSpeaking()
        {
            try
            {
                Enter(SpeakState.Preparing);
                Assert.True(IsSpeaking);
                Assert.Equal(SpeakState.Preparing, State);

                Enter(SpeakState.Speaking);
                Assert.True(IsSpeaking);
            }
            finally
            {
                Enter(SpeakState.Idle);
            }

            Assert.False(IsSpeaking);
        }
    }
}
