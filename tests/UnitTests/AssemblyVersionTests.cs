using System.Reflection;
using Xunit;

namespace ClaudeBuddy.Tests;

/// <summary>
/// CB-205: the shipped exe reported FileVersion 0.4.1.0 next to a ProductVersion of
/// 0.5.x-beta, because the csproj hard-coded AssemblyVersion/FileVersion and nobody
/// bumped them. They are now derived from &lt;Version&gt;; this pins that, so a future
/// hard-code fails on both rids instead of surviving five releases.
/// </summary>
public class AssemblyVersionTests
{
    private static readonly Assembly App = typeof(SessionManager).Assembly;

    private static string Informational() =>
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    // "0.5.9-beta+abc123" -> "0.5.9": the numeric part of the informational version.
    private static string NumericPart() => Informational().Split('+')[0].Split('-')[0];

    [Fact]
    public void FileVersion_is_the_numeric_part_of_Version()
    {
        var file = App.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        Assert.Equal(NumericPart() + ".0", file);
    }

    [Fact]
    public void AssemblyVersion_is_the_numeric_part_of_Version()
    {
        Assert.Equal(NumericPart() + ".0", App.GetName().Version!.ToString());
    }

    [Fact]
    public void Informational_version_keeps_the_prerelease_label()
    {
        // Stripping the suffix for the numeric fields must not strip it from the
        // label users see.
        Assert.StartsWith(NumericPart(), Informational());
        Assert.Contains("-", Informational().Split('+')[0]);
    }
}
