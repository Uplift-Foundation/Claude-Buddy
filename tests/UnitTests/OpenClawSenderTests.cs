using Xunit;

namespace ClaudeBuddy.UnitTests;

// Who said a user-role message the gateway handed us.
//
// Worth its own suite for the reason ChatSpeaker and OrbGlyph have one: the
// answer is drawn as an assertion about a person — your colour, your side of
// the panel — and the only other way to check it is to open a room and look at
// bubbles. It also reads an undocumented block on a format nobody here
// controls, so the cases below are the record of what was actually measured.
//
// Every id, name and sentence here is invented. The shapes are real; the
// contents are not, and this repository is public where the gateway is not.
public class OpenClawSenderTests
{
    private const string Prefix = OpenClawSender.MirrorPrefix;

    // --- the operator, three ways ------------------------------------------

    // Typed in Discord. The gateway states this outright, which makes it the
    // only one of the three that is measured rather than inferred.
    [Fact]
    public void TheGatewaySayingTheOwnerSentItIsEnough()
    {
        var sender = OpenClawSender.Classify(true, "quillfeather", null, "what is the status");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
        Assert.Equal("what is the status", sender.Text);
    }

    // Typed here. No sender fields at all — the gateway does not attribute a
    // message it accepted from this client — and a top-level idempotency key
    // whose ":user" suffix is the whole signal.
    [Fact]
    public void AMessageThisClientSentIsRecognisedByItsIdempotencyKey()
    {
        var sender = OpenClawSender.Classify(
            null, null, "8f2a41c0-1d3e-4b77-9a05-6c1e2f0b8d34:user", "any thoughts?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // The same block on __openclaw rather than on the message, since both carry
    // it and the caller falls back from one to the other.
    [Fact]
    public void TheSameKeyIsRecognisedWhereverItWasRead()
    {
        var sender = OpenClawSender.Classify(
            null, null, "0b9d1a55-77c4-4f10-bb2e-3d8a90e6c412:user", "still there?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // The agent's own reply carries an idempotency key too, and it must not
    // match. It is assistant-role anyway, so this is belt and braces — but the
    // suffix test is a string test, and "cli-assistant:<guid>" is exactly the
    // shape that would be caught by a looser one.
    [Fact]
    public void AnAgentsOwnReplyKeyIsNotMistakenForYours()
    {
        var sender = OpenClawSender.Classify(
            null, null, "cli-assistant:5c7e0f81-2b46-4d93-8a1f-70e6bb43d205", "done");

        Assert.NotEqual(OpenClawSender.SenderKind.Mine, sender.Kind);
    }

    // --- our own mirror, coming back ---------------------------------------

    // The prefix beats the name, and this is the case that made the ordering
    // matter. A message typed here is posted to the channel by whichever bot
    // account carried it, so the copies that reach the *other* agents arrive
    // attributed to that bot — trusting senderName would draw your own words as
    // an agent's.
    [Fact]
    public void OurOwnMirrorIsYoursEvenThoughItArrivesUnderTheCarriersName()
    {
        var sender = OpenClawSender.Classify(
            false, "Quillbot", null, Prefix + "everyone still awake?");

        Assert.Equal(OpenClawSender.SenderKind.Mine, sender.Kind);
        Assert.Null(sender.Name);
    }

    // And the prefix comes off. It is addressing, not content: leaving it on
    // would show you a sentence you did not type, and — because the carrier's
    // own transcript holds the copy without it — would stop the two matching,
    // which is how a successful room send came to be drawn twice.
    [Fact]
    public void TheMirrorPrefixIsStrippedFromWhatIsShown()
    {
        var sender = OpenClawSender.Classify(
            false, "Quillbot", null, Prefix + "everyone still awake?");

        Assert.Equal("everyone still awake?", sender.Text);
    }

    // Only at the front. Somebody writing the words in the middle of a sentence
    // is talking *about* this app, not through it, and their message is theirs.
    [Fact]
    public void TheWordsInTheMiddleOfASentenceAreNotAMirror()
    {
        var sender = OpenClawSender.Classify(
            false, "Thistle", null, "I sent that one via Claude Buddy earlier");

        Assert.Equal(OpenClawSender.SenderKind.Named, sender.Kind);
        Assert.Equal("Thistle", sender.Name);
        Assert.Equal("I sent that one via Claude Buddy earlier", sender.Text);
    }

    // --- somebody else ------------------------------------------------------

    // An agent relayed through the channel. Richer than expected — the relay
    // carries the bot's Discord display name — so a relayed agent turn is named
    // rather than anonymous.
    [Fact]
    public void ARelayedMessageIsAttributedToWhoeverTheGatewayNames()
    {
        var sender = OpenClawSender.Classify(false, "Thistle", null, "nodes are loaded");

        Assert.Equal(OpenClawSender.SenderKind.Named, sender.Kind);
        Assert.Equal("Thistle", sender.Name);
    }

    // The name is trimmed but not otherwise touched — it goes on a chip beside
    // the bubble, and leading space would show as a gap inside it.
    [Fact]
    public void TheNameIsTrimmed()
    {
        var sender = OpenClawSender.Classify(false, "  Thistle  ", null, "nodes are loaded");

        Assert.Equal("Thistle", sender.Name);
    }

    // A name that is only whitespace says nothing, so it is not a name.
    [Fact]
    public void AnEmptyNameIsNotANameAtAll()
    {
        var sender = OpenClawSender.Classify(false, "   ", null, "nodes are loaded");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
    }

    // --- nothing said -------------------------------------------------------

    // The safety net, and the reason the whole rule is ordered the way it is:
    // with no metadata at all the answer is exactly what this app did before any
    // of it existed — an anonymous turn, drawn as the room's own voice. A
    // gateway that stops sending `__openclaw` degrades to the old transcript
    // rather than to a wrong one.
    [Fact]
    public void WithNoMetadataAtAllNobodyIsNamedAndNothingIsClaimed()
    {
        var sender = OpenClawSender.Classify(null, null, null, "morning");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
        Assert.Null(sender.Name);
        Assert.Equal("morning", sender.Text);
    }

    // senderIsOwner false with no name is the same answer. The gateway has said
    // it wasn't you and has not said who, which is less than it usually offers
    // and still not grounds to guess.
    [Fact]
    public void NotYoursAndUnnamedIsStillUnknown()
    {
        var sender = OpenClawSender.Classify(false, null, null, "morning");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
    }

    // An inter-session message arrives here after Readable has already stripped
    // its machine header and identified the agent behind it. It carries no
    // sender fields, so it falls through to Unknown — which is what leaves
    // Readable's better answer in place at the call site.
    [Fact]
    public void AnInterSessionMessageFallsThroughAndLeavesReadablesAnswerAlone()
    {
        var sender = OpenClawSender.Classify(null, null, null, "can you take the build?");

        Assert.Equal(OpenClawSender.SenderKind.Unknown, sender.Kind);
    }
}
