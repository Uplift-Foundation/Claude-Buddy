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
    private static HistoryTurn Turn(
        string? imageUrl, DateTimeOffset at, ChatRole role = ChatRole.Assistant, string alt = "") =>
        new(role, "", imageUrl, alt, at, null, null);

    // A turn carrying its picture inline instead of by url — the shape this
    // gateway actually sends (CB-91).
    private static HistoryTurn BytesTurn(
        byte[]? bytes, DateTimeOffset at, ChatRole role = ChatRole.Assistant) =>
        new(role, "", null, "", at, null, null, false, bytes);

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

        Assert.Equal("https://x/near.png",
            OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near)!.Value.ImageUrl);
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

        Assert.Equal("https://x/only.png",
            OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near)!.Value.ImageUrl);
    }

    [Fact]
    public void NoPictureAnywhereInThePageReturnsNull()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn> { Turn(null, near) };

        Assert.Null(OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near));
    }

    [Fact]
    public void AnEmptyPageReturnsNull()
    {
        Assert.Null(OpenClawSessions.BestImageMatch(
            Array.Empty<HistoryTurn>(), ChatRole.Assistant, DateTimeOffset.Now));
    }

    // A session's own chat.history mixes that agent's replies (role
    // "assistant") with everyone else's messages arriving as its input (role
    // "user" — a room's other agents, or a real person). A picture someone
    // else posted seconds before the agent's own reply is not the agent's
    // picture, even though it is the nearest one in time — QA (CB-87) found
    // this as a real defect in the pre-role-filter version.
    [Fact]
    public void ATurnFromSomeoneElseIsNeverPickedEvenWhenNearerInTime()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn>
        {
            Turn("https://x/someone-elses.png", near.AddSeconds(1), ChatRole.User),
            Turn("https://x/the-agents-own.png", near.AddMinutes(3), ChatRole.Assistant),
        };

        Assert.Equal("https://x/the-agents-own.png",
            OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near)!.Value.ImageUrl);
    }

    // The live turn can itself be role System (a "thinking" stream, per
    // OpenClawChatSession.OnAgentText) — matching restricts to whatever role
    // the caller actually asks for, not just Assistant.
    [Fact]
    public void MatchingRespectsWhicheverRoleIsAsked()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn>
        {
            Turn("https://x/assistant.png", near, ChatRole.Assistant),
            Turn("https://x/system.png", near, ChatRole.System),
        };

        Assert.Equal("https://x/system.png",
            OpenClawSessions.BestImageMatch(turns, ChatRole.System, near)!.Value.ImageUrl);
    }

    // ---- turns carrying their picture inline (CB-91) ---------------------

    // The one that mattered: filtering on ImageUrl alone meant this gateway's
    // own shape could never match, so a live reply reconciled against
    // nothing at all.
    [Fact]
    public void ATurnCarryingBytesInsteadOfAUrlIsAMatch()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn> { BytesTurn(new byte[] { 1, 2, 3 }, near) };

        var match = OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near);
        Assert.Equal(new byte[] { 1, 2, 3 }, match!.Value.ImageBytes);
    }

    [Fact]
    public void ABytesTurnIsStillChosenByNearestTime()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn>
        {
            BytesTurn(new byte[] { 1 }, near.AddMinutes(-10)),
            BytesTurn(new byte[] { 2 }, near.AddSeconds(2)),
        };

        Assert.Equal(new byte[] { 2 },
            OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near)!.Value.ImageBytes);
    }

    // An empty byte array is no more a picture than a missing url is.
    [Fact]
    public void ATurnWithEmptyBytesIsNotAMatch()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn> { BytesTurn(Array.Empty<byte>(), near) };

        Assert.Null(OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near));
    }

    // The role filter applies to bytes-bearing turns the same way, so
    // somebody else's inline picture is not handed to an agent's reply.
    [Fact]
    public void ABytesTurnFromAnotherRoleIsNeverPicked()
    {
        var near = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var turns = new List<HistoryTurn>
        {
            BytesTurn(new byte[] { 1 }, near.AddSeconds(1), ChatRole.User),
            BytesTurn(new byte[] { 2 }, near.AddMinutes(3), ChatRole.Assistant),
        };

        Assert.Equal(new byte[] { 2 },
            OpenClawSessions.BestImageMatch(turns, ChatRole.Assistant, near)!.Value.ImageBytes);
    }
}
