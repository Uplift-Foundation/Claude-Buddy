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

    // A non-empty list that still has no winner in it — distinct from the
    // empty-list case above, which returns before Winner() is ever called.
    // SessionManager never actually builds a list like this (it only adds
    // an event when the signal isn't None), but Decide is a pure function
    // reasoned about on its own terms, and this is the input shape that
    // proves Winner() returning null is handled rather than merely never
    // exercised.
    [Fact]
    public void ASignalListOfOnlyNoneNeverFindsAWinner()
    {
        var events = new[] { new TurnSoundEvent(TurnSignal.None, "key-a", "session-a") };

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, LongAgo, speechBusy: false);

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

    // QA (CB-167): coalescing used to pick the highest-ranked *signal*
    // before asking whether it resolved to anything — so orb A's attention
    // could be muted for itself and still "win" the scan, evaporate against
    // its own "off" override, and take orb B's perfectly audible Finished
    // chime down with it. A muted signal must never be a candidate to win
    // at all.
    [Fact]
    public void AnOrbMutedForAttentionDoesNotSilenceAnotherOrbsFinishedChimeInTheSameScan()
    {
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new(Finished: null, Attention: "off"),
        };
        var events = new[]
        {
            new TurnSoundEvent(TurnSignal.NeedsAttention, "key-a", "session-a"),
            new TurnSoundEvent(TurnSignal.Finished, "key-b", "session-b"),
        };

        var result = TurnSoundPolicy.Decide(events, Snapshot(overrides: overrides), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
    }

    // Same defect, finished-only: the first finished orb is muted, the
    // second is not. One chime is still owed — coalescing within one rank
    // has to skip a muted candidate the same way it does across ranks.
    [Fact]
    public void AMutedFirstFinishedOrbDoesNotSilenceASecondAudibleOne()
    {
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new(Finished: "off", Attention: null),
        };
        var events = new[]
        {
            new TurnSoundEvent(TurnSignal.Finished, "key-a", "session-a"),
            new TurnSoundEvent(TurnSignal.Finished, "key-b", "session-b"),
        };

        var result = TurnSoundPolicy.Decide(events, Snapshot(overrides: overrides), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
    }

    // --- the rate limit ---

    // QA (CB-167): a signal inside the gap used to return plain Silent, and
    // because TurnSignalTracker never re-raises an unchanged state, nothing
    // would ever ask again — an orb that started waiting 1.5s after another
    // orb's chime simply never got its Ping, for as long as it sat there.
    // The fix defers rather than drops: still Chime (or Summary), with
    // PlayAt set to when the gap actually opens, so a caller can tell "play
    // this now" from "hold this until then" without a new Kind to check.
    [Fact]
    public void WithinTheGapTheSignalIsDeferredRatherThanDropped()
    {
        var events = new[] { Finished() };
        var lastPlayed = Now - TimeSpan.FromSeconds(1);

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, lastPlayed, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
        Assert.True(result.IsDeferred);
        Assert.Equal(lastPlayed + TurnSoundPolicy.MinimumGap, result.PlayAt);
    }

    // The negative control for the case above: a deferred action still
    // carries the same path an immediate one would, so a caller that only
    // reads Kind and Path (and ignores PlayAt) gets the right sound either
    // way.
    [Fact]
    public void ADeferredChimeStillNamesTheSamePathAnImmediateOneWould()
    {
        var events = new[] { Finished() };
        var settings = Snapshot(resolveFinished: _ => "/sounds/glass.aiff");

        var immediate = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);
        var deferred = TurnSoundPolicy.Decide(
            events, settings, Now, Now - TimeSpan.FromSeconds(1), speechBusy: false);

        Assert.False(immediate.IsDeferred);
        Assert.True(deferred.IsDeferred);
        Assert.Equal(immediate.Path, deferred.Path);
    }

    // A summary decided inside the gap defers the same way a chime does —
    // and carries the session id a deferred summary needs, not a path.
    [Fact]
    public void ASummaryInsideTheGapDefersWithTheSessionIdIntact()
    {
        var events = new[] { Finished("key-a", "session-a") };
        var settings = Snapshot(defaultFinished: "summary");
        var lastPlayed = Now - TimeSpan.FromSeconds(1);

        var result = TurnSoundPolicy.Decide(events, settings, Now, lastPlayed, speechBusy: false);

        Assert.Equal(SoundActionKind.Summary, result.Kind);
        Assert.True(result.IsDeferred);
        Assert.Equal("session-a", result.SessionId);
    }

    [Fact]
    public void ExactlyTheGapIsNoLongerDeferred()
    {
        var events = new[] { Finished() };
        var lastPlayed = Now - TurnSoundPolicy.MinimumGap;

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, lastPlayed, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
        Assert.False(result.IsDeferred);
    }

    [Fact]
    public void WellPastTheGapChimesImmediately()
    {
        var events = new[] { Finished() };

        var result = TurnSoundPolicy.Decide(events, Snapshot(), Now, LongAgo, speechBusy: false);

        Assert.Equal(SoundActionKind.Chime, result.Kind);
        Assert.False(result.IsDeferred);
    }

    // QA's own demonstrating case: the tracker and policy driven exactly as
    // SessionManager drives them, scan by scan, over real wall-clock gaps.
    // Orb A finishes (Glass) 2s after the baseline scan. 1.5s later, orb B —
    // a genuinely new transition, not a repeat — hits a permission prompt,
    // landing inside the gap. Session b then sits at "waiting" for the rest
    // of the run, so no later scan ever re-raises the signal (waiting→
    // waiting is None) — the only chance for B's Ping is the one Decide
    // call 3.5s in, and it must not come back Silent.
    [Fact]
    public void ANeedsAttentionThatLandsInsideTheGapIsEventuallyPlayed()
    {
        var tracker = new TurnSignalTracker();
        var settings = Snapshot();
        var lastPlayed = LongAgo;
        var played = new List<(DateTime At, TurnSignal Winner)>();

        void ScanAt(DateTime now, params (string Id, string State)[] sessions)
        {
            var events = new List<TurnSoundEvent>();
            foreach (var (id, state) in sessions)
            {
                var s = tracker.Observe(id, state);
                if (s != TurnSignal.None) events.Add(new TurnSoundEvent(s, "key-" + id, id));
            }
            tracker.Prune(sessions.Select(x => x.Id).ToHashSet());
            var d = TurnSoundPolicy.Decide(events, settings, now, lastPlayed, speechBusy: false);
            if (d.Kind != SoundActionKind.Silent)
            {
                lastPlayed = now;
                played.Add((now, events.Any(e => e.Signal == TurnSignal.NeedsAttention)
                    ? TurnSignal.NeedsAttention : TurnSignal.Finished));
            }
        }

        ScanAt(Now,                  ("a", "generating"), ("b", "generating")); // baseline
        ScanAt(Now.AddSeconds(2.0),  ("a", "idle"),       ("b", "generating")); // Glass
        ScanAt(Now.AddSeconds(3.5),  ("a", "idle"),       ("b", "waiting"));    // inside the gap — deferred, not dropped
        ScanAt(Now.AddSeconds(5.5),  ("a", "idle"),       ("b", "waiting"));
        ScanAt(Now.AddSeconds(60),   ("a", "idle"),       ("b", "waiting"));    // a minute later, still waiting

        Assert.Contains(played, p => p.Winner == TurnSignal.NeedsAttention);
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

    // The attention-side mirror of AnOverrideBeatsTheGlobalDefault — the
    // ternary that picks between the two fields has to be proven on both
    // arms, not just the Finished one every other case here happens to use.
    [Fact]
    public void AnOverrideBeatsTheGlobalDefaultForAttentionToo()
    {
        var events = new[] { Attention("key-a", "session-a") };
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new ClaudeBuddySettings.OrbTurnSound(Finished: null, Attention: "custom-ping"),
        };

        string? seenSetting = null;
        var settings = Snapshot(
            defaultAttention: "global-default",
            overrides: overrides,
            resolveAttention: setting =>
            {
                seenSetting = setting;
                return "/sounds/" + setting + ".aiff";
            });

        var result = TurnSoundPolicy.Decide(events, settings, Now, LongAgo, speechBusy: false);

        Assert.Equal("custom-ping", seenSetting);
        Assert.Equal("/sounds/custom-ping.aiff", result.Path);
    }

    // And the same pairing the Finished side gets: an override that only
    // names Finished leaves Attention on the global default.
    [Fact]
    public void AnOverrideOfOnlyFinishedLeavesAttentionOnTheGlobalDefault()
    {
        var events = new[] { Attention("key-a", "session-a") };
        var overrides = new Dictionary<string, ClaudeBuddySettings.OrbTurnSound>
        {
            ["key-a"] = new ClaudeBuddySettings.OrbTurnSound(Finished: "custom-glass", Attention: null),
        };

        string? seenSetting = null;
        var settings = Snapshot(
            defaultAttention: "global-default",
            overrides: overrides,
            resolveAttention: setting => { seenSetting = setting; return "/x.aiff"; });

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
