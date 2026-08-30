namespace ClaudeBuddy
{
    // Who the copy gesture belongs to when two things in the panel can both
    // hold a selection.
    //
    // Pure, like ChatMarkdown and OrbGlyph, and for the same reason: the rule
    // is one line of consequence and three lines of code, but getting it wrong
    // is invisible until someone loses a sentence they thought they had copied.
    //
    // The rule exists because the composer is the only focusable control in the
    // panel (see ChatPanel.axaml's comment on Focusable), so it is *always* the
    // keyboard's target — including when the thing the user selected is a
    // message bubble they dragged across with the pointer. Avalonia's TextBox
    // marks the copy gesture handled whether or not it had anything to copy, so
    // the panel has to claim the keystroke on the tunnel route and decide for
    // itself. That decision is this.
    public static class ChatCopy
    {
        public enum Target
        {
            // Nothing is selected anywhere; the gesture is not ours to claim
            // and is left for the TextBox to no-op on as it always has.
            Nothing,

            // The composer has its own selection, so the gesture means what it
            // has always meant. A person who has just selected part of their
            // own half-typed message and pressed copy did not mean the reply
            // above it, even if a bubble is still showing a selection from
            // earlier.
            Composer,

            // Nothing is selected in the composer and a bubble is holding a
            // selection, so the gesture means the transcript.
            Message
        }

        public static Target Decide(bool composerHasSelection, bool messageHasSelection)
        {
            if (composerHasSelection) return Target.Composer;
            if (messageHasSelection) return Target.Message;

            return Target.Nothing;
        }
    }
}
