using System;

namespace ClaudeBuddy
{
    // Decides when a scroll-to-end settle loop has watched layout long enough
    // to stop, with no window, no dispatcher and no scroll viewer behind it —
    // the same reasoning that keeps OrbArrangement and OrbGlyph pure, so the
    // rule can be tested as a rule rather than only by driving a headless
    // ChatPanel through however many render ticks a particular case happens
    // to need.
    //
    // CB-51 fixed a two-tick version of this: one dispatcher post got rows
    // into the tree, a second at Background gave them a measure, and that was
    // assumed to be enough. CB-160 is what happens when it isn't — an inline
    // picture decoding on a worker thread, a markdown block reflowing, an
    // attachment row resizing, anything that grows a turn's height on a tick
    // later than the second one — and the fixed count has no way to notice
    // and no third correction. This tracker replaces the count with a rule
    // that watches the extent itself: keep reporting "watch again" until it
    // stops moving, rather than guessing how many ticks moving takes.
    internal sealed class ScrollSettleTracker
    {
        // Two rather than one: a single unchanged reading only means the
        // layout pass that just ran didn't happen to touch this row's
        // height, which is true of plenty of ordinary passes and would end
        // the loop on its very first tick almost every time. Requiring a
        // second confirms the extent has actually stopped rather than merely
        // having a quiet pass in the middle of still growing.
        internal const int StableReadingsRequired = 2;

        // A hard ceiling so a transcript whose content never stops growing —
        // which streaming text does, but that path has its own per-turn
        // scroll call and never reaches this tracker — cannot pin the loop
        // open for the life of the panel. Twenty render passes is far more
        // than any realistic late-arriving row needs and still bounded well
        // under a frame budget's worth of visible delay.
        internal const int MaxReadings = 20;

        private double? _lastExtent;
        private int _stableCount;
        private int _readings;

        // Whether the caller should keep taking readings. Starts true;
        // Observe flips it false once the extent has held steady for
        // StableReadingsRequired readings in a row, or once MaxReadings has
        // been spent regardless of stability.
        internal bool ShouldKeepWatching { get; private set; } = true;

        // Hands the tracker the extent height as it stands right now. Safe
        // to call after ShouldKeepWatching has gone false — it does nothing,
        // rather than the caller having to remember to stop asking.
        internal void Observe(double extent)
        {
            if (!ShouldKeepWatching) return;

            _readings++;

            // The first reading has nothing to compare against, so it always
            // resets the run rather than starting one — there is no way yet
            // to say the extent has "stopped" doing anything.
            if (_lastExtent is { } last && Math.Abs(extent - last) < 0.5)
                _stableCount++;
            else
                _stableCount = 0;

            _lastExtent = extent;

            if (_stableCount >= StableReadingsRequired || _readings >= MaxReadings)
                ShouldKeepWatching = false;
        }
    }
}
