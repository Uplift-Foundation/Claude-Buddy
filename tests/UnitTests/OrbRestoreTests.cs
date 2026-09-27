using Avalonia;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-198: a saved orb spot is a top-left corner, so restoring it at a different
// size has to move the corner by half the size difference or the orb comes back
// off-centre. SessionManager.RestoredTopLeft is that correction, pure so it can
// be checked here per outcome.
public class OrbRestoreTests
{
    private static ClaudeBuddySettings.OrbPlacement At(double? size) => new(300, 200, size);

    [Theory]
    [InlineData(2.0, 1.0, 1.0, 328, 228)]   // saved big, restored small: corner moves in by 28
    [InlineData(1.0, 2.0, 1.0, 272, 172)]   // and the other way
    [InlineData(2.0, 1.0, 2.0, 356, 256)]   // the shift is in physical pixels
    [InlineData(1.5, 1.5, 1.0, 300, 200)]   // same size, same corner
    public void TheCornerMovesByHalfTheSizeDifference(double saved, double now, double scaling, int x, int y)
    {
        Assert.Equal(new PixelPoint(x, y), SessionManager.RestoredTopLeft(At(saved), now, scaling));
    }

    [Fact]
    public void ASpotSavedBeforeOrbsHadASizeReadsAsTheDefault()
    {
        Assert.Equal(new PixelPoint(300, 200), SessionManager.RestoredTopLeft(At(null), 1.0, 1.0));
        Assert.Equal(new PixelPoint(272, 172), SessionManager.RestoredTopLeft(At(null), 2.0, 1.0));
    }

    [Theory]
    [InlineData(2.0, 1.0)]
    [InlineData(0.6, 2.0)]
    [InlineData(1.25, 0.75)]
    public void TheCentreIsWhatSurvives(double saved, double now)
    {
        const double scaling = 1.5;
        var placement = At(saved);
        var corner = SessionManager.RestoredTopLeft(placement, now, scaling);

        var savedCentre = placement.X + OrbSizing.CentreDip(saved) * scaling;
        var restoredCentre = corner.X + OrbSizing.CentreDip(now) * scaling;

        Assert.Equal(savedCentre, restoredCentre, 0);
    }
}
