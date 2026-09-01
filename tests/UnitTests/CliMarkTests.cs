using Xunit;

namespace ClaudeBuddy.UnitTests;

public class CliMarkTests
{
    [Fact]
    public void ClaudeCodexAndGrokEachGetADistinctMark()
    {
        var claude = CliMark.For(SessionSource.ClaudeCode);
        var codex = CliMark.For(SessionSource.Codex);
        var grok = CliMark.For(SessionSource.Grok);

        Assert.NotNull(claude);
        Assert.NotNull(codex);
        Assert.NotNull(grok);

        Assert.Equal("claude", claude!.Value.Name);
        Assert.Equal("codex", codex!.Value.Name);
        Assert.Equal("grok", grok!.Value.Name);

        Assert.NotEqual(claude.Value.FillHex, codex.Value.FillHex);
        Assert.NotEqual(claude.Value.FillHex, grok.Value.FillHex);
        Assert.NotEqual(codex.Value.FillHex, grok.Value.FillHex);

        Assert.NotEqual(claude.Value.GlyphPath, codex.Value.GlyphPath);
        Assert.NotEqual(claude.Value.GlyphPath, grok.Value.GlyphPath);
        Assert.NotEqual(codex.Value.GlyphPath, grok.Value.GlyphPath);
    }

    [Fact]
    public void OpenClawAndRemoteCarryNoMark()
    {
        Assert.Null(CliMark.For(SessionSource.OpenClaw));
        Assert.Null(CliMark.For(SessionSource.RemoteControl));
    }

    [Fact]
    public void AccountSourcesMatchTheSessionMarks()
    {
        Assert.Equal(CliMark.For(SessionSource.ClaudeCode)!.Value, CliMark.For(AccountUsageSource.ClaudeCode));
        Assert.Equal(CliMark.For(SessionSource.Codex)!.Value, CliMark.For(AccountUsageSource.Codex));
        Assert.Equal(CliMark.For(SessionSource.Grok)!.Value, CliMark.For(AccountUsageSource.Grok));
    }

    [Fact]
    public void TheDiscIsBiggerThanTheKindBadge()
    {
        Assert.True(CliMark.Size > 16);
        Assert.True(CliMark.GlyphSize < CliMark.Size);
    }
}
