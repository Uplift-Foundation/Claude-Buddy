using System.IO;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// ClaudeConfigRoots.All — every Claude Code account on this machine.
//
// In the Settings collection because it reads ClaudeCodeProfileDirs, which is
// one process-wide static that nine other classes in this assembly also touch;
// SettingsCollection.cs has the story of the once-in-five failure that bought
// that rule.
[Collection("Settings")]
public class ClaudeConfigRootsTests
{
    private const string Home = "/home/w";

    [Fact]
    public void TheDefaultAccountIsAlwaysFirst()
    {
        // Whatever else is configured, ~/.claude is the account the app itself
        // runs out of, and every caller here treats the first answer as the
        // authoritative one on a collision.
        var roots = ClaudeConfigRoots.All(Home);

        Assert.Equal(Path.Combine(Home, ".claude"), roots[0]);
    }

    [Fact]
    public void AConfiguredAccountIsIncluded()
    {
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        try
        {
            var roots = ClaudeConfigRoots.All(Home);

            Assert.Contains(Path.Combine(Home, ".claude-board"), roots);
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-board");
        }
    }

    // A blank entry is what a half-finished settings edit leaves behind.
    // Path.Combine would answer $HOME for it, which is not a config root but
    // *is* a directory that exists — so it would be asked and would fail,
    // rather than being skipped, and under the merge rule one failed account
    // poisons the whole listing. A stray space in the settings file would have
    // taken every background orb off the screen.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankEntryIsSkipped(string blank)
    {
        OrbweaverSettings.AddClaudeCodeProfileDir(blank);
        try
        {
            var roots = ClaudeConfigRoots.All(Home);

            Assert.Single(roots);
            Assert.DoesNotContain(Home, roots.Skip(1));
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(blank);
        }
    }

    [Fact]
    public void SurroundingSpaceIsTrimmedRatherThanTakenLiterally()
    {
        OrbweaverSettings.AddClaudeCodeProfileDir("  .claude-board  ");
        try
        {
            var roots = ClaudeConfigRoots.All(Home);

            Assert.Contains(Path.Combine(Home, ".claude-board"), roots);
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir("  .claude-board  ");
        }
    }

    // Listing the default account alongside itself is not an error worth
    // refusing, but it must not double the work: every caller either launches a
    // subprocess per root or stats a file per root, and the second answer can
    // only repeat the first.
    [Fact]
    public void TheDefaultAccountIsNotAskedTwice()
    {
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude");
        try
        {
            var roots = ClaudeConfigRoots.All(Home);

            Assert.Single(roots);
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude");
        }
    }

    [Fact]
    public void TwoAccountsBothAppearInOrder()
    {
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-work");
        try
        {
            var roots = ClaudeConfigRoots.All(Home);

            Assert.Equal(new[]
            {
                Path.Combine(Home, ".claude"),
                Path.Combine(Home, ".claude-board"),
                Path.Combine(Home, ".claude-work"),
            }, roots);
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-board");
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-work");
        }
    }

    // RootOfTranscript: which account a transcript belongs to, read off its path
    // (CB-266). Pure, so it needs no settings and the Settings collection is only
    // incidental to these.
    [Theory]
    [InlineData("/home/w/.claude/projects/-home-w-code/abc.jsonl", "/home/w/.claude")]
    [InlineData("/home/w/.claude-board/projects/-x/abc.jsonl", "/home/w/.claude-board")]
    [InlineData("C:\\Users\\w\\.claude\\projects\\C--code\\abc.jsonl", "C:\\Users\\w\\.claude")]
    // A slug that is itself called projects does not shift the answer.
    [InlineData("/home/w/.claude/projects/projects/abc.jsonl", "/home/w/.claude")]
    [InlineData("/projects/x/abc.jsonl", "/")]
    public void TheRootIsTheParentOfTheProjectsSegment(string path, string expected) =>
        Assert.Equal(expected, ClaudeConfigRoots.RootOfTranscript(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/home/w/.claude/sessions/abc.jsonl")]
    // "projects" as the file name, or as the first segment with nothing before it.
    [InlineData("/home/w/.claude/projects")]
    [InlineData("projects/x/abc.jsonl")]
    public void APathWithNoUsableProjectsSegmentIsUnknown(string? path) =>
        Assert.Null(ClaudeConfigRoots.RootOfTranscript(path));
}
