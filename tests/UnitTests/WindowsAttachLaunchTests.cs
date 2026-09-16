using Xunit;

namespace ClaudeBuddy.Tests;

// The Windows half of a background-orb click cannot create a terminal in a
// headless test runner. Its command description is pure, though, and this
// protects the two details that decide whether the visible terminal reaches
// the clicked session: the working directory and the short job id accepted by
// `claude attach`.
public class WindowsAttachLaunchTests
{
    [Fact]
    public void WindowsTerminalLaunchKeepsTheDirectoryAndTargetsTheShortJobId()
    {
        var start = AgentTeamViewer.WindowsAttachStartInfo(
            @"C:\Program Files\Claude\claude.exe", @"C:\work\with spaces",
            "b1425d42-3c45-4e9d-bf25-38194fae23c0", useWindowsTerminal: true);

        Assert.NotNull(start);
        Assert.Equal("wt.exe", start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Equal(@"C:\work\with spaces", start.WorkingDirectory);
        Assert.Equal(new[]
        {
            "-d", @"C:\work\with spaces", "cmd.exe", "/k",
            @"C:\Program Files\Claude\claude.exe", "attach", "b1425d42"
        }, start.ArgumentList);
    }

    [Fact]
    public void CmdFallbackCarriesTheSameAttachTarget()
    {
        var start = AgentTeamViewer.WindowsAttachStartInfo(
            "claude.exe", @"C:\work", "b1425d42-3c45-4e9d-bf25-38194fae23c0",
            useWindowsTerminal: false);

        Assert.NotNull(start);
        Assert.Equal("cmd.exe", start.FileName);
        Assert.Equal(new[] { "/k", "claude.exe", "attach", "b1425d42" }, start.ArgumentList);
    }

    [Theory]
    [InlineData(null, "session")]
    [InlineData("claude.exe", null)]
    [InlineData("", "session")]
    [InlineData("claude.exe", "")]
    public void MissingLaunchCoordinatesRefuseToProduceACommand(string? claude, string? sessionId)
    {
        Assert.Null(AgentTeamViewer.WindowsAttachStartInfo(
            claude, @"C:\work", sessionId, useWindowsTerminal: true));
    }
}
