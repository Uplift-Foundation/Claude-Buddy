using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The handful of behaviours that only run once an orb is actually Loaded:
// UpdateFrom's own "apply the new state immediately" branch, and
// ReapplyStateColors (the settings colour picker's live preview). Both are
// gated on IsLoaded precisely because Avalonia fires Loaded *after* the
// first UpdateFrom (see UpdateFrom's own comment) — a window that is shown
// gets there for real, the way OrbFlyoutTests and AvatarPopupTests already
// show their own windows.
//
// Never closed, per every sibling file in this suite: closing a headless
// window corrupts a process-wide font cache shared with every other one.
[Collection("Settings")]
public class OrbWindowShownTests
{
    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static SessionStatus Status(string state, string title = "claude-buddy") => new()
    {
        Source = SessionSource.ClaudeCode,
        State = state,
        Title = title,
        Cwd = "/Users/warren/project",
    };

    [AvaloniaFact]
    public void ShowingAnOrbMakesItLoaded()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        Assert.True(orb.IsLoaded);
    }

    // UpdateFrom stores a state change and applies it immediately when the
    // orb is already loaded, rather than waiting for Loaded to do it — the
    // other half of the branch OrbWindowStateTests drives via ApplyState
    // directly for an orb that is never shown.
    //
    // Glyph.Fill's own colour is the wrong thing to assert on here: it is
    // wrapped in a 300ms ColorTransition, so reading it back immediately
    // after the set (with no wall-clock time for the animation to advance)
    // still reads the *old* colour — this caught the test out the first time
    // it was written. Glow.IsVisible is GlowsFor(state) applied synchronously
    // with no transition at all (AnimateColor's own comment: "Hidden rather
    // than made transparent"), so it is what actually proves ApplyState ran
    // rather than merely that _lastState was recorded.
    [AvaloniaFact]
    public void AStateChangeOnAShownOrbIsAppliedImmediately()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(Status("idle"));
        Assert.False(orb.Glow.IsVisible);

        orb.UpdateFrom(Status("waiting"));
        Assert.True(orb.Glow.IsVisible);
    }

    // ReapplyStateColors is the settings colour picker's live preview: a
    // colour changing is not itself a state change, so UpdateFrom's own
    // dedup wouldn't reapply anything on its own. Its fade is only 60ms
    // (SettingsColorFade, deliberately quicker than a real state change's —
    // see that field's own comment), so a short real-time poll is enough to
    // observe the brush actually move, the same pattern AvatarPopupTests uses
    // for its own DispatcherTimer-driven animation.
    [AvaloniaFact]
    public async System.Threading.Tasks.Task ReapplyStateColorsRepaintsAShownOrbWithTheCurrentStatesColour()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();
        orb.UpdateFrom(Status("waiting"));
        Flush();

        var before = ((Avalonia.Media.SolidColorBrush)orb.Orb.Fill!).Color;

        var previous = ClaudeBuddySettings.WaitingColor;
        try
        {
            ClaudeBuddySettings.WaitingColor = "#AA00FF";
            orb.ReapplyStateColors();

            // Wall-clock bounded rather than iteration-bounded — see
            // TheSharedTickerAnimatesAShownPulsingOrbsBreath's own comment on
            // why, below.
            Avalonia.Media.Color after = before;
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline)
            {
                Flush();
                after = ((Avalonia.Media.SolidColorBrush)orb.Orb.Fill!).Color;
                if (after != before) break;
                await System.Threading.Tasks.Task.Delay(5);
            }

            Assert.NotEqual(before, after);
        }
        finally
        {
            ClaudeBuddySettings.WaitingColor = previous;
        }
    }

    // Never shown: ReapplyStateColors is a no-op, because Loaded will apply
    // _lastState with the (by then, current) colours anyway once it fires.
    [AvaloniaFact]
    public void ReapplyStateColorsOnAnUnshownOrbIsANoOp()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        // No exception, and nothing to observe changing — this is the guard
        // clause itself (`if (!IsLoaded) return;`).
        orb.ReapplyStateColors();
    }

    // --- the pulse and heartbeat ticks --------------------------------------
    //
    // OrbWindowStateTests already pins the state-to-amplitude *mapping*
    // (_pulseTo) by calling ApplyState directly on an orb that is never
    // shown. What that leaves uncovered is TickPulse and TickHeart
    // themselves, which in production only ever run from the shared static
    // ticker's own Tick handler.
    //
    // Driven directly here rather than by waiting on that real
    // DispatcherTimer to fire, for the same reason OrbWindowStateTests drives
    // ApplyState directly instead of Loaded: this suite's own Pulsing list is
    // a process-wide static that every UiTests class which ever shows an orb
    // and calls ApplyState adds to and never removes (Closed is what would
    // remove one, and no headless window here is ever closed), so by the
    // time the full suite has run for a while it can be carrying hundreds of
    // orbs from unrelated test classes. A first version of this test waited
    // on the real shared ticker and passed in isolation but failed even with
    // a 10-second wall-clock budget once the rest of the suite's tests were
    // running around it — not a timing margin problem, since a single 50ms
    // tick is enough to move the phase away from zero, but a sign that this
    // suite's real-wall-clock DispatcherTimer techniques (borrowed from
    // AvatarPopupTests and OrbFlyoutTests) do not scale to a ticker whose
    // roster grows for the entire life of the process. TickPulse's own
    // IsVisible guard still needs a genuinely shown orb, so that much of the
    // real setup is kept.
    [AvaloniaFact]
    public void TickPulseAnimatesAShownOrbsBreathAndItsHeartbeatBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.Show();
        Flush();

        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "waiting",
            Title = "claude-buddy",
            Cwd = "/Users/warren/project",
            Heartbeat = true,
        });

        var orbScale = (Avalonia.Media.ScaleTransform)typeof(OrbWindow)
            .GetField("_orbScale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orb)!;
        var heartGlyph = orb.FindControl<Avalonia.Controls.TextBlock>("HeartGlyph")!;

        // A tiny real sleep, not a poll loop: TickPulse computes its phase
        // from Environment.TickCount64 - _pulseStartedAt, so calling it with
        // zero elapsed time would compute phase 0 and leave the scale
        // unmoved regardless of whether the method ran.
        System.Threading.Thread.Sleep(20);
        orb.TickPulse();

        Assert.NotEqual(1.0, orbScale.ScaleX);
        Assert.NotEqual(1.0, heartGlyph.Opacity);
    }

    // TickPulse skips all its own motion — and, with it, the
    // HeartBadge.IsVisible check that would otherwise call TickHeart — once
    // the window is not visible. "Not visible" here means IsVisible is
    // false on a never-shown orb, the state most orbs in this whole suite
    // are actually left in.
    [AvaloniaFact]
    public void TickPulseOnAnUnshownOrbResetsTheScaleAndSkipsTheHeart()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.ApplyState("waiting");

        var orbScale = (Avalonia.Media.ScaleTransform)typeof(OrbWindow)
            .GetField("_orbScale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orb)!;

        orb.TickPulse();

        Assert.Equal(1.0, orbScale.ScaleX);
        Assert.Equal(1.0, orbScale.ScaleY);
    }
}
