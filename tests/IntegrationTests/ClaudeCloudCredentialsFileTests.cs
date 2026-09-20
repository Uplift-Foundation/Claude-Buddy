using System;
using System.IO;
using System.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// Drives FileCredentialSource over real files in a scratch directory — the
// Windows and Linux credential store, which is a file and therefore is the half
// of this arm that can actually be exercised end to end.
//
// It belongs here rather than in UnitTests for the reason the repo already
// separates the two: the parse is covered as a unit, and this covers the *seam*
// with something this process does not own. The two fail differently. A parser
// bug gets a field wrong; a seam bug gets the whole exchange wrong — a file that
// exists but is empty, a permission error, an mtime that does not move.
//
// **Nothing here touches a real credential, the real Keychain or the network.**
// The tokens are canaries, chosen to be unmistakable if one ever escapes into
// wording, and every file is written into a temp directory this fixture owns and
// deletes.
public class ClaudeCloudCredentialsFileTests : IDisposable
{
    private const string FakeAccess = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private readonly string _dir;

    public ClaudeCloudCredentialsFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "cb-cloud-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that outlives the run is not a test failure.
        }
    }

    private string Path_ => System.IO.Path.Combine(_dir, ".credentials.json");

    private FileCredentialSource Source() => new(Path_);

    private void Write(string contents) => File.WriteAllText(Path_, contents);

    private static string Blob(long expiresAtMillis) =>
        $$$"""
        {"claudeAiOauth":{"accessToken":"{{{FakeAccess}}}","refreshToken":"r","expiresAt":{{{expiresAtMillis}}}}}
        """;

    private static long InAnHour =>
        DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();

    private static long AnHourAgo =>
        DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();

    [Fact]
    public void APresentUnexpiredCredentialIsFound()
    {
        Write(Blob(InAnHour));

        var read = Source().Read();

        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        Assert.Equal(FakeAccess, read.AccessToken);
    }

    [Fact]
    public void AnAbsentFileIsNotLoggedInRatherThanAnError()
    {
        var read = Source().Read();

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    // A zero-byte file is a real state — an interrupted write, a disk that filled
    // up — and it is NotLoggedIn rather than Malformed, because there is nothing
    // there to be wrong.
    [Fact]
    public void AZeroByteFileIsNotLoggedIn()
    {
        Write("");

        Assert.Equal(CredentialOutcome.NotLoggedIn, Source().Read().Outcome);
    }

    [Fact]
    public void GarbageIsMalformed()
    {
        Write("}{ this is not json");

        var read = Source().Read();

        Assert.Equal(CredentialOutcome.Malformed, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    [Fact]
    public void ValidJsonOfTheWrongShapeIsMalformed()
    {
        Write("""{"someOtherProduct":{"accessToken":"x"}}""");

        Assert.Equal(CredentialOutcome.Malformed, Source().Read().Outcome);
    }

    [Fact]
    public void AnExpiredCredentialIsNotLoggedInAndHandsBackNoToken()
    {
        Write(Blob(AnHourAgo));

        var read = Source().Read();

        Assert.Equal(CredentialOutcome.NotLoggedIn, read.Outcome);
        Assert.Null(read.AccessToken);
    }

    [Fact]
    public void NoWordingFromAnyFileStateCarriesTheToken()
    {
        foreach (var contents in new[] { Blob(InAnHour), Blob(AnHourAgo), FakeAccess, "" })
        {
            Write(contents);
            var read = Source().Read();
            Assert.DoesNotContain(FakeAccess, read.Detail ?? "", StringComparison.Ordinal);
        }
    }

    // ## The stamp
    //
    // Stamp() is what lets a poll notice a re-login without reading the secret
    // every tick — on macOS that is the difference between prompting and not.
    // Its contract is narrow and worth pinning: it changes when the file changes
    // and not otherwise, and it never carries secret bytes.

    [Fact]
    public void AnAbsentFileHasNoStamp()
    {
        Assert.Null(Source().Stamp());
    }

    [Fact]
    public void AStampIsStableWhileTheFileIsNotRewritten()
    {
        Write(Blob(InAnHour));
        var source = Source();

        var first = source.Stamp();
        Assert.NotNull(first);
        Assert.Equal(first, source.Stamp());
        Assert.Equal(first, source.Stamp());
    }

    // The sleep is not a timing assumption about the code under test — it is
    // about the *filesystem's* mtime granularity, which on some filesystems is
    // coarse. Without it this would be a flake about HFS rather than a test of
    // FileCredentialSource. CLAUDE.md's rule against sleeps is about making a
    // test independent of what else is running; this one waits on a property of
    // the disk, which no amount of restructuring removes.
    [Fact]
    public void AStampChangesWhenTheFileIsRewritten()
    {
        Write(Blob(InAnHour));
        var source = Source();
        var before = source.Stamp();

        Thread.Sleep(50);
        Write(Blob(InAnHour + 1000));

        var after = source.Stamp();

        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AStampCarriesNothingDerivedFromTheCredential()
    {
        Write(Blob(InAnHour));

        var stamp = Source().Stamp();

        Assert.NotNull(stamp);
        Assert.DoesNotContain(FakeAccess, stamp!, StringComparison.Ordinal);
        // A tick count, so digits and nothing else.
        Assert.True(long.TryParse(stamp, out _), $"stamp was not a tick count: {stamp}");
    }

    // A directory where the file should be reads as "nothing stored", because
    // File.Exists is false for one. That is the honest answer rather than a
    // contrivance: there is no credential at that path, and saying so is what a
    // user needs to hear.
    [Fact]
    public void ADirectoryWhereTheFileShouldBeReadsAsNothingStored()
    {
        Directory.CreateDirectory(Path_);

        Assert.Equal(CredentialOutcome.NotLoggedIn, Source().Read().Outcome);
        Assert.Null(Source().Stamp());
    }

    // ## The two OS-refusal arms
    //
    // Denied is genuinely one-legged: it is enforced by the Unix permission bits
    // against the file's own owner, and an administrator on Windows is not
    // refused by an ACL it can rewrite. So that one runs on Unix and returns
    // early on Windows.
    //
    // **Unreadable is not, and this file used to claim it was.** The reasoning
    // was that a share-mode lock is a Windows concept Unix does not have, which
    // is true of the *kernel* and not of .NET: FileStream emulates FileShare on
    // Unix with flock, so a handle opened FileShare.None makes the next
    // File.ReadAllText throw IOException on macOS exactly as it does on Windows.
    // Measured, not assumed — the skip was costing four lines of coverage on
    // every macOS leg for an arm that is reachable there.

    [Fact]
    public void AFileTheUserCannotReadIsDeniedRatherThanRetried()
    {
        if (OperatingSystem.IsWindows()) return;

        Write(Blob(InAnHour));
        File.SetUnixFileMode(Path_, UnixFileMode.None);

        try
        {
            var read = Source().Read();

            Assert.Equal(CredentialOutcome.Denied, read.Outcome);
            Assert.Null(read.AccessToken);

            // Denied stops rather than backing off: retrying a refusal is how a
            // prompt storm starts on the other platform's store.
            Assert.Null(Backoff.Next(read.Outcome, null));
        }
        finally
        {
            File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // A path the platform will not accept at all. Contrived — nothing in the app
    // builds a path like this — but the guard it exercises is not: Stamp() runs
    // on a poll, and an exception out of a timer is a worse failure than a null.
    [Fact]
    public void APathThePlatformRejectsOutrightHasNoStampRatherThanThrowing()
    {
        var source = new FileCredentialSource(System.IO.Path.Combine(_dir, "bad\0name.json"));

        Assert.Null(source.Stamp());
    }

    [Fact]
    public void AFileHeldExclusivelyByAnotherHandleIsUnreadable()
    {
        Write(Blob(InAnHour));

        using var held = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(CredentialOutcome.Unreadable, Source().Read().Outcome);
    }

    // The negative control for it, and the reason the case above is worth having
    // rather than being a restatement of what FileStream does: released, the same
    // file reads. Without this, a bug that reported Unreadable for every file
    // would pass the case above and nothing else here would notice, since every
    // other fixture asserts on the parse rather than on the read succeeding.
    [Fact]
    public void TheSameFileReadsOnceTheHandleIsReleased()
    {
        Write(Blob(InAnHour));

        using (new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(CredentialOutcome.Unreadable, Source().Read().Outcome);
        }

        Assert.Equal(CredentialOutcome.Found, Source().Read().Outcome);
    }
}
