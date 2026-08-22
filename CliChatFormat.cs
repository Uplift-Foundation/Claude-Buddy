namespace ClaudeBuddy
{
    // What LocalCliChatSession needs to know about the CLI whose transcript it
    // is tailing, and nothing more.
    //
    // The list is short on purpose, and the shortness is the finding. Two CLIs
    // that both write a JSONL transcript and both take typing through a tmux
    // pane turned out to differ in exactly two things a chat panel can see: the
    // shape of a transcript line, and which pair of settings governs reading and
    // replying. Everything that looked like it might differ — the dialog, the
    // interrupt key, how text is submitted — was measured against a real Codex
    // session and turned out to be the same. Each of those is recorded below
    // rather than left as an absence, because "we checked and it is the same" and
    // "nobody looked" are indistinguishable in code.
    internal sealed record CliChatFormat(
        Func<IEnumerable<string>, List<ChatTranscript.Row>> Map,
        Func<bool> ChatEnabled,
        Func<bool> ReplyEnabled)
    {
        public static readonly CliChatFormat ClaudeCode = new(
            ChatTranscript.Map,
            () => ClaudeBuddySettings.ClaudeCodeChatEnabled,
            () => ClaudeBuddySettings.ClaudeCodeReplyEnabled);

        public static readonly CliChatFormat Codex = new(
            CodexTranscript.Map,
            () => ClaudeBuddySettings.CodexChatEnabled,
            () => ClaudeBuddySettings.CodexReplyEnabled);

        public static CliChatFormat For(SessionSource source) =>
            source == SessionSource.Codex ? Codex : ClaudeCode;

        // --- what was expected to differ, and does not -----------------------
        //
        // These are here as comments rather than as members because a member
        // nobody varies is a worse lie than a note. All three were measured
        // against a real Codex 0.148 session in a tmux pane.
        //
        // **The approval dialog parses with ChatTranscript.ParseDialog,
        // unchanged.** This was expected to need its own parser — the binary's
        // strings advertise "Allow" / "Allow for this session" wording that
        // would have broken the contiguous-1..n rule. The TUI does not use it.
        // A real escalation prompt reads:
        //
        //     Would you like to run the following command?
        //     Reason: …
        //     $ touch $HOME/cb-approval-probe
        //   › 1. Yes, proceed (y)
        //     2. Yes, and don't ask again for commands that start with … (p)
        //     3. No, and tell Codex what to do differently (esc)
        //     Press enter to confirm or esc to cancel
        //
        // which the existing parser reads correctly, keys and labels both. The
        // title it picks is the command rather than the question, which is the
        // more useful of the two anyway.
        //
        // **A digit answers immediately.** Sending "3" dismissed the prompt and
        // acted on it; the "Press enter to confirm" line describes the arrow-key
        // route, not the numeric one. Same as Claude Code, so AnswerAsync needs
        // no per-CLI key.
        //
        // **Escape interrupts.** Codex's own working indicator says so — "•
        // Working (0s • esc to interrupt)". An earlier note here warned that Esc
        // might mean backtrack and that interrupting raised a dialog offering to
        // exit Codex; that came from reading strings out of the binary and is
        // not what the TUI does.
        //
        // Submitting is likewise identical: the paste-buffer-then-Enter sequence
        // TerminalFocuser already uses was replayed against a live Codex pane and
        // submitted the message exactly as it does for Claude Code.
    }
}
