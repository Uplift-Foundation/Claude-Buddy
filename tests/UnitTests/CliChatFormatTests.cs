using Xunit;

namespace ClaudeBuddy.Tests;

// Covers CliChatFormat.cs's For() dispatch. The record's whole point is
// documented as "the list is short on purpose" — only Map (which parser
// reads the transcript) and the two settings-enabled checks actually differ
// between CLIs, so this only needs to prove the dispatch wires the right
// static Map method to the right source.
public class CliChatFormatTests
{
    [Fact]
    public void For_Codex_UsesCodexTranscriptMap()
    {
        var format = CliChatFormat.For(SessionSource.Codex);

        // Both sides are method-group conversions of the same static method,
        // so Delegate equality (same target + same MethodInfo) is the right
        // check — two separately-created delegate wrappers over
        // CodexTranscript.Map are never the same object, but they are equal.
        Assert.Equal((System.Delegate)(System.Func<System.Collections.Generic.IEnumerable<string>,
            System.Collections.Generic.List<ChatTranscript.Row>>)CodexTranscript.Map, format.Map);
    }

    [Fact]
    public void For_ClaudeCode_UsesChatTranscriptMap()
    {
        var format = CliChatFormat.For(SessionSource.ClaudeCode);

        Assert.Equal((System.Delegate)(System.Func<System.Collections.Generic.IEnumerable<string>,
            System.Collections.Generic.List<ChatTranscript.Row>>)ChatTranscript.Map, format.Map);
    }

    [Fact]
    public void For_AnythingOtherThanCodexFallsBackToClaudeCode()
    {
        // "source == SessionSource.Codex ? Codex : ClaudeCode" — OpenClaw (a
        // gateway conversation) falls through to the ClaudeCode shape too.
        Assert.Same(CliChatFormat.ClaudeCode, CliChatFormat.For(SessionSource.OpenClaw));
    }

    [Fact]
    public void For_IsBackedByTheSameCachedRecordInstanceEveryCall()
    {
        Assert.Same(CliChatFormat.Codex, CliChatFormat.For(SessionSource.Codex));
        Assert.Same(CliChatFormat.ClaudeCode, CliChatFormat.For(SessionSource.ClaudeCode));
    }
}
