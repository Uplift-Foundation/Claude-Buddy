namespace ClaudeBuddy
{
    // How much room an arrow between two orbs needs, on its own so that both
    // the thing that draws arrows and the thing that positions orbs can agree
    // about it — and so the arrangement tests can ask without dragging a window
    // toolkit in behind them.
    //
    // The two used to disagree: orbs were fanned closer than an arrow can be
    // drawn, so TeamLinks silently parked every one and a team looked like
    // unrelated orbs sitting near each other.
    internal static class TeamLinkGeometry
    {
        // Clearance between an orb's visible edge and the arrow. The lead end
        // gets more so the arrowhead reads as pointing *at* the orb rather than
        // touching it.
        public const double MemberGap = 4;
        public const double LeadGap = 7;

        public const double HeadLength = 9;

        // An arrow shorter than its own head is a blob, not an arrow.
        public const double MinimumLength = HeadLength + 4;

        // Edge to edge, everything an arrow needs before it is worth drawing.
        public const double RequiredClearance = MemberGap + LeadGap + MinimumLength;

        // The same thing as a centre-to-centre distance, in whatever units the
        // radii are given in. Both radii and the answer are DIPs; multiply by
        // the display scale to compare against screen pixels.
        public static double MinimumCentreDistance(double memberRadius, double leadRadius)
            => memberRadius + leadRadius + RequiredClearance;
    }
}
