using System.Runtime.CompilerServices;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-200 QA: the Speech level's environment variable is spelled in exactly
// one place, and both the app and the side-car engine compile that place.
//
// The engine is not referenced by any test project (it drags in 82MB of
// packages; see its csproj), so "the engine uses the shared constant" cannot
// be asserted on a type. It is asserted on the sources instead: the literal
// appears only in SpeechEngineContract.cs, the engine's Program.cs names the
// constant rather than a copy of it, and the app's csproj links the file in.
// A copy reintroduced on either side fails here, where drift would otherwise
// only show as an engine quietly speaking at full volume.
public class SpeechEngineContractTests
{
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);
        while (directory is not null && !File.Exists(Path.Combine(directory, "ClaudeBuddy.csproj")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new DirectoryNotFoundException("no ClaudeBuddy.csproj above " + thisFile);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void TheAppSpellsTheNameFromTheSharedFile() =>
        Assert.Equal(SpeechEngineContract.VolumeEnvVar, AudioVolume.SpeechVolumeEnvVar);

    [Fact]
    public void TheEngineSpellsTheNameFromTheSharedFile()
    {
        var program = Read("tools", "ClaudeBuddySpeech", "Program.cs");

        Assert.Contains("SpeechEngineContract.VolumeEnvVar", program);
        Assert.DoesNotContain("\"" + SpeechEngineContract.VolumeEnvVar + "\"", program);
    }

    [Fact]
    public void TheSharedFileLivesWithTheEngineAndIsLinkedIntoTheApp()
    {
        Assert.Contains("\"" + SpeechEngineContract.VolumeEnvVar + "\"",
            Read("tools", "ClaudeBuddySpeech", "SpeechEngineContract.cs"));
        Assert.Contains(@"tools\ClaudeBuddySpeech\SpeechEngineContract.cs", Read("ClaudeBuddy.csproj"));
    }

    // The contract stamp (CB-200 second review): the engine's csproj writes
    // StampFileName holding a number it reads out of SpeechEngineContract.cs
    // with a regex. Pinned here from both ends — the csproj names the same
    // file the app reads, and the csproj's regex, run over the real source,
    // finds the same number the app compiled in. A renamed constant or a
    // reworded declaration fails here rather than as an engine with an empty
    // stamp that every app reads as "ignores the level".
    [Fact]
    public void TheEngineBuildStampsTheContractVersionTheAppExpects()
    {
        var csproj = Read("tools", "ClaudeBuddySpeech", "ClaudeBuddySpeech.csproj");
        Assert.Contains("<SpeechEngineStampFile>" + SpeechEngineContract.StampFileName + "</SpeechEngineStampFile>", csproj);
        Assert.Contains("AfterTargets=\"Build\"", csproj);
        Assert.Contains("AfterTargets=\"Publish\"", csproj);

        var pattern = System.Text.RegularExpressions.Regex.Match(csproj, @"'(ContractVersion = \(\\d\+\);)'").Groups[1].Value;
        Assert.Equal(@"ContractVersion = (\d+);", pattern);

        var source = Read("tools", "ClaudeBuddySpeech", "SpeechEngineContract.cs");
        var stamped = System.Text.RegularExpressions.Regex.Match(source, pattern).Groups[1].Value;
        Assert.Equal(SpeechEngineContract.ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), stamped);
    }

    // And nowhere else in the app's own sources spells it out.
    [Fact]
    public void NoAppSourceCarriesItsOwnCopy()
    {
        var root = RepositoryRoot();
        var copies = Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => File.ReadAllText(f).Contains("\"" + SpeechEngineContract.VolumeEnvVar + "\""))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(copies);
    }
}
