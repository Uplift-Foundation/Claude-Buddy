using Xunit;

namespace ClaudeBuddy.Tests;

// The blend grammar (CB-136), and the arithmetic behind the file it names.
//
// The first case is this repository's own persona line, verbatim, and it is
// the point of the suite rather than one row in it — CB-133 shipped a persona
// grammar every test of which was green while `.claude/PERSONA.MD` produced
// nothing, and CB-135 fixed two thirds of that file while leaving an explicit
// assertion here that the voice line still produced nothing. This is where
// that assertion is paid off.
public class VoiceBlendTests
{
    // The voices a machine with the engine switched on offers, as the resolver
    // sees them. Four rather than two on purpose: `af_bella` and `bf_isabella`
    // are the pair that makes given-name matching non-trivial — "bella" must
    // reach `af_bella` and not be a coin flip against the "bella" inside
    // "isabella" — and CB-133's own rule is what has to keep holding here.
    private static TextToSpeech.VoiceOption Neural(string name) =>
        new(TextToSpeech.SpeakEngine.Neural, name, $"{name} (Kokoro)");

    private static readonly TextToSpeech.VoiceOption Sky = Neural("af_sky");
    private static readonly TextToSpeech.VoiceOption Nicole = Neural("af_nicole");
    private static readonly TextToSpeech.VoiceOption Bella = Neural("af_bella");
    private static readonly TextToSpeech.VoiceOption Isabella = Neural("bf_isabella");

    private static readonly TextToSpeech.VoiceOption[] Installed =
        { Sky, Nicole, Bella, Isabella };

    private static readonly TextToSpeech.VoiceOption Samantha =
        new(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha (system)");

    private static (string Voice, int Percent)[] Parsed(string value)
    {
        var blend = VoiceBlend.Parse(value);
        Assert.NotNull(blend);
        return blend!.Parts.Select(part => (part.Voice, part.Percent)).ToArray();
    }

    // --- the line this ticket exists for -----------------------------------

    [Fact]
    public void TheRepositorysOwnVoiceLineIsAFiftyFiftyBlendOfSkyAndNicole()
    {
        Assert.Equal(
            new[] { ("sky", 50), ("nicole", 50) },
            Parsed("50% sky and 50% nicole"));
    }

    // --- what a plain voice still is ---------------------------------------

    // Null means "not a blend", and the caller falls through to the ordinary
    // single-voice match. Everything that worked before this ticket has to
    // land here, or CB-133's whole voice path has quietly moved.
    [Theory]
    [InlineData("af_bella")]
    [InlineData("Bella")]
    [InlineData("Samantha")]
    [InlineData("Ava (Premium)")]
    [InlineData("Microsoft David Desktop")]
    [InlineData("O'Brien")]
    public void AnOrdinaryVoiceNameIsNotABlendAtAll(string value) =>
        Assert.Null(VoiceBlend.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotABlendEither(string? value) =>
        Assert.Null(VoiceBlend.Parse(value));

    // --- separators ---------------------------------------------------------

    [Theory]
    [InlineData("50% sky and 50% nicole")]
    [InlineData("50% sky, 50% nicole")]
    [InlineData("50% sky + 50% nicole")]
    [InlineData("50% sky plus 50% nicole")]
    [InlineData("50% sky AND 50% nicole")]
    public void EverySeparatorSpellsTheSameBlend(string value) =>
        Assert.Equal(new[] { ("sky", 50), ("nicole", 50) }, Parsed(value));

    // Mixed separators in one value, which is what somebody listing three
    // things actually writes.
    [Fact]
    public void ACommaAndAnAndInTheSameLineAreBothSeparators() =>
        Assert.Equal(
            new[] { ("sky", 34), ("nicole", 33), ("bella", 33) },
            Parsed("sky, nicole and bella"));

    // --- where the weight sits ----------------------------------------------

    [Fact]
    public void AWeightMayFollowItsVoiceInsteadOfLeadingIt() =>
        Assert.Equal(new[] { ("sky", 60), ("nicole", 40) }, Parsed("sky 60% and nicole 40%"));

    [Fact]
    public void AWeightWrittenAtBothEndsOfOnePartIsTwoClaimsAndIsRefused() =>
        Assert.Null(VoiceBlend.Parse("50% sky 50% and 50% nicole"));

    [Fact]
    public void APercentageNeedNotBeSpacedAwayFromItsVoice() =>
        Assert.Equal(new[] { ("sky", 50), ("nicole", 50) }, Parsed("50%sky and 50%nicole"));

    // --- weights, or the absence of them ------------------------------------

    // The percent-free spelling, which already parsed before this ticket — as
    // the literal string "sky and nicole" — reached MatchVoiceOption, matched
    // nothing, and fell silently back to the global voice. It is a blend now.
    [Fact]
    public void TwoVoicesWithNoWeightsAtAllShareEqually() =>
        Assert.Equal(new[] { ("sky", 50), ("nicole", 50) }, Parsed("sky and nicole"));

    [Fact]
    public void ThreeEqualPartsAreThirtyFourAndTwoThirtyThrees() =>
        Assert.Equal(
            new[] { ("sky", 34), ("nicole", 33), ("bella", 33) },
            Parsed("sky and nicole and bella"));

    [Fact]
    public void FourEqualPartsAreFourTwentyFives() =>
        Assert.Equal(
            new[] { ("sky", 25), ("nicole", 25), ("bella", 25), ("isabella", 25) },
            Parsed("sky, nicole, bella, isabella"));

    [Fact]
    public void APartWithAWeightBesideOneWithoutIsNotAMixtureAnybodyStated() =>
        Assert.Null(VoiceBlend.Parse("60% sky and nicole"));

    // 99 and 101 are what thirds and sevenths round to, so they are normalised
    // without comment...
    [Fact]
    public void NinetyNineIsNormalisedToAHundred() =>
        Assert.Equal(new[] { ("sky", 34), ("nicole", 33), ("bella", 33) },
            Parsed("33% sky and 33% nicole and 33% bella"));

    [Fact]
    public void AHundredAndOneIsNormalisedToAHundred() =>
        Assert.Equal(new[] { ("sky", 50), ("nicole", 50) }, Parsed("51% sky and 50% nicole"));

    // ...and 120 is not a rounding error. Somebody who wrote 60 and 60 meant
    // something, and it is not "thirty each plus forty of nothing".
    [Fact]
    public void SixtyAndSixtyIsNotARoundingErrorAndIsRefused() =>
        Assert.Null(VoiceBlend.Parse("60% sky and 60% nicole"));

    [Theory]
    [InlineData("40% sky and 40% nicole")]      // 80
    [InlineData("97% sky and 1% nicole")]       // 98, one under the floor
    [InlineData("2% sky and 100% nicole")]      // 102, over the ceiling
    [InlineData("0% sky and 0% nicole")]
    public void AStatedTotalOutsideAHundredGiveOrTakeOneIsRefused(string value) =>
        Assert.Null(VoiceBlend.Parse(value));

    // --- how many parts ------------------------------------------------------

    // A share written on one voice is somebody starting to write a blend and
    // stopping. Half of one voice is that voice — and the reason this line
    // matters at all is that `Voice is 50% sky` was refused before this ticket
    // too, at two words, which is what proved the word cap was never the first
    // barrier.
    [Fact]
    public void OnePartIsThatVoiceWhateverShareIsWrittenOnIt()
    {
        Assert.Equal(new[] { ("sky", 100) }, Parsed("50% sky"));
        Assert.Equal(new[] { ("sky", 100) }, Parsed("100% sky"));
    }

    [Fact]
    public void FiveePartsIsSomebodyGeneratingTextRatherThanChoosingAVoice() =>
        Assert.Null(VoiceBlend.Parse("sky, nicole, bella, isabella, heart"));

    // --- what is not a voice --------------------------------------------------

    // The bound that lets PersonaMarkdown widen its whitelist at all: a part
    // is one token, so a phrase is not a voice however plausibly it is
    // punctuated.
    [Theory]
    [InlineData("a matter of taste, plus tone")]
    [InlineData("the voice of reason and a bit of warmth")]
    [InlineData("50% of the time, and the rest")]
    public void APhraseIsNotAPartHoweverItIsSeparated(string value) =>
        Assert.Null(VoiceBlend.Parse(value));

    [Theory]
    [InlineData("voices/af_sky.npy and voices/af_nicole.npy")]
    [InlineData("https://example.invalid/x and y")]
    [InlineData("sky and nicole!")]
    public void AValueCarryingMarksAVoiceIdentifierNeverHasIsRefused(string value) =>
        Assert.Null(VoiceBlend.Parse(value));

    // A separator with nothing on one side of it is a trailing word, not a
    // part of nothing.
    [Fact]
    public void ATrailingSeparatorIsDroppedRatherThanCountedAsAPart() =>
        Assert.Equal(new[] { ("sky", 50), ("nicole", 50) }, Parsed("sky and nicole and"));

    [Theory]
    [InlineData("and, and %")]      // one piece, and it is not a voice
    [InlineData("and plus and")]    // no pieces at all
    public void SeparatorsAndNothingElseIsNotABlend(string value) =>
        Assert.Null(VoiceBlend.Parse(value));

    // --- Shares, on its own ----------------------------------------------------

    // Largest remainder, exercised where the rounding actually bites. The
    // percentages are the weights rather than a display of them, so a set that
    // did not total exactly 100 would be a blend whose file and whose name
    // disagreed.
    [Theory]
    [InlineData(new[] { 1, 1 }, new[] { 50, 50 })]
    [InlineData(new[] { 1, 1, 1 }, new[] { 34, 33, 33 })]
    [InlineData(new[] { 1, 1, 1, 1 }, new[] { 25, 25, 25, 25 })]
    [InlineData(new[] { 33, 33, 33 }, new[] { 34, 33, 33 })]
    [InlineData(new[] { 50, 51 }, new[] { 50, 50 })]
    [InlineData(new[] { 1, 2 }, new[] { 33, 67 })]
    public void SharesAlwaysTotalExactlyOneHundred(int[] raw, int[] expected)
    {
        var shares = VoiceBlend.Shares(raw);

        Assert.Equal(expected, shares);
        Assert.Equal(100, shares.Sum());
    }

    // --- resolving the parts ----------------------------------------------------

    // CB-133's given-name rule, exercised once per part rather than once per
    // value. `sky` and `nicole` are unique given names among the four
    // installed here, and `bella` is the case that would be a coin flip under
    // a substring match.
    [Fact]
    public void EachPartResolvesByGivenNameAgainstWhatIsInstalled()
    {
        var resolved = VoiceBlend.Resolve(VoiceBlend.Parse("50% sky and 50% nicole")!, Installed);

        Assert.NotNull(resolved);
        Assert.Equal(new[] { "af_sky", "af_nicole" }, resolved!.Parts.Select(p => p.Option.Name));
        Assert.Equal(new[] { 50, 50 }, resolved.Parts.Select(p => p.Percent));
        Assert.Equal(new[] { 0.5, 0.5 }, resolved.Parts.Select(p => p.Weight));
    }

    [Fact]
    public void BellaStillReachesAfBellaRatherThanBfIsabella()
    {
        var resolved = VoiceBlend.Resolve(VoiceBlend.Parse("bella and isabella")!, Installed);

        Assert.NotNull(resolved);
        Assert.Equal(new[] { "af_bella", "bf_isabella" }, resolved!.Parts.Select(p => p.Option.Name));
    }

    // One part nobody has rejects the whole blend. Speaking a two-voice
    // mixture as one of its halves would be a voice nobody chose, and it would
    // be indistinguishable from the blend having worked.
    [Fact]
    public void AnUnknownVoiceInAnyPartRejectsTheWholeBlend()
    {
        Assert.Null(VoiceBlend.Resolve(VoiceBlend.Parse("50% sky and 50% nobody")!, Installed));
        Assert.Null(VoiceBlend.Resolve(VoiceBlend.Parse("50% nobody and 50% sky")!, Installed));
    }

    // The engine being switched off arrives here as a list with no neural
    // options in it, which is exactly what AllVoiceOptions produces then. So
    // the gate the ticket asks for is not a second check anywhere — it is this
    // filter.
    [Fact]
    public void WithNoNeuralVoicesOfferedABlendResolvesToNothing() =>
        Assert.Null(VoiceBlend.Resolve(VoiceBlend.Parse("sky and nicole")!, new[] { Samantha }));

    // A system voice that happens to share a name with a part is not a part.
    [Fact]
    public void ASystemVoiceIsNeverAPartOfABlend() =>
        Assert.Null(VoiceBlend.Resolve(
            VoiceBlend.Parse("samantha and sky")!, new[] { Samantha, Sky }));

    [Fact]
    public void ASinglePartResolvesToThatVoiceAndNothingIsNamedAfterIt()
    {
        var resolved = VoiceBlend.Resolve(VoiceBlend.Parse("50% sky")!, Installed);

        Assert.NotNull(resolved);
        Assert.True(resolved!.IsSingleVoice);
        Assert.Equal(Sky, resolved.Parts[0].Option);
        Assert.Equal("af_sky", resolved.Name);
    }

    // --- the name the file wears -------------------------------------------------

    // Measured against the real engine on this machine before it was chosen:
    // `--list-voices` dropped a `zz_`-prefixed file and listed both an
    // `af_blend_…` one and a prefix-less one. The prefix is therefore not
    // decoration — a blend filed under the wrong language is invisible.
    [Fact]
    public void ABlendWearsItsFirstPartsPrefixAndSpellsTheRestShort() =>
        Assert.Equal(
            "af_blend_sky50-nicole50",
            VoiceBlend.Slug(VoiceBlend.Resolve(VoiceBlend.Parse("50% sky and 50% nicole")!, Installed)!.Parts));

    // A part from another language keeps its own prefix, so the two can never
    // collapse onto one name.
    [Fact]
    public void APartFromAnotherPrefixKeepsItsOwn() =>
        Assert.Equal(
            "af_blend_sky50-bf_isabella50",
            VoiceBlend.Slug(VoiceBlend.Resolve(VoiceBlend.Parse("sky and isabella")!, Installed)!.Parts));

    [Fact]
    public void AVoiceWithNoPrefixAtAllLeadsAPrefixlessBlend()
    {
        var custom = Neural("annabel_mix");

        Assert.Equal(
            "blend_annabel_mix50-af_sky50",
            VoiceBlend.Slug(VoiceBlend.Resolve(
                VoiceBlend.Parse("annabel_mix and sky")!, new[] { custom, Sky })!.Parts));
    }

    // The whole point of the slug: the same mixture written two ways is one
    // file, and two different mixtures are never one file.
    [Fact]
    public void TheSameMixtureWrittenTwoWaysNamesTheSameFile()
    {
        string Name(string written) =>
            VoiceBlend.Resolve(VoiceBlend.Parse(written)!, Installed)!.Name;

        Assert.Equal(Name("50% sky and 50% nicole"), Name("sky 50%, nicole 50%"));
        Assert.Equal(Name("50% sky and 50% nicole"), Name("sky and nicole"));
        Assert.NotEqual(Name("50% sky and 50% nicole"), Name("60% sky and 40% nicole"));
        Assert.NotEqual(Name("50% sky and 50% nicole"), Name("50% nicole and 50% sky"));
    }

    // --- the whole rule, as the two speak paths call it ---------------------------

    // VoiceForPersona is the single entry point both LocalPersonas and
    // OpenClawSessions now use, and its first job is to leave every
    // non-blend alone.
    [Fact]
    public void AnOrdinaryVoiceStillResolvesExactlyAsItDid()
    {
        Assert.Equal(Bella, TextToSpeech.VoiceForPersona("Bella", Installed));
        Assert.Equal(Samantha, TextToSpeech.VoiceForPersona("Samantha", new[] { Samantha, Bella }));
        Assert.Null(TextToSpeech.VoiceForPersona("Not Installed Here", Installed));
        Assert.Null(TextToSpeech.VoiceForPersona(null, Installed));
    }

    [Fact]
    public void ASinglePartBlendSpeaksAsThatVoiceWithoutBuildingAnything() =>
        Assert.Equal(Sky, TextToSpeech.VoiceForPersona("50% sky", Installed));

    // A blend nobody can resolve is the user's global voice, which is what
    // null means to both callers. No file is written and no engine is asked
    // anything — this is the "malformed → global voice, never silence" rule
    // seen from the entry point.
    [Theory]
    [InlineData("60% sky and 60% nicole")]
    [InlineData("50% sky and 50% nobody")]
    [InlineData("a matter of taste, plus tone")]
    public void AMalformedOrUnresolvableBlendIsTheUsersOwnVoice(string written) =>
        Assert.Null(TextToSpeech.VoiceForPersona(written, Installed));

    // --- the locale prefix, which two files now depend on --------------------------

    [Theory]
    [InlineData("af_sky", "af_")]
    [InlineData("bm_fable", "bm_")]
    [InlineData("annabel_mix", "")]
    [InlineData("Microsoft David Desktop", "")]
    [InlineData("ab_", "")]                 // nothing after the prefix is not a name
    [InlineData("AF_sky", "")]              // lowercase, which is Kokoro's whole convention
    public void ALocalePrefixIsTwoLowercaseLettersAndAnUnderscoreOrNothing(string name, string expected) =>
        Assert.Equal(expected, TextToSpeech.LocalePrefix(name));
}
