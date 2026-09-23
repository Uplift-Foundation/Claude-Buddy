using Xunit;

namespace ClaudeBuddy.Tests;

// What actually happens for one scan's worth of signals, with no process, no
// settings file and no clock but the one each case hands in. TurnSounds
// carries out whatever comes back; this is where every rule the plan names —
// coalescing, the rate limit, an override beating the default, the
// busy-speech fallback — is a branch a test can point at directly.
public class TurnSoundPolicyTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = DateTime.MinValue;

    private static SoundSettingsSnapshot Snapshot(
        bool masterEnabled = true,
        string? defaultFinished = null,
        string? defaultAttention = null,
        Dictionary<string, ClaudeBuddySettings.OrbTurnSound>? overrides = null,
        Func<string?, string?>? resolveFinished = null,
        Func<string?, string?>? resolveAttention = null)
    {
        var over = overrides ?? new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>();

        // Fake resolvers that behave like SystemSoundCatalog.Resolve would
        // for the purposes of these tests: null substitutes a platform
        // default path, "off" is never actually handed to a resolver (Decide
        // short-circuits on it first) and anything else becomes a fake path
        // so a test can tell which name won.
        Func<string?, string?> defaultResolve = setting =>
            setting is null ? "/default/sound.aiff" : "/sounds/" + setting + ".aiff";

        return new SoundSettingsSnapshot(
            masterEnabled,
            defaultFinished,
            defaultAttention,
            key => over.TryGetValue(key, out var value) ? value : null,
            resolveFinished ?? defaultResolve,
            resolveAttention ?? defaultResolve);
    }

    private static TurnSoundEvent Finished(string key = "key-a", string sessionId = "session-a") =>
        new(TurnSignal.Finished, key, sessionId);

    private static TurnSoundEvent Attention(string key = "key-a", string sessionId = "session-a") =>
        new(TurnSignal.NeedsAttention, key, sessionId);

    // --- the trivial cases ---

    [Fact]
    public void NoSignalsIsSilent()
    {
        var result = TurnSoundPolicy.Decide(
            Array.Empty<TurnSoundEvent>(), Snapshot(), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    [Fact]
    public void MasterOffSilencesEverythingRegardlessOfSignals()
    {
        var events = new[] { Attention() };
        var result = TurnSoundPolicy.Decide(
            events, Snapshot(masterEnabled: false), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    // --- coalescing ---

    // Four orbs finishing in one scan produce one action, not a queue of
    // four — automatic given Decide's return type, but worth pinning: this
    // is the whole reason SessionManager collects every signal before
    // calling Deliver once, rather than once per session.
    [Fact]
    public void MultipleFinishedSignalsInOneScanCoalesceIntoOneChime()
    {
        var events = new[]
        {
            Finished("key-a", "session-a"),
            Finished("key-b", "session-b"),
            Finished("key-c", "session-c"),
            Finished("key-d", "session-d"),
        };

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
    }

    // Attention outranks finished regardless of where in the list it sits —
    // this asserts it winning from second place specifically, so the rule
    // can't be satisfied by "whichever came first" instead of an actual
    // priority.
    [Fact]
    public void NeedsAttentionOutranksFinishedEvenWhenFinishedWasObservedFirst()
    {
        var events = new[] { Finished("key-a", "session-a"), Attention("key-b", "session-b") };

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
    }

    // Same case, asserted precisely: the attention branch's own resolver ran,
    // not the finished branch's — the negative control for the test above.
    [Fact]
    public void TheWinningSignalDeterminesWhichResolverRuns()
    {
        var events = new[] { Finished("key-a", "session-a"), Attention("key-b", "session-b") };
        var settings = Snapshot(
            resolveFinished: _ => "/sounds/finished.aiff",
            resolveAttention: _ => "/sounds/attention.aiff");

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal("/sounds/attention.aiff", result.Path);
    }

    // --- the rate limit ---

    [Fact]
    public void WithinTheGapSinceTheLastSoundIsSilentEvenThoughItWouldOtherwiseChime()
    {
        var events = new[] { Finished() };
        var lastPlayed = Now - TimeSpan.FromSeconds(1);

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, lastPlayed, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    [Fact]
    public void ExactlyTheGapIsNoLongerSilent()
    {
        var events = new[] { Finished() };
        var lastPlayed = Now - TurnSoundPolicy.MinimumGap;

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, lastPlayed, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
    }

    [Fact]
    public void WellPastTheGapChimes()
    {
        var events = new[] { Finished() };

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
    }

    // --- override vs default ---

    [Fact]
    public void AnOverrideBeatsTheGlobalDefault()
    {
        var events = new[] { Finished("key-a", "session-a") };
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new ClaudeBuddySettings.OrbTurnSound(Finished: "custom", Attention: null),
        };

        string? seenSetting = null;
        var settings = Snapshot(
            defaultFinished: "global-default",
            overrides: overrides,
            resolveFinished: setting =>
            {
                seenSetting = setting;
                return "/sounds/" + setting + ".aiff";
            });

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal("custom", seenSetting);
        Assert.Equal("/sounds/custom.aiff", result.Path);
    }

    // An override only naming the *other* trigger falls through to the
    // global default for this one — the two fields are independent, not a
    // package deal.
    [Fact]
    public void AnOverrideOfOnlyAttentionLeavesFinishedOnTheGlobalDefault()
    {
        var events = new[] { Finished("key-a", "session-a") };
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new ClaudeBuddySettings.OrbTurnSound(Finished: null, Attention: "custom-ping"),
        };

        string? seenSetting = null;
        var settings = Snapshot(
            defaultFinished: "global-default",
            overrides: overrides,
            resolveFinished: setting => { seenSetting = setting; return "/x.aiff"; });

        TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal("global-default", seenSetting);
    }

    [Fact]
    public void AnOffOverrideSilencesAnOtherwiseLoudDefault()
    {
        var events = new[] { Finished("key-a", "session-a") };
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new ClaudeBuddySettings.OrbTurnSound(Finished: "off", Attention: null),
        };

        // The resolver would happily hand back a real path if it were ever
        // asked — proving the silence comes from the "off" check, not from
        // the resolver failing to find anything.
        var settings = Snapshot(
            defaultFinished: "glass",
            overrides: overrides,
            resolveFinished: _ => "/sounds/glass.aiff");

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("Off")]
    public void OffIsRecognisedRegardlessOfCase(string setting)
    {
        var events = new[] { Finished("key-a", "session-a") };
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new ClaudeBuddySettings.OrbTurnSound(Finished: setting, Attention: null),
        };
        var settings = Snapshot(overrides: overrides, resolveFinished: _ => "/would-play.aiff");

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    // --- a missing file ---

    [Fact]
    public void AResolverThatFindsNothingGivesSilentRatherThanThrowing()
    {
        var events = new[] { Finished() };
        var settings = Snapshot(resolveFinished: _ => null);

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    // --- vibe summary ---

    [Fact]
    public void ATurnFinishedSetToSummaryProducesASummaryActionWhenSpeechIsIdle()
    {
        var events = new[] { Finished("key-a", "session-a") };
        var settings = Snapshot(defaultFinished: "summary");

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Summary, result.Kind);
        Assert.Equal("session-a", result.SessionId);
    }

    // A summary never interrupts speech already in progress — it falls back
    // to the ordinary default finished chime instead of to silence, so a
    // turn ending while the user happens to be listening to something else
    // still gets *some* feedback.
    [Fact]
    public void ATurnFinishedSetToSummaryFallsBackToTheDefaultChimeWhenSpeechIsBusy()
    {
        var events = new[] { Finished("key-a", "session-a") };
        var settings = Snapshot(
            defaultFinished: "summary",
            resolveFinished: setting => setting is null ? "/default/glass.aiff" : null);

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: true);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
        Assert.Equal("/default/glass.aiff", result.Path);
    }

    // "summary" is a turn-finished value only — needsAttentionSound has no
    // such setting in the settings schema, so a hand-edited file that sets
    // one anyway is not given special meaning. It simply fails to resolve,
    // same as any other unrecognised name would.
    [Fact]
    public void SummaryIsNeverSpecialForANeedsAttentionSignal()
    {
        var events = new[] { Attention("key-a", "session-a") };
        var settings = Snapshot(defaultAttention: "summary", resolveAttention: _ => null);

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Silent, result.Kind);
    }

    // --- the platform-default hand-off ---

    // A null setting (nothing overridden, nothing globally configured) is
    // handed to the resolver exactly as null — substituting the platform
    // default name is the resolver's job (TurnSounds.Snapshot in
    // production), not something this decision does itself.
    [Fact]
    public void ANullSettingReachesTheResolverAsNull()
    {
        var events = new[] { Finished() };
        string? seen = "not yet called";
        var settings = Snapshot(defaultFinished: null, resolveFinished: setting =>
        {
            seen = setting;
            return "/default/glass.aiff";
        });

        TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Null(seen);
    }
}
