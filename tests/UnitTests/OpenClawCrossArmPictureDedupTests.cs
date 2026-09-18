using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-98's cross-arm case, second instance: an agent's own inline image block
// and the gateway's delivery-mirror record can each put the same picture on
// screen. TurnsFromHistory already collapses the sibling case — a named
// MEDIA: path plus its mirror — by matching the two arms' resolved *path*
// (see OpenClawNamedPictureOnHistoryTests). An inline block has no path at
// all (CB-91: the block is bare base64, no filename anywhere in it), so this
// case has to be told apart a different way: identical bytes, but only once
// the two turns are also known to have come from different arms.
//
// This is currently LATENT rather than observed on screen: the 7 cross-arm
// files QA measured over 42 real delivery-mirror records live under a path
// the gateway currently refuses to serve, so nothing here was captured from a
// live render. The fixtures below model the shape the ticket describes —
// synthetic bytes standing in for a real picture — rather than pasting
// anything captured.
//
// Two halves, matching the two places the fix touches:
//
//   1. TurnsFromHistory sets HistoryTurn.Arm at parse time, for the two arms
//      that can produce a cross-arm duplicate (an inline block, a
//      delivery-mirror record) and nothing else.
//   2. CrossArmDuplicateMirrorIndices reads it back, paired with each
//      candidate's bytes, to decide which mirror turns are the same delivery
//      as an inline turn already on the page. It is pure and synchronous —
//      the caller (FetchHistoryPageAsync, excluded from coverage like every
//      other real gateway fetch in this file) is the one that actually fetches
//      a mirror turn's bytes; this method only ever compares bytes it is
//      handed.
[Collection("Settings")]
public class OpenClawCrossArmPictureDedupTests
{
    private static JsonElement Messages(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static List<HistoryTurn> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(Messages(json), null);

    private static HistoryTurn Turn(
        MediaSourceArm arm, byte[]? imageBytes = null, string? imageUrl = null) =>
        new(ChatRole.Assistant, "", imageUrl, "", DateTimeOffset.Now, null, null,
            ImageBytes: imageBytes, Arm: arm);

    // ---- HistoryTurn.Arm, set at parse time ------------------------------

    // The shape CB-91 says this gateway actually sends: bare base64 in
    // `data`, no url. That is exactly the turn the cross-arm dedup needs to
    // recognise as a candidate.
    [Fact]
    public void AnInlineImageBlockIsTaggedInline()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"image","data":"aGVsbG8=","mimeType":"image/png"}]}]
        """));

        Assert.Equal(MediaSourceArm.Inline, turn.Arm);
    }

    // The url-carrying shape of an image block — kept for a deployment that
    // might send it, per InlineImageBytes' own header comment — has no bytes
    // to compare against a mirror's, so it is not tagged as a dedup
    // candidate even though it is still drawn as a picture.
    [Fact]
    public void AUrlCarryingImageBlockIsNotTaggedInline()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"image","url":"https://example.invalid/a.png"}]}]
        """));

        Assert.Equal(MediaSourceArm.None, turn.Arm);
    }

    [Fact]
    public void ADeliveryMirrorRecordIsTaggedMirror()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]}]
        """));

        Assert.Equal(MediaSourceArm.Mirror, turn.Arm);
    }

    // The named-path arm has its own, separate collapse (path-keyed, already
    // shipped) and does not need this discriminator — it stays at the
    // default so CrossArmDuplicateMirrorIndices never mistakes it for either
    // side of the bytes comparison.
    [Fact]
    public void ANamedPathTurnIsNotTaggedEitherArm()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"~/.openclaw/media/browser/03a1be83.png"}]}]
        """));

        Assert.Equal(MediaSourceArm.None, turn.Arm);
    }

    // An ordinary text turn, no picture at all.
    [Fact]
    public void APlainTextTurnIsNotTaggedEitherArm()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"just talking, nothing attached"}]}]
        """));

        Assert.Equal(MediaSourceArm.None, turn.Arm);
    }

    // ---- CrossArmDuplicateMirrorIndices ----------------------------------

    // The collapse case: one delivery, seen through both arms. Identical
    // bytes, different arms — the mirror turn's index comes back as a
    // duplicate to drop.
    [Fact]
    public void AMirrorWithBytesMatchingAnInlineTurnIsADuplicate()
    {
        byte[] picture = { 1, 2, 3, 4, 5 };
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: picture),
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=a.png"),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [1] = picture });

        Assert.Equal(new[] { 1 }, doomed);
    }

    // Must-not-collapse #1: two mirror-derived turns, never compared against
    // each other regardless of their bytes. This is the exact shape QA's 5
    // real mirror+mirror pages have — two separate deliveries of a
    // same-named file, byte-identical on the wire, that must stay two
    // bubbles. With no Inline-arm turn present at all there is nothing for
    // either mirror to be paired against, so nothing is ever collapsed here.
    [Fact]
    public void TwoMirrorTurnsWithIdenticalBytesAreNeverCollapsed()
    {
        byte[] picture = { 9, 9, 9 };
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=a.png"),
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=a.png"),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [0] = picture, [1] = picture });

        Assert.Empty(doomed);
    }

    // Must-not-collapse #2: an inline-only page, no mirror at all. Nothing in
    // the fetched-bytes map to even consider.
    [Fact]
    public void AnInlineTurnWithNoMirrorOnThePageIsUntouched()
    {
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: new byte[] { 1, 2, 3 }),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]>());

        Assert.Empty(doomed);
    }

    // Must-not-collapse #3: a mirror-only page, no inline block anywhere.
    // The overwhelmingly common shape in the real corpus (CB-91) — this is
    // the case the caller's own "don't fetch at all" gate exists for, and
    // this method's early return matches it independently, so a caller that
    // fetched anyway still does not collapse anything.
    [Fact]
    public void AMirrorOnlyPageWithNoInlineTurnIsUntouched()
    {
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=a.png"),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [0] = new byte[] { 1, 2, 3 } });

        Assert.Empty(doomed);
    }

    // A mirror whose fetched bytes simply do not match any inline turn's
    // bytes — two genuinely different pictures, however they got there.
    [Fact]
    public void AMirrorWithDifferentBytesThanTheInlineTurnIsNotADuplicate()
    {
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: new byte[] { 1, 2, 3 }),
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=b.png"),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [1] = new byte[] { 9, 9, 9 } });

        Assert.Empty(doomed);
    }

    // A page can carry a real cross-arm pair *and* an unrelated second
    // mirror for a different file — only the matching one collapses, the
    // same "budget, not membership" shape CB-98's named-path merge already
    // proved out for the sibling case.
    [Fact]
    public void OnlyTheMatchingMirrorCollapsesWhenAnUnrelatedSecondMirrorIsOnThePage()
    {
        byte[] picture = { 4, 4, 4 };
        byte[] otherPicture = { 5, 5, 5 };
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: picture),
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=a.png"),
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=b.png"),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [1] = picture, [2] = otherPicture });

        Assert.Equal(new[] { 1 }, doomed);
    }

    // A caller handing in an out-of-range index — defensive, since the two
    // real callers never do this, but the method's own contract (an index
    // into `turns`) should not throw on a mismatched map.
    [Fact]
    public void AnOutOfRangeIndexInTheBytesMapIsIgnored()
    {
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: new byte[] { 1, 2, 3 }),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [7] = new byte[] { 1, 2, 3 } });

        Assert.Empty(doomed);
    }

    // A map entry pointing at a turn that is not actually Mirror-arm (a
    // Named or None turn sharing an index with the map by coincidence) must
    // never be treated as a candidate, even if its bytes happen to match.
    [Fact]
    public void AMapEntryForANonMirrorTurnIsIgnored()
    {
        byte[] picture = { 1, 2, 3 };
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: picture),
            Turn(MediaSourceArm.None),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [1] = picture });

        Assert.Empty(doomed);
    }

    // Empty fetched bytes for a mirror (a fetch that came back with nothing)
    // is not a match against anything.
    [Fact]
    public void EmptyFetchedBytesAreNeverADuplicate()
    {
        var turns = new List<HistoryTurn>
        {
            Turn(MediaSourceArm.Inline, imageBytes: Array.Empty<byte>()),
            Turn(MediaSourceArm.Mirror, imageUrl: "https://gateway.invalid/media?path=a.png"),
        };

        var doomed = OpenClawSessions.CrossArmDuplicateMirrorIndices(
            turns, new Dictionary<int, byte[]> { [1] = Array.Empty<byte>() });

        Assert.Empty(doomed);
    }
}
