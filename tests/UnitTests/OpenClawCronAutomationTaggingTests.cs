using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-115: TurnsFromHistory's half of the cron-recovery trigger — tagging a
// HistoryTurn with the automation that produced it, so the async pass in
// OpenClawSessions.FetchHistoryPageAsync knows which turns are even worth a
// cron.runs lookup. This is the pure, fixture-driven half; the RPC-calling
// half (paging, caching, the actual recovery) is covered end to end in
// OpenClawCronRunRecoveryEndToEndTests against a fake gateway.
//
// Serialised on the settings collection for the same reason
// OpenClawHistoryTurnTests is: TurnsFromHistory resolves speaker names and
// colours through the process-wide identity table.
[Collection("Settings")]
public class OpenClawCronAutomationTaggingTests
{
    private static JsonElement Messages(string json) => JsonDocument.Parse(json).RootElement;

    private static System.Collections.Generic.List<HistoryTurn> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(Messages(json), null);

    // The shape this ticket exists for: a caption plus a bare filename, no
    // MEDIA: line and no rooted path anywhere — LocalMediaPathFrom resolves
    // nothing (see its own tests), so this lands in the plain-text fallback,
    // and that is exactly where Automation gets attached.
    [Fact]
    public void AnUnresolvedCronAutomationTurnIsTaggedWithItsJobId()
    {
        var turns = Turns("""
        [{"role":"assistant","content":"sunset over the harbour today\nbare.png",
          "openclawAutomation":{"kind":"cron","jobId":"job-1","runId":"cron:job-1:123"}}]
        """);

        var turn = Assert.Single(turns);
        // CB-115: ImageUrl stopped being the witness once CB-107 landed — the
        // named-path arm resolves a bare filename to the ~/.openclaw/media/
        // guess and sets a route for it independently of this feature.
        // ImageSourcePath separates the two: the guess means nothing was
        // recovered, a rooted run-record path means it was.
        Assert.StartsWith("~/.openclaw/media/", turn.ImageSourcePath);
        Assert.NotNull(turn.Automation);
        Assert.Equal("job-1", turn.Automation!.Value.JobId);
    }

    // Not "cron" — a text automation of a different kind must not be tagged,
    // since the trigger's first conjunct is specifically kind == "cron".
    [Fact]
    public void ANonCronAutomationKindIsNotTagged()
    {
        var turns = Turns("""
        [{"role":"assistant","content":"just an ordinary automated reply",
          "openclawAutomation":{"kind":"task","jobId":"job-1"}}]
        """);

        Assert.Null(Assert.Single(turns).Automation);
    }

    [Fact]
    public void AMessageWithNoAutomationAtAllIsNotTagged()
    {
        var turns = Turns("""[{"role":"assistant","content":"an ordinary reply"}]""");

        Assert.Null(Assert.Single(turns).Automation);
    }

    // The skip case, and the most valuable assertion in this file: a turn
    // whose picture already resolved through an earlier arm must not be
    // tagged, because FetchHistoryPageAsync's recovery loop uses Automation
    // being non-null as its only signal to spend an RPC at all. Losing this
    // would mean an already-resolved turn quietly costs a cron.runs call on
    // every history load forever.
    [Fact]
    public void ATurnThatAlreadyResolvedViaTheDeliveredMirrorArmIsNotTagged()
    {
        var turns = Turns("""
        [{"role":"assistant","model":"delivery-mirror","content":"pic.png",
          "openclawAutomation":{"kind":"cron","jobId":"job-1"}}]
        """);

        var turn = Assert.Single(turns);
        Assert.NotNull(turn.ImageUrl);
        Assert.Null(turn.Automation);
    }

    // Same skip, through the other resolving arm: a message whose entire
    // trimmed text is already a rooted MEDIA:-less path resolves through
    // LocalMediaPathFrom's bare-path rule directly, with no recovery needed.
    [Fact]
    public void ATurnWhoseOnlyResolutionWasTheSharedMediaGuessIsStillTagged()
    {
        var turns = Turns("""
        [{"role":"assistant","content":"/home/agent/outputs/pic.png",
          "openclawAutomation":{"kind":"cron","jobId":"job-1"}}]
        """);

        var turn = Assert.Single(turns);
        // Corrected with CB-115's fix: this asserted the turn was NOT tagged,
        // on the reasoning that the named-path arm had already resolved it.
        // That reasoning was wrong and it made the feature inert — the arm
        // resolves a bare filename to a guess that will 404, so "already
        // resolved" was never true. Tagging is what lets the run record
        // override the guess.
        Assert.NotNull(turn.ImageUrl);
        Assert.NotNull(turn.Automation);
    }
}
