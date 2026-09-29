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
