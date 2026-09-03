using System;
using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Which of a freshly-fetched chat.history page is the picture a live "agent"
// event was talking about, when nothing in the live event ties back to a
// history message directly — see OpenClawSessions.BestImageMatch's own
// comment, and OpenClawLiveImageResolutionTests for the gateway-backed half
// this feeds.
public class OpenClawBestImageMatchTests
{
    private static HistoryTurn Turn(string? imageUrl, DateTimeOffset at, string alt = "") =>
        new(ChatRole.Assistant, "", imageUrl, alt, at, null, null);

    [Fact]
    public void PicksTheTurnNearestInTime()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn>
        {
            Turn("https://x/far.png", near.AddMinutes(-10)),
            Turn("https://x/near.png", near.AddSeconds(2)),
            Turn("https://x/farther.png", near.AddMinutes(10)),
        };

        Assert.Equal("https://x/near.png", OpenClawSessions.BestImageMatch(turns, near)!.Value.ImageUrl);
    }

    [Fact]
    public void ATurnWithNoPictureIsNeverPicked()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn>
        {
            Turn(null, near), // exact time match, but nothing to show
            Turn("https://x/only.png", near.AddMinutes(5)),
        };

        Assert.Equal("https://x/only.png", OpenClawSessions.BestImageMatch(turns, near)!.Value.ImageUrl);
    }

    [Fact]
    public void NoPictureAnywhereInThePageReturnsNull()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn> { Turn(null, near) };

        Assert.Null(OpenClawSessions.BestImageMatch(turns, near));
    }

    [Fact]
    public void AnEmptyPageReturnsNull()
    {
        Assert.Null(OpenClawSessions.BestImageMatch(
            Array.Empty<HistoryTurn>(), DateTimeOffset.Now));
    }
}
