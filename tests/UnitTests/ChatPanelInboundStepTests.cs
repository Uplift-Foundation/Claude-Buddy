using Xunit;

namespace Orbweaver.Tests;

// CB-267: the chat panel's confirmation box as two pure functions — which
// stage a click or an answer moves it to, and what each stage draws. No window:
// ChatPanelPeerInboundTests in tests/UiTests drives the same box through real
// clicks.
public class ChatPanelInboundStepTests
{
    private const string Home = "/Users/someone";

    private static PeerInboundOffer Offer(bool canEdit = true) => new(
        PeerInbound.OfferText("job-hunter"), Home + "/.claude-board/settings.json", canEdit);

    // Whatever PeerInbound.DisplayPath makes of the offer's file — its own
    // rule, tested where it lives; what is asserted here is that the view uses
    // it rather than the raw path.
    private static string ShownPath => PeerInbound.DisplayPath(Offer().SettingsPath, Home);

    // --- the state machine, every arrow -------------------------------------

    [Theory]
    [InlineData("Offer", "Ask", "Confirming")]
    [InlineData("Failed", "Ask", "Confirming")]
    [InlineData("Confirming", "Cancel", "Offer")]
    [InlineData("Confirming", "Confirm", "Working")]
    [InlineData("Working", "Succeeded", "Done")]
    [InlineData("Working", "Failed", "Failed")]
    // Everything else stands still: a second Confirm mid-write, a late answer
    // after a cancel, a click on a box that is already done.
    [InlineData("Working", "Confirm", "Working")]
    [InlineData("Offer", "Succeeded", "Offer")]
    [InlineData("Done", "Ask", "Done")]
    [InlineData("Offer", "Cancel", "Offer")]
    // Names rather than the enums themselves: a public test method cannot take
    // an internal type.
    public void EachArrowOfTheConfirmation(string from, string input, string to) =>
        Assert.Equal(Enum.Parse<ChatPanel.InboundStage>(to),
            ChatPanel.InboundStep(Enum.Parse<ChatPanel.InboundStage>(from), Enum.Parse<ChatPanel.InboundInput>(input)));

    // --- what each state draws ----------------------------------------------

    [Fact]
    public void NoOfferAndNothingInFlightMeansNoBox()
    {
        Assert.Null(ChatPanel.InboundViewFor(ChatPanel.InboundStage.Offer, null, false, Home));
        Assert.Null(ChatPanel.InboundViewFor(ChatPanel.InboundStage.Confirming, null, false, Home));
    }

    [Fact]
    public void TheOfferNamesTheFileUnderHomeAndOffersTheButton()
    {
        var view = ChatPanel.InboundViewFor(ChatPanel.InboundStage.Offer, Offer(), false, Home)!.Value;

        Assert.Equal(PeerInbound.OfferText("job-hunter"), view.Text);
        Assert.Equal(ShownPath, view.Path);
        Assert.Equal(new ChatPanel.InboundView(view.Text, view.Path, true, false, false, false), view);
    }

    [Fact]
    public void AnUneditableOfferIsSaidWithNoButtonInAnyStage()
    {
        foreach (var stage in new[] { ChatPanel.InboundStage.Offer, ChatPanel.InboundStage.Confirming })
        {
            var view = ChatPanel.InboundViewFor(stage, Offer(canEdit: false), false, Home)!.Value;
            Assert.Equal(new ChatPanel.InboundView(Offer().Explanation, ShownPath,
                false, false, false, false), view);
        }
    }

    [Fact]
    public void ConfirmingSaysTheTradeOffAndOffersBothAnswers()
    {
        var view = ChatPanel.InboundViewFor(ChatPanel.InboundStage.Confirming, Offer(), false, Home)!.Value;

        Assert.Equal(new ChatPanel.InboundView(PeerInbound.TradeOff, ShownPath,
            false, true, true, false), view);
    }

    // The write raises InboundChanged before it returns, so the offer can be
    // gone while the panel is still Working — the box must not blink out.
    [Fact]
    public void WorkingOutlivesTheOffer()
    {
        Assert.Equal(new ChatPanel.InboundView(PeerInbound.TradeOff, null, false, false, false, false),
            ChatPanel.InboundViewFor(ChatPanel.InboundStage.Working, null, false, Home));
        Assert.Equal(ShownPath,
            ChatPanel.InboundViewFor(ChatPanel.InboundStage.Working, Offer(), false, Home)!.Value.Path);
    }

    [Fact]
    public void DoneOffersTheResendOnlyWhenSomethingIsHeld()
    {
        Assert.Equal(new ChatPanel.InboundView(PeerInbound.DoneText, null, false, false, false, true),
            ChatPanel.InboundViewFor(ChatPanel.InboundStage.Done, null, true, Home));
        Assert.Equal(new ChatPanel.InboundView(PeerInbound.DoneText, null, false, false, false, false),
            ChatPanel.InboundViewFor(ChatPanel.InboundStage.Done, Offer(), false, Home));
    }

    [Fact]
    public void FailedOffersToTryAgainOnlyWhileTheFileIsStillEditable()
    {
        Assert.Equal(new ChatPanel.InboundView(ChatPanel.InboundFailedText, ShownPath,
            true, false, false, false), ChatPanel.InboundViewFor(ChatPanel.InboundStage.Failed, Offer(), false, Home));
        Assert.Equal(new ChatPanel.InboundView(ChatPanel.InboundFailedText, null, false, false, false, false),
            ChatPanel.InboundViewFor(ChatPanel.InboundStage.Failed, null, false, Home));
    }
}
