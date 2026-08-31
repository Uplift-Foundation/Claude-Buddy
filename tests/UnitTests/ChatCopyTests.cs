using Xunit;

namespace ClaudeBuddy.Tests;

// Which of two selections the copy gesture means. One case per outcome, plus
// the case the rule exists to settle: both selected at once.
public class ChatCopyTests
{
    [Fact]
    public void NothingSelectedAnywhereIsNotOursToClaim()
    {
        Assert.Equal(
            ChatCopy.Target.Nothing,
            ChatCopy.Decide(composerHasSelection: false, messageHasSelection: false));
    }

    [Fact]
    public void ASelectionInAMessageIsCopiedWhenTheComposerHasNone()
    {
        Assert.Equal(
            ChatCopy.Target.Message,
            ChatCopy.Decide(composerHasSelection: false, messageHasSelection: true));
    }

    [Fact]
    public void ASelectionInTheComposerIsItsOwnToCopy()
    {
        Assert.Equal(
            ChatCopy.Target.Composer,
            ChatCopy.Decide(composerHasSelection: true, messageHasSelection: false));
    }

    // The whole reason the rule is written down. A bubble keeps showing its
    // selection until something clears it, so "both at once" is not a corner
    // case — it is what the screen looks like the moment someone selects part
    // of a reply, then goes back to editing what they were typing. The
    // keystroke has to mean the thing they are working in.
    [Fact]
    public void TheComposerWinsWhenBothHoldASelection()
    {
        Assert.Equal(
            ChatCopy.Target.Composer,
            ChatCopy.Decide(composerHasSelection: true, messageHasSelection: true));
    }
}
