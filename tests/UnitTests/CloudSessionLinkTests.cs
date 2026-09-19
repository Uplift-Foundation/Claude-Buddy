using Xunit;

namespace ClaudeBuddy.Tests;

// How the app asks the OS to open a cloud session's URL.
//
// Both platforms asserted from either runner, because the decision is not a
// platform detail — it is a decision *about* platforms, and a rule that can only
// be checked on the machine it describes is a rule half the CI matrix never
// reads. StartInfoFor takes the two answers as arguments for exactly that.
//
// Nothing here starts a process. The launch itself is the one thing this type
// keeps behind [ExcludeFromCodeCoverage], since there is no version of running
// it that does not open a real browser window on whatever machine ran the suite.
public class CloudSessionLinkTests
{
    private const string Url = "https://claude.ai/code/session_01abc";

    // macOS hands the URL to `open` as an argument, never as the FileName. The
    // distinction is the whole safety property: a string that turns out not to
    // be a URL is then an argument to a known program rather than a program to
    // run.
    [Fact]
    public void MacOsRunsOpenWithTheUrlAsAnArgument()
    {
        var info = CloudSessionLink.StartInfoFor(Url, isMacOs: true, isWindows: false);

        Assert.NotNull(info);
        Assert.Equal("open", info!.FileName);
        Assert.Equal(new[] { Url }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    // Windows must do the opposite, and it is not a style choice: a URL is a
    // thing for the shell to resolve, and CreateProcess cannot start one. With
    // UseShellExecute false this throws rather than opening anything, which is
    // the same bug SettingsWindow's ms-settings: link was fixed for.
    [Fact]
    public void WindowsHandsTheUrlToTheShell()
    {
        var info = CloudSessionLink.StartInfoFor(Url, isMacOs: false, isWindows: true);

        Assert.NotNull(info);
        Assert.Equal(Url, info!.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.ArgumentList);
    }

    // Neither platform is not a guess to be improvised. This app ships for two,
    // and a caller handed null opens nothing — which is the correct behaviour
    // for "we do not know how", and better than launching something plausible.
    [Fact]
    public void AnUnknownPlatformGetsNothing()
    {
        Assert.Null(CloudSessionLink.StartInfoFor(Url, isMacOs: false, isWindows: false));
    }

    // A session whose address never arrived opens nothing, on either platform.
    // Without this the macOS arm would run `open` with no argument, which opens
    // a Finder window.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentUrlGetsNothing(string? url)
    {
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: true, isWindows: false));
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: false, isWindows: true));
    }

    // The convenience overload asks the running OS and agrees with whichever
    // arm above applies. Written as an implication rather than as a fixed
    // expectation so it says the same thing on both CI legs.
    [Fact]
    public void TheMachinesOwnAnswerMatchesTheExplicitOne()
    {
        var actual = CloudSessionLink.StartInfoFor(Url);
        var expected = CloudSessionLink.StartInfoFor(
            Url, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

        Assert.Equal(expected?.FileName, actual?.FileName);
        Assert.Equal(expected?.UseShellExecute, actual?.UseShellExecute);
    }
}
