using Xunit;

namespace ClaudeBuddy.Tests;

// What sounds exist, and turning a setting string back into a path
// ChimePlayer can open — over a scratch directory of this test's own, never
// the real /System/Library/Sounds or C:\Windows\Media, so what's actually
// installed on the machine running the suite can never change the answer.
public class SystemSoundCatalogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb-soundcatalog-" + Guid.NewGuid());

    public SystemSoundCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    // --- List ---

    [Fact]
    public void ListOfAMissingDirectoryIsEmptyRatherThanThrowing()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        Assert.Empty(SystemSoundCatalog.List(missing, new[] { ".aiff" }));
    }

    [Fact]
    public void ListReturnsNamesWithoutTheirExtension()
    {
        Touch("Glass.aiff");
        Touch("Ping.aiff");

        var names = SystemSoundCatalog.List(_dir, new[] { ".aiff" });

        Assert.Equal(new[] { "Glass", "Ping" }, names);
    }

    [Fact]
    public void ListIgnoresFilesWithAnUnlistedExtension()
    {
        Touch("Glass.aiff");
        Touch("readme.txt");
        Touch("Submarine.wav");

        var names = SystemSoundCatalog.List(_dir, new[] { ".aiff" });

        Assert.Equal(new[] { "Glass" }, names);
    }

    [Fact]
    public void ListAcceptsAnyOfSeveralExtensions()
    {
        Touch("Glass.aiff");
        Touch("Chime.wav");

        var names = SystemSoundCatalog.List(_dir, new[] { ".aiff", ".wav" });

        Assert.Equal(new[] { "Chime", "Glass" }, names); // sorted
    }

    [Fact]
    public void ListMatchesExtensionsCaseInsensitively()
    {
        Touch("Glass.AIFF");

        Assert.Equal(new[] { "Glass" }, SystemSoundCatalog.List(_dir, new[] { ".aiff" }));
    }

    [Fact]
    public void ListIsSortedOrdinalIgnoringCase()
    {
        Touch("submarine.aiff");
        Touch("Ping.aiff");
        Touch("glass.aiff");

        var names = SystemSoundCatalog.List(_dir, new[] { ".aiff" });

        Assert.Equal(new[] { "glass", "Ping", "submarine" }, names);
    }

    // --- Resolve ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveOfAnAbsentSettingIsNull(string? setting)
    {
        Assert.Null(SystemSoundCatalog.Resolve(setting, _dir, new[] { ".aiff" }));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("Off")]
    [InlineData("OFF")]
    public void ResolveOfOffIsNullRegardlessOfCase(string setting)
    {
        Assert.Null(SystemSoundCatalog.Resolve(setting, _dir, new[] { ".aiff" }));
    }

    [Fact]
    public void ResolveOfABareNameFindsItInTheDirectory()
    {
        var expected = Touch("Glass.aiff");

        Assert.Equal(expected, SystemSoundCatalog.Resolve("Glass", _dir, new[] { ".aiff" }));
    }

    [Fact]
    public void ResolveOfABareNameNotOnDiskIsNull()
    {
        Assert.Null(SystemSoundCatalog.Resolve("Nonexistent", _dir, new[] { ".aiff" }));
    }

    // A default that used to exist and was removed — a system sound deleted,
    // a chosen file moved — reads as silence rather than an exception. The
    // plan states this explicitly: a missing default resolves to off, not
    // an error nobody asked for.
    [Fact]
    public void ResolveOfANameThatNoLongerExistsIsNullNotAThrow()
    {
        var path = Touch("Glass.aiff");
        File.Delete(path);

        Assert.Null(SystemSoundCatalog.Resolve("Glass", _dir, new[] { ".aiff" }));
    }

    [Fact]
    public void ResolveTriesExtensionsInOrderAndStopsAtTheFirstMatch()
    {
        Touch("Chime.wav");
        var aiff = Touch("Chime.aiff");

        Assert.Equal(aiff, SystemSoundCatalog.Resolve("Chime", _dir, new[] { ".aiff", ".wav" }));
    }

    [Fact]
    public void ResolveOfAnAbsolutePathTrustsItAsIsWhenItExists()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var custom = Path.Combine(elsewhere, "my-chosen-sound.mp3");
        File.WriteAllBytes(custom, Array.Empty<byte>());

        // A directory nowhere near the one passed in — proving the absolute
        // path is trusted on its own rather than looked up against `_dir`.
        Assert.Equal(custom, SystemSoundCatalog.Resolve(custom, _dir, new[] { ".aiff" }));
    }

    [Fact]
    public void ResolveOfAnAbsolutePathThatDoesNotExistIsNull()
    {
        var missing = Path.Combine(_dir, "gone.mp3");

        Assert.Null(SystemSoundCatalog.Resolve(missing, _dir, new[] { ".aiff" }));
    }

    // --- platform defaults ---

    [Fact]
    public void TheDefaultDirectoryAndExtensionsAreOneOfTheTwoSupportedPlatforms()
    {
        var directory = SystemSoundCatalog.DefaultDirectory;
        var extensions = SystemSoundCatalog.DefaultExtensions;

        Assert.True(
            (directory == "/System/Library/Sounds" && extensions.SequenceEqual(new[] { ".aiff" }))
            || (directory == @"C:\Windows\Media" && extensions.SequenceEqual(new[] { ".wav" })));
    }

    [Fact]
    public void TheDefaultSoundNamesAreOneOfTheTwoSupportedPlatforms()
    {
        Assert.True(SystemSoundCatalog.DefaultFinishedSoundName is "Glass" or "Windows Notify Messaging");
        Assert.True(
            SystemSoundCatalog.DefaultAttentionSoundName is "Ping" or "Windows Notify System Generic");
    }
}
