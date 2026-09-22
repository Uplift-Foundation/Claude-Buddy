using Avalonia;

namespace ClaudeBuddy
{
    // Where a chat panel opens, once more than one of them can be on screen
    // at a time.
    //
    // Pulled out of ChatPanel.Reposition() and made pure — no window, no
    // screen, no settings — for the same reason OrbArrangement was: a person
    // notices a panel drawn on top of a pinned one only by looking at the
    // screen, and CB-110 makes that possible for the first time. Before
    // pinning existed there was never more than one panel open, so
    // Reposition() never had to ask whether its answer collided with
    // anything else.
    //
    // "Today's answer first" is deliberate, not a starting point to improve
    // on: below the anchor, centred, flipped above the anchor when below
    // would run off the bottom, clamped into the work area. That is exactly
    // Reposition()'s own maths, and with no pinned panels in the way the two
    // must agree pixel for pixel — a chat panel that happens to be the only
    // one open should look exactly like it did before this feature shipped.
    // Only when that answer collides with something already pinned does this
    // go further: flip the other way, and failing that, slide sideways along
    // whichever side clears first. Overlap beats off-screen at every step,
    // because a panel outside the work area can be impossible to reach again
    // (see the Reposition() comment on why a clamped-upward panel was
    // rejected in favour of flipping), while an overlapping one is merely
    // inconvenient and can be dragged.
    internal static class ChatPanelPlacement
    {
        // The minimum horizontal slide step. `gap` is normally big enough on
        // its own (it is the space already left between an orb and its
        // panel), but a user can drag Gap down near zero in settings, and a
        // zero-pixel slide would never separate two rectangles no matter how
        // many times it is applied.
        private const int MinSlideStep = 8;

        internal static PixelPoint Resolve(
            PixelPoint anchor,
            PixelSize size,
            int gap,
            PixelRect work,
            IReadOnlyList<PixelRect> occupied)
        {
            var below = Clamp(Candidate(anchor, size, gap, work, above: false), size, work);
            var above = Clamp(Candidate(anchor, size, gap, work, above: true), size, work);

            // Below is "today's answer" whenever it fits the work area on its
            // own, exactly as Reposition() decides it today; only when below
            // would run past the bottom does today's code reach for above
            // instead. Recomputing that same choice here, rather than always
            // trying both and picking whichever misses `occupied`, is what
            // keeps the empty-occupied-list case bit-identical to the old
            // behaviour instead of merely equivalent to it.
            var fitsBelow = anchor.Y + gap + size.Height <= work.Bottom;
            var primary = fitsBelow ? below : above;
            var secondary = fitsBelow ? above : below;

            if (!Intersects(primary, occupied)) return primary.Position;
            if (!Intersects(secondary, occupied)) return secondary.Position;

            // Both vertical sides collide with something pinned. Slide each
            // side sideways — right first, then left, since a chat panel
            // opens to the right of where most orbs sit on a wide screen and
            // a rightward slide is the one least likely to run off the work
            // area on the first try. Try the side that was today's answer
            // before the one that was the flip, so a tie between two equally
            // clear slid positions still favours the un-flipped side.
            var step = Math.Max(gap, MinSlideStep);

            foreach (var side in new[] { primary, secondary })
            {
                for (var dx = step; dx <= work.Width; dx += step)
                {
                    var right = Clamp(new PixelRect(new PixelPoint(side.X + dx, side.Y), size), size, work);
                    if (!Intersects(right, occupied)) return right.Position;

                    var left = Clamp(new PixelRect(new PixelPoint(side.X - dx, side.Y), size), size, work);
                    if (!Intersects(left, occupied)) return left.Position;
                }
            }

            // Nothing on the tried lattice ever clears every pinned panel —
            // the work area is small, or thoroughly tiled with pinned panels.
            // Falling back to the clamped, un-slid primary candidate keeps
            // the panel on screen and reachable rather than pushed somewhere
            // arbitrary; overlapping a pinned panel is a panel the user can
            // still see and drag, which is the whole reason overlap beats
            // off-screen throughout this file.
            return primary.Position;
        }

        // Reposition()'s own maths, unclamped: below the anchor, centred, or
        // (when asked for the flip) above it instead. Kept separate from the
        // clamp so the "does below fit" test above can be answered before
        // anything is clamped into the work area.
        private static PixelRect Candidate(PixelPoint anchor, PixelSize size, int gap, PixelRect work, bool above)
        {
            var y = above ? anchor.Y - gap - size.Height : anchor.Y + gap;
            var x = anchor.X - size.Width / 2;
            return new PixelRect(new PixelPoint(x, y), size);
        }

        // Reposition()'s Math.Clamp calls, verbatim: clamped into `work`, and
        // when the panel is bigger than the work area itself, pinned to its
        // top-left rather than centred or left to run negative — the same
        // Math.Max guard the original code uses.
        private static PixelRect Clamp(PixelRect candidate, PixelSize size, PixelRect work)
        {
            var x = Math.Clamp(candidate.X, work.X, Math.Max(work.X, work.Right - size.Width));
            var y = Math.Clamp(candidate.Y, work.Y, Math.Max(work.Y, work.Bottom - size.Height));
            return new PixelRect(new PixelPoint(x, y), size);
        }

        // CB-111: a pinned panel's saved position, pulled back onto whatever
        // work area it is being restored into. Reuses Clamp rather than
        // duplicating its Math.Clamp/Math.Max pair, for the same reason
        // SessionManager.ClampIntoWork is shared between a restored orb
        // position and one rescued from a display that changed shape — the
        // two callers clamp for the same reason, and drifting apart would
        // mean one of them stranding a panel the other would have saved.
        //
        // Sized to the panel rather than to a fixed orb footprint: unlike an
        // orb, a chat panel's width and height vary per agent (see
        // ChatPanelSizes), and clamping only the top-left corner into the
        // work area — the way RestoreOrbPosition/ClampIntoWork do for a
        // square 56x56 orb — would leave a wide or tall panel's far edge
        // hanging off a monitor that shrank since the position was saved.
        internal static PixelPoint ClampSavedPosition(PixelPoint saved, PixelSize size, PixelRect work) =>
            Clamp(new PixelRect(saved, size), size, work).Position;

        private static bool Intersects(PixelRect candidate, IReadOnlyList<PixelRect> occupied)
        {
            for (var i = 0; i < occupied.Count; i++)
            {
                if (candidate.Intersects(occupied[i])) return true;
            }

            return false;
        }
    }
}
