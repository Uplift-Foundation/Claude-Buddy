using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

// CB-256: the speech engine's name is spelled in five places, and four of them
// cannot read a C# constant.
//
// Brand.SpeechEngineName builds the URL the Settings toggle downloads. The
// engine csproj's <AssemblyName> decides what the executable is called; the
// two build scripts name the zip; release.yml's two upload globs decide which
// zips reach the release at all. If any one of them disagrees, nothing fails
// at build time and nothing fails in CI — CI never runs a release — and the
// first anybody hears of it is a user turning neural voice on and getting a
// 404, or an engine that downloads and is then reported as having no
// executable. That is exactly the shape of the phase-3 rename, which moved
// all five at once.
//
// So this reads the four files and checks each against the constant. A
// source scan rather than a behaviour test for the same reason
// LogDirSingleReadSiteTests is one: what it protects is that files nobody
// compiles agree with one that is compiled, and only reading them says so.
public class SpeechEngineNameConsistencyTests
{
    private static readonly string Name = Brand.SpeechEngineName;

    [Fact]
    public void TheEngineProjectsAssemblyNameIsTheBrandName()
    {
        var csproj = Read("tools", Name, Name + ".csproj");

        var assemblyName = Regex.Match(csproj, "<AssemblyName>([^<]*)</AssemblyName>");
        Assert.True(assemblyName.Success, "the engine csproj has no <AssemblyName>");
        Assert.Equal(Name, assemblyName.Groups[1].Value);
    }

    // Every upload-artifact step in release.yml uploads this zip — today the
    // macOS rid matrix and the Windows job. Counted, so a third upload step
    // that forgets the engine fails here rather than shipping a release with
    // a rid missing, and a step that drops the glob cannot hide behind the
    // other one still carrying it.
    [Fact]
    public void BothReleaseUploadStepsCarryTheEngineZip()
    {
        var workflow = Read(".github", "workflows", "release.yml");

        var uploads = Regex.Matches(workflow,
                @"uses: actions/upload-artifact@\S+\s+with:(?<body>.*?)if-no-files-found",
                RegexOptions.Singleline)
            .Select(m => m.Groups["body"].Value)
            .ToList();

        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, body => Assert.Contains($"dist/{Name}-*.zip", body));
    }

    // And no upload glob under any other stem, which is what a half-done
    // rename leaves behind: the right glob added, the old one still there.
    [Fact]
    public void NoReleaseGlobNamesAnyOtherEngineZip()
    {
        var workflow = Read(".github", "workflows", "release.yml");

        var stems = Regex.Matches(workflow, @"dist/([A-Za-z]+)-\*\.zip")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Equal(new[] { Name }, stems);
    }

    [Fact]
    public void TheWindowsBuildScriptNamesTheZipAfterTheEngine()
    {
        var script = Read("tools", "build-speech-engine.ps1");

        Assert.Contains($"\"{Name}-$version-$Rid.zip\"", script);
        Assert.Contains($"'{Name}\\{Name}.csproj'", script);
    }

    // The macOS script also signs the executable by name, and the name it
    // signs has to be the one the csproj produces, or a signed release build
    // fails on a missing file after notarisation credentials are spent.
    [Fact]
    public void TheMacBuildScriptNamesTheZipAndTheExecutableAfterTheEngine()
    {
        var script = Read("tools", "build-speech-engine.sh");

        Assert.Contains($"\"$DIST/{Name}-$VERSION-$RID.zip\"", script);
        Assert.Contains($"tools/{Name}/{Name}.csproj", script);
        Assert.Contains($"--sign \"$SIGN_IDENTITY\" \"$PUBLISH/{Name}\"", script);
    }

    // Negative control: the scan above is not vacuous. The legacy name, which
    // every one of these files carried before CB-256, is now in none of them —
    // so the assertions are reading the real files, not passing on anything.
    [Fact]
    public void NoneOfTheFiveSitesStillNamesTheLegacyEngine()
    {
        var legacy = Brand.Legacy.SpeechEngineName;

        Assert.DoesNotContain(legacy, Read(".github", "workflows", "release.yml"));
        Assert.DoesNotContain(legacy, Read("tools", "build-speech-engine.ps1"));
        Assert.DoesNotContain(legacy, Read("tools", "build-speech-engine.sh"));
        Assert.DoesNotContain(legacy, Read("tools", Name, Name + ".csproj"));
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

        // Loud rather than skipped: a missing file is the rename having moved
        // something this guard still looks for, and a guard that passes when
        // it cannot see what it guards reads exactly like a clean run.
        Assert.True(File.Exists(path), $"{path} does not exist — has the engine moved?");
        return File.ReadAllText(path);
    }

    // From this file's compile-time path, as LogDirSingleReadSiteTests does:
    // the test host's working directory varies by rid and configuration.
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);

        while (directory is not null && !File.Exists(Path.Combine(directory, "Orbweaver.csproj")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new DirectoryNotFoundException("no Orbweaver.csproj above " + thisFile);
    }
}
