using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-116: LocalMediaPathFrom's caption-plus-trailing-token arm (CB-107) is
// right for *finding* a picture and wrong for deciding whether a *failed*
// fetch is worth a visible "picture not shown" note — "I deleted photo.png"
// matches the identical shape as a real delivery, and unlike a real
// delivery the population of ordinary messages that happen to end in a
// filename is unbounded.
//
// These tests are the pure half: what HistoryTurn.Confidence comes out of
// TurnsFromHistory for each of CB-116's five provenance tiers. The note
// itself — whether a failed fetch actually stays silent — is proven at the
// ChatPanel level in ChatPanelImageNoteTests, and at the live-stream level
// in OpenClawLiveImageResolutionTests and OpenClawChatSessionTests; this
// file is about the decision those places act on, not the fetch around it.
[Collection("Settings")]
public class OpenClawMediaConfidenceTests
{
    private static System.Collections.Generic.List<HistoryTurn> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(json).RootElement, null);

    // ---- the two named cases from CB-116's own bug report -----------------

    // The bug itself: "I deleted photo.png" is exactly the caption-plus-
    // trailing-bare-filename shape CB-107 added, on an ordinary (non-
    // automation) turn. LocalMediaPathFrom still finds a candidate — the
    // fetch still gets attempted, which is the point of tiering on
    // confidence rather than refusing the match — but nothing here is an
    // automation delivery, so a failure must not explain itself.
    [Fact]
    public void IDeletedPhotoPngIsLowConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":"I deleted photo.png"}]
        """));

        // The fetch is still attempted — CB-116 does not touch the parser's
        // own looseness, only whether a *failure* gets a note.
        Assert.NotNull(turn.ImageUrl);
        Assert.Equal(MediaConfidence.Low, turn.Confidence);
    }

    // The other named shape: a rooted path in the same sentence. Do NOT tier
    // by shape — a rooted path in prose is exactly as untrustworthy as a bare
    // one, so this must land at the identical tier as the bare case above,
    // not at High merely for being rooted.
    [Fact]
    public void IDeletedARootedPhotoPngIsAlsoLowConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":"I deleted /Users/me/photo.png"}]
        """));

        Assert.NotNull(turn.ImageUrl);
        Assert.Equal(MediaConfidence.Low, turn.Confidence);
    }

    // ---- the three unconditionally-High sources ----------------------------

    // An explicit "MEDIA:" line is High regardless of automation — an agent
    // that writes the prefix is asserting a picture, full stop.
    [Fact]
    public void AMediaLineIsHighConfidenceEvenWithNoAutomation()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":"here you go\nMEDIA:/tmp/pic.png"}]
        """));

        Assert.NotNull(turn.ImageUrl);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // A delivery-mirror record is the gateway's own word that it delivered
    // something — never a guess about an agent's prose.
    [Fact]
    public void ADeliveryMirrorIsHighConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"sample_sunrise_100200300.png"}]}]
        """));

        Assert.NotNull(turn.ImageUrl);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // The regression guard for provenance-vs-shape: a delivery-mirror's own
    // text is a bare filename with no caption around it — exactly the shape
    // a naive "bare filename = low confidence" fix would have caught and
    // gotten wrong. It is High because the gateway wrote the record, not
    // because of anything about the text's shape.
    [Fact]
    public void ADeliveryMirrorsBareFilenameShapeDoesNotMakeItLowConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"a.png"}]}]
        """));

        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // ---- the automation-gated tier -----------------------------------------

    // The shape CB-115 exists for: a caption plus a bare filename, tagged
    // with a cron automation. High, because a delivery ran — even though the
    // candidate itself is only a trailing token, exactly the same shape as
    // the two Low cases above.
    [Fact]
    public void ATrailingTokenOnACronAutomationTurnIsHighConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":"sunset over the harbour today\nbare.png",
          "openclawAutomation":{"kind":"cron","jobId":"job-1","runId":"cron:job-1:123"}}]
        """));

        Assert.NotNull(turn.ImageUrl);
        Assert.NotNull(turn.Automation);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // A non-cron automation kind is not confirmed cron delivery — the same
    // "kind == cron" gate OpenClawCronAutomationTaggingTests already covers
    // for the Automation field itself applies identically to Confidence.
    [Fact]
    public void ATrailingTokenOnANonCronAutomationTurnIsLowConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":"here's the render\nbare.png",
          "openclawAutomation":{"kind":"task","jobId":"job-1"}}]
        """));

        Assert.NotNull(turn.ImageUrl);
        Assert.Null(turn.Automation);
        Assert.Equal(MediaConfidence.Low, turn.Confidence);
    }

    // A turn with no candidate at all still carries a tier, for the CB-115
    // recovery pass that may fill in a picture later purely from the
    // automation tag — see FetchHistoryPageAsync's override, which restates
    // High explicitly rather than relying on this default carrying forward.
    [Fact]
    public void AnUnresolvedCronTurnWithNoCandidateIsStillTaggedHighConfidence()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":"all systems nominal, nothing to report",
          "openclawAutomation":{"kind":"cron","jobId":"job-1"}}]
        """));

        Assert.Null(turn.ImageUrl);
        Assert.NotNull(turn.Automation);
        Assert.Equal(MediaConfidence.High, turn.Confidence);
    }

    // ---- CB-116: the tier must survive CB-115's own lesson -----------------
    //
    // CB-115 was inert for months because CB-107 landing changed what
    // ImageUrl meant out from under it, and nothing pinned the earlier
    // meaning down. The same risk applies here: whether this page's harvest
    // (CB-94's MediaPathsByFileName) happens to resolve a bare filename's
    // real directory must never change whether a failure gets to explain
    // itself — Confidence is decided from provenance (Explicit, automation),
    // never from whether the resolved path is a guess or a real directory.
    //
    // Two fixtures, identical automation and identical trailing-token
    // candidate, differing only in whether another message on the page
    // happens to mention "shared.png"'s real directory — which is exactly
    // what flips ResolveLocalMediaPath's guess into a harvested answer.
    // The harvest source has to be a message whose own raw JSON contains the
    // rooted path as a standalone token — MediaPathsByFileName scans quoted
    // JSON strings, not the rendered text — so this is a second message
    // whose entire content is the bare rooted path, the same shape
    // AFileDrawnByBothArmsIsDrawnOnce already relies on to prove the harvest
    // fires at all.
    [Fact]
    public void TheTierIsHighWhetherOrNotTheHarvestResolvedTheGuess()
    {
        const string automated = """
        {"role":"assistant","content":"caption\nshared.png",
         "openclawAutomation":{"kind":"cron","jobId":"job-1"}}
        """;

        var withoutHarvest = Turns($"[{automated}]");
        var withHarvest = Turns($$"""
        [{{automated}},
         {"role":"assistant","content":"/home/agent/outputs/shared.png"}]
        """);

        var unresolved = Assert.Single(withoutHarvest);
        Assert.StartsWith(OpenClawSessions.SharedMediaDir, unresolved.ImageSourcePath);
        Assert.Equal(MediaConfidence.High, unresolved.Confidence);

        var resolved = withHarvest[0];
        Assert.Equal("/home/agent/outputs/shared.png", resolved.ImageSourcePath);
        Assert.Equal(MediaConfidence.High, resolved.Confidence);
    }

    // The Low-tier twin of the same guard: a non-automation turn stays Low
    // whether or not the harvest happens to resolve a real directory for it.
    [Fact]
    public void TheTierIsLowWhetherOrNotTheHarvestResolvedTheGuessOnANonAutomationTurn()
    {
        const string ordinary = """{"role":"assistant","content":"caption\nshared.png"}""";

        var withoutHarvest = Turns($"[{ordinary}]");
        var withHarvest = Turns($$"""
        [{{ordinary}},
         {"role":"assistant","content":"/home/agent/outputs/shared.png"}]
        """);

        var unresolved = Assert.Single(withoutHarvest);
        Assert.StartsWith(OpenClawSessions.SharedMediaDir, unresolved.ImageSourcePath);
        Assert.Equal(MediaConfidence.Low, unresolved.Confidence);

        var resolved = withHarvest[0];
        Assert.Equal("/home/agent/outputs/shared.png", resolved.ImageSourcePath);
        Assert.Equal(MediaConfidence.Low, resolved.Confidence);
    }
}
