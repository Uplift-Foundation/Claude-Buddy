using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// Windows PowerShell 5.1 reads a .ps1 with no BOM as the ANSI codepage, not
// UTF-8. An em dash (E2 80 94) then decodes to three characters, one of which
// is a curly quote in cp1252 -- so a dash inside a double-quoted string
// silently ends the string and the script fails to parse ("The string is
// missing the terminator"). pwsh 7 reads UTF-8 and never shows it, which is
// how tools/build-windows-installer.ps1 stayed broken (CB-204, CB-224): CI and
// every developer run pwsh. The repo's fix is pure ASCII rather than a BOM,
// since ASCII needs no encoding agreement between the file and either shell.
//
// This pins it for every .ps1 in the tree, not just the one that was reported.
public class PowerShellScriptEncodingTests
{
    // Directories that hold copies, build output or other checkouts rather than
    // sources that ship. .claude holds agent worktrees, each a full checkout.
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".claude", "bin", "obj", "node_modules", "dist", "TestResults" };

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    // Returns one "path:line: ..." message per offending line, or none when the
    // file is ASCII-only or carries a UTF-8 BOM. Pure, so the failure path can
    // be tested without a bad file on disk.
    internal static IReadOnlyList<string> FindUnsafeLines(string displayPath, byte[] content)
    {
        if (content.AsSpan().StartsWith(Utf8Bom)) return Array.Empty<string>();

        var problems = new List<string>();
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i <= content.Length; i++)
        {
            if (i < content.Length && content[i] != (byte)'\n') continue;
            var slice = content.AsSpan(lineStart, i - lineStart);
            var bad = slice.IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
            if (bad >= 0)
            {
                var text = Encoding.UTF8.GetString(slice).TrimEnd('\r').Trim();
                problems.Add($"{displayPath}:{line}: non-ASCII byte 0x{slice[bad]:X2} in a .ps1 with no UTF-8 BOM " +
                             $"(Windows PowerShell 5.1 will misread it). Use plain ASCII (e.g. '--' for an em dash). Line: {text}");
            }
            line++;
            lineStart = i + 1;
        }
        return problems;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClaudeBuddyHook.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find ClaudeBuddyHook.ps1 by walking up from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> ScriptsUnder(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.ps1"))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(root))
        {
            if (SkippedDirs.Contains(Path.GetFileName(sub))) continue;
            foreach (var file in ScriptsUnder(sub))
                yield return file;
        }
    }

    [Fact]
    public void EveryShippedPs1IsAsciiOrHasABom()
    {
        var root = FindRepoRoot();
        var scripts = ScriptsUnder(root).ToList();
        Assert.Contains(scripts, s => Path.GetFileName(s) == "build-windows-installer.ps1");

        var problems = scripts
            .SelectMany(s => FindUnsafeLines(Path.GetRelativePath(root, s), File.ReadAllBytes(s)))
            .ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void FlagsNonAsciiWithoutBom_NamingFileAndLine()
    {
        var bytes = Encoding.UTF8.GetBytes("# fine\r\nWrite-Host \"a — b\"\r\n");
        var problems = FindUnsafeLines("tools/bad.ps1", bytes);

        var single = Assert.Single(problems);
        Assert.StartsWith("tools/bad.ps1:2:", single);
        Assert.Contains("0xE2", single);
    }

    [Fact]
    public void ReportsEveryOffendingLine()
    {
        var bytes = Encoding.UTF8.GetBytes("a —\nplain\nb …");
        var problems = FindUnsafeLines("x.ps1", bytes);

        Assert.Equal(2, problems.Count);
        Assert.StartsWith("x.ps1:1:", problems[0]);
        Assert.StartsWith("x.ps1:3:", problems[1]);
    }

    [Fact]
    public void AcceptsNonAsciiWhenAUtf8BomIsPresent()
    {
        var bytes = Utf8Bom.Concat(Encoding.UTF8.GetBytes("Write-Host \"a — b\"")).ToArray();
        Assert.Empty(FindUnsafeLines("tools/ok.ps1", bytes));
    }

    [Fact]
    public void AcceptsPureAscii()
    {
        Assert.Empty(FindUnsafeLines("tools/ok.ps1", Encoding.ASCII.GetBytes("Write-Host 'hi'\n")));
    }
}
