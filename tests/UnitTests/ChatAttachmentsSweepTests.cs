using Xunit;

namespace ClaudeBuddy.Tests;

// The sweep that runs before every pasted picture is written.
//
// ChatPanel's paste path is already driven end-to-end in tests/UiTests, which
// is what covers Save() — but Save's *first* act is to delete other people's
// files, and nothing exercised that. It matters more than its four lines
// suggest: the cutoff decides whether a path this app has just typed into a
// terminal still resolves by the time the CLI on the other end opens it. Too
// eager and a picture is gone before it is read; the six-hour window is the
// deliberate answer, and a test that pins it is what stops it drifting.
public class ChatAttachmentsSweepTests
{
    private static string Scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FilesOlderThanTheCutoffGoAndNewerOnesStay()
    {
        var dir = Scratch();
        try
        {
            var stale = Path.Combine(dir, "paste-old.png");
            var fresh = Path.Combine(dir, "paste-new.png");
            File.WriteAllText(stale, "x");
            File.WriteAllText(fresh, "x");

            // Six hours is the window Sweep's own cutoff names, so the two
            // fixtures straddle it rather than sitting a token distance apart:
            // seven hours old must go, five hours old must not.
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(7));
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow - TimeSpan.FromHours(5));

            ChatAttachments.Sweep(dir);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A directory that isn't there is the ordinary state on a first paste after
    // a reboot — Save creates it, but Sweep is also reachable with nothing on
    // disk, and the comment on the catch ("a file we can't delete is one still
    // being read") is a promise that a failure here is never allowed to reach
    // the paste. A throw out of here would lose the picture the user just
    // copied, which is the one thing this must not do.
    [Fact]
    public void AMissingDirectoryIsSwallowedRatherThanFailingThePaste()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cb-sweep-absent-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        ChatAttachments.Sweep(missing);
    }
}
