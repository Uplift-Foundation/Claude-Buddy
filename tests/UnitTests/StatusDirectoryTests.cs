using Xunit;

namespace ClaudeBuddy.UnitTests;

// Which temp root holds the session status files.
//
// **The hooks and the app have to agree on this, and on a launchd-started Mac
// they did not.** Hooks run from the user's shell, where `TMPDIR` is the
// per-user temp directory macOS gives every login session. The app asked .NET
// for `Path.GetTempPath()`, which reads `TMPDIR` and falls back to `/tmp` when
// it is unset — and a launchd agent's environment carries no `TMPDIR` at all.
//
// Measured on the mini: `launchctl print` listed no TMPDIR for the job, and
// `/tmp/claude_buddy` did not exist. So Buddy read an empty directory, drew no
// orbs, and told every other machine it had no sessions, while two live Claude
// Code sessions sat in the real directory the whole time. Nothing errored — the
// two halves were looking in different places.
//
// That is precisely the deployment the direct link is for: a headless Mac,
// always on, started by launchd, serving the machine somebody is sitting at.
public class StatusDirectoryTests
{
    [Fact]
    public void AnExplicitTmpdirAlwaysWins()
    {
        // **Not merely first — load-bearing.** Every isolated run in this
        // repository works by setting TMPDIR to get its own status directory,
        // so a rule that overruled it would break the test suites and every
        // second instance launched for a manual test.
        Assert.Equal(
            "/set/by/the/caller",
            StatusDirectory.Root("/set/by/the/caller", () => "/per/user", "/tmp"));
    }

    [Fact]
    public void WithNoTmpdirThePlatformIsAsked()
    {
        // The launchd case, which is the one that shipped broken.
        Assert.Equal("/per/user", StatusDirectory.Root(null, () => "/per/user", "/tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyTmpdirCountsAsNoneRatherThanAsARoot(string tmpdir)
    {
        // An empty variable is set-but-useless, and joining a folder onto it
        // would produce a relative path that resolves against whatever the
        // working directory happens to be.
        Assert.Equal("/per/user", StatusDirectory.Root(tmpdir, () => "/per/user", "/tmp"));
    }

    [Fact]
    public void WithNothingToGoOnTheOldBehaviourRemains()
    {
        // On a platform that cannot answer — or when the call fails — this
        // falls back to what .NET said, which is what it did before. Being no
        // worse than the previous behaviour is the floor.
        Assert.Equal("/tmp", StatusDirectory.Root(null, () => null, "/tmp"));
    }

    [Fact]
    public void AnEmptyPlatformAnswerIsNotUsedEither()
    {
        Assert.Equal("/tmp", StatusDirectory.Root(null, () => "", "/tmp"));
    }

    [Fact]
    public void ThePlatformIsNotAskedWhenTmpdirIsSet()
    {
        // It is a P/Invoke. Doing it on every scan when the answer is already
        // in the environment would be work for nothing.
        var asked = 0;

        StatusDirectory.Root("/mine", () => { asked++; return "/per/user"; }, "/tmp");

        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheFolderNameIsTheOneTheHooksWrite()
    {
        // Both halves of this agreement live in different languages — the hooks
        // are shell and PowerShell — so the name is worth pinning on this side
        // at least. tests/IntegrationTests drives the real hooks against a
        // scratch TMPDIR and would catch a change on the other side.
        Assert.Equal("claude_buddy", StatusDirectory.FolderName);
    }
}
