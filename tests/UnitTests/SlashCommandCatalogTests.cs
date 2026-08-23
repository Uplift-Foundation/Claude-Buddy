using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the parts of SlashCommandCatalog that don't depend on the real
// ~/.claude or ~/.codex — the built-in floor, and everything driven by a
// project directory this can create and delete on its own. Personal-level
// discovery reuses the exact same CustomCommands/Skills helpers against a
// different root, so it isn't retested here; the only thing that differs is
// which literal path gets passed in.
public class SlashCommandCatalogTests : IDisposable
{
    private readonly string _cwd = Path.Combine(Path.GetTempPath(), "cb-slash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cwd)) Directory.Delete(_cwd, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_cwd, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void ClaudeCode_IncludesTheDocumentedBuiltins()
    {
        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, "");

        Assert.Contains(found, c => c.Name == "/clear");
        Assert.Contains(found, c => c.Name == "/rename");
        Assert.Contains(found, c => c.Name == "/color");
    }

    [Fact]
    public void Codex_UsesItsOwnBuiltinsRatherThanClaudeCodes()
    {
        var found = SlashCommandCatalog.For(SessionSource.Codex, "");

        // Codex has no /clear-with-history-clearing distinction from Claude
        // Code worth asserting on, but /rewind and /agents are Claude
        // Code-only, and Codex's /archive has no Claude Code equivalent —
        // proof the two lists are actually different rather than one
        // falling through to the other.
        Assert.DoesNotContain(found, c => c.Name == "/rewind");
        Assert.DoesNotContain(found, c => c.Name == "/agents");
        Assert.Contains(found, c => c.Name == "/archive");
    }

    [Fact]
    public void ProjectCommand_IsNamedForItsFileWithoutExtension()
    {
        Write(".claude/commands/deploy.md", "Deploy the app");

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, _cwd);

        Assert.Contains(found, c => c.Name == "/deploy");
    }

    [Fact]
    public void NestedProjectCommand_IsNamespacedWithAColon()
    {
        Write(".claude/commands/frontend/component.md", "Scaffold a component");

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, _cwd);

        Assert.Contains(found, c => c.Name == "/frontend:component");
    }

    [Fact]
    public void ProjectCommand_ReadsDescriptionFromFrontmatter()
    {
        Write(".claude/commands/deploy.md", "---\ndescription: Deploy the app to production\n---\nBody text.");

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, _cwd);

        Assert.Equal("Deploy the app to production", found.Single(c => c.Name == "/deploy").Description);
    }

    [Fact]
    public void ProjectCommand_FallsBackToFirstLineWithoutFrontmatter()
    {
        Write(".claude/commands/deploy.md", "\n  Deploy the app to production  \nMore body text.");

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, _cwd);

        Assert.Equal("Deploy the app to production", found.Single(c => c.Name == "/deploy").Description);
    }

    [Fact]
    public void ProjectSkill_IsNamedForItsDirectory()
    {
        Write(".claude/skills/summarize-changes/SKILL.md",
            "---\ndescription: Summarizes uncommitted changes\n---\nBody.");

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, _cwd);

        var skill = found.Single(c => c.Name == "/summarize-changes");
        Assert.Equal("Summarizes uncommitted changes", skill.Description);
    }

    [Fact]
    public void ProjectSkills_AreFoundFromAnAncestorDirectory()
    {
        // Claude Code itself loads .claude/skills/ from the directory it
        // started in and every parent up to the repository root — a session
        // opened two levels below the root should still see it.
        Write(".claude/skills/deploy/SKILL.md", "Deploy the app");

        var subdirectory = Path.Combine(_cwd, "packages", "frontend");
        Directory.CreateDirectory(subdirectory);

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, subdirectory);

        Assert.Contains(found, c => c.Name == "/deploy");
    }

    [Fact]
    public void SkillTakesPrecedenceOverACommandOfTheSameName()
    {
        Write(".claude/commands/deploy.md", "---\ndescription: The old command\n---\n");
        Write(".claude/skills/deploy/SKILL.md", "---\ndescription: The new skill\n---\n");

        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, _cwd);

        Assert.Equal("The new skill", found.Single(c => c.Name == "/deploy").Description);
    }

    [Fact]
    public void MissingProjectDirectories_YieldOnlyBuiltins()
    {
        var found = SlashCommandCatalog.For(SessionSource.ClaudeCode, Path.Combine(_cwd, "does-not-exist"));

        Assert.Equal(found.Count, found.Select(c => c.Name).Distinct().Count());
        Assert.Contains(found, c => c.Name == "/help");
    }
}
