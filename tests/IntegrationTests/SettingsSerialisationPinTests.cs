using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-241's guard, for this assembly. tests/UnitTests/SettingsSerialisationPinTests
// has the story and the reasoning; it is copied here rather than shared because
// xUnit collections are per assembly — each suite runs in its own process, so
// only a class in *this* suite can race another class in it — and a guard can
// only check attributes by reflection on its own assembly.
//
// The rule: every test class here that writes the Claude Code profile list
// (Add/RemoveClaudeCodeProfileDir) or reads it through SourcesFor,
// ClaudeConfigRoots or TranscriptReader.LatestTranscriptForCwd is in the
// Settings collection. Four classes here write ".claude-work", ".claude-board"
// and the like for the length of a test; TranscriptDiscoveryTests read the
// list from outside any collection and was harmless only because its private
// temp home held nothing else. SessionMessengerKeyFileTests is not caught by
// it and does not need to be: SessionMessenger.Live takes its roots explicitly.
public class SettingsSerialisationPinTests
{
    private const string Settings = "Settings";

    private static readonly Regex Touches = new(
        @"\b(AddClaudeCodeProfileDir|RemoveClaudeCodeProfileDir|LatestTranscriptForCwd)\s*\(|\bSourcesFor\s*\(|\bClaudeConfigRoots\.",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclaration = new(
        @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find Orbweaver.csproj above " + AppContext.BaseDirectory);
    }

    private static string WithoutLineComments(string source) =>
        string.Join("\n", source.Split('\n').Select(line =>
        {
            var at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        }));

    private static IReadOnlyList<string> ClassesThatTouchTheProfileList(string source)
    {
        var code = WithoutLineComments(source);
        var declarations = ClassDeclaration.Matches(code).ToList();
        var found = new List<string>();

        for (var i = 0; i < declarations.Count; i++)
        {
            var start = declarations[i].Index;
            var end = i + 1 < declarations.Count ? declarations[i + 1].Index : code.Length;
            if (Touches.IsMatch(code[start..end])) found.Add(declarations[i].Groups[1].Value);
        }

        return found;
    }

    private static string? CollectionOf(Type type)
    {
        var outer = type;
        while (outer.DeclaringType is not null) outer = outer.DeclaringType;

        return outer.GetCustomAttributesData()
            .Where(a => a.AttributeType.Name == "CollectionAttribute")
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .FirstOrDefault();
    }

    [Fact]
    public void EveryClassThatTouchesTheProfileListIsInTheSettingsCollection()
    {
        var root = Path.Combine(FindRepoRoot(), "tests", "IntegrationTests");
        var types = typeof(SettingsSerialisationPinTests).Assembly.GetTypes();
        var checkedClasses = new List<string>();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            if (Path.GetFileName(file) == nameof(SettingsSerialisationPinTests) + ".cs") continue;

            foreach (var name in ClassesThatTouchTheProfileList(File.ReadAllText(file)))
            {
                var type = types.FirstOrDefault(t => t.Name == name);
                if (type is null) continue;
                checkedClasses.Add(name);
                if (CollectionOf(type) != Settings) offenders.Add($"{name} ({Path.GetFileName(file)})");
            }
        }

        // Not vacuous: the writers and the reader that prompted this are found.
        Assert.Contains(nameof(TranscriptReaderTests), checkedClasses);
        Assert.Contains(nameof(TranscriptDiscoveryTests), checkedClasses);

        Assert.True(offenders.Count == 0,
            "Outside [Collection(\"Settings\")] but touching the profile list: " + string.Join(", ", offenders));
    }
}
