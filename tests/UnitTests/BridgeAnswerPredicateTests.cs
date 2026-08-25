using Xunit;

namespace ClaudeBuddy.UnitTests;

// The two predicates that decide when the relay has heard back.
//
// AskAsync types a prompt into a live Claude Code session and reads its pane
// until one of these says yes. So a predicate that is too strict does not fail —
// it waits, and the relay then reports "not answering" about a session that
// answered perfectly well. That is the failure mode worth testing, and it is why
// both predicates are deliberately loose: the question is "does this look like
// the tool answered", with parsing happening afterwards.
public class BridgeAnswerPredicateTests
{
    // ---- the peer list ---------------------------------------------------

    [Fact]
    public void ThePeerListHeaderIsRecognised()
    {
        Assert.True(BridgeProtocol.LooksLikeAgentList(
            "Peer sessions (2):\n  mac-mini/zara  idle"));
    }

    // The "none" case matters as much as the list case. Without it, a machine
    // that legitimately has no peers never satisfies the predicate, times out,
    // and is reported as not answering rather than as empty — which reads to the
    // user as a broken relay instead of an idle one.
    [Fact]
    public void HavingNoPeersIsAlsoAnAnswer()
    {
        Assert.True(BridgeProtocol.LooksLikeAgentList("There are no peer sessions right now."));
    }

    // The no-peer phrasing is a sentence a model wrote, so its case is not
    // dependable and the match is case-insensitive.
    [Fact]
    public void TheNoPeerPhrasingIsMatchedWhateverItsCase()
    {
        Assert.True(BridgeProtocol.LooksLikeAgentList("No peer sessions found."));
        Assert.True(BridgeProtocol.LooksLikeAgentList("NO PEERS"));
    }

    // The header, by contrast, is the tool's own output rather than prose, so it
    // is matched exactly — and a lowercase version is prose about the tool, not
    // the tool.
    [Fact]
    public void TheHeaderItselfIsCaseSensitive()
    {
        Assert.False(BridgeProtocol.LooksLikeAgentList("peer sessions (2):"));
    }

    // Still thinking, still typing, or answering something else entirely: none
    // of these end the wait.
    [Theory]
    [InlineData("")]
    [InlineData("Let me check that for you.")]
    [InlineData("I'll call the ListAgents tool now.")]
    [InlineData("Peer")]
    public void OutputThatIsNotAnAnswerDoesNotEndTheWait(string text)
    {
        Assert.False(BridgeProtocol.LooksLikeAgentList(text));
    }

    // ---- the send receipt ------------------------------------------------

    [Fact]
    public void ASendReceiptIsRecognisedByItsIdField()
    {
        Assert.True(BridgeProtocol.LooksLikeSendReceipt(
            """{"msg_id":"01SX9H","delivered":true}"""));
    }

    // Found anywhere, because the receipt arrives surrounded by whatever else
    // the session chose to say about it.
    [Fact]
    public void TheReceiptIsFoundInsideSurroundingProse()
    {
        Assert.True(BridgeProtocol.LooksLikeSendReceipt(
            "Sent it. The tool returned msg_id 01SX9H, so it is on its way."));
    }

    // Case-sensitive, because it is a field name in the tool's own output rather
    // than anything a person wrote — and a model saying "Msg_Id" is describing
    // the field, not returning it.
    [Fact]
    public void TheReceiptFieldNameIsCaseSensitive()
    {
        Assert.False(BridgeProtocol.LooksLikeSendReceipt("Msg_Id: 01SX9H"));
        Assert.False(BridgeProtocol.LooksLikeSendReceipt("MSG_ID"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sending that now.")]
    [InlineData("I will use the SendMessage tool.")]
    [InlineData("message id 01SX9H")]
    public void OutputWithoutAReceiptDoesNotEndTheWait(string text)
    {
        Assert.False(BridgeProtocol.LooksLikeSendReceipt(text));
    }

    // The two must not accept each other's answers, or a send would be reported
    // as complete the moment a peer list happened to scroll past.
    [Fact]
    public void NeitherPredicateAcceptsTheOthersAnswer()
    {
        Assert.False(BridgeProtocol.LooksLikeSendReceipt("Peer sessions (2):"));
        Assert.False(BridgeProtocol.LooksLikeAgentList("""{"msg_id":"01SX9H"}"""));
    }
}
