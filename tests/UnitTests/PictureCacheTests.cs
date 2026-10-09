using Xunit;

namespace Orbweaver.Tests;

// CB-216: the client's pictures-by-id cache, and the rule for which roster hash
// it hands back on the next ask.
public class PictureCacheTests
{
    [Fact]
    public void APictureIsHeldByItsId()
    {
        var cache = new PictureCache(2);
        var bytes = new byte[] { 1 };

        cache.Add("a", bytes);

        Assert.Same(bytes, cache.Get("a"));
        Assert.Null(cache.Get("b"));
    }

    [Fact]
    public void AddingAnIdAgainKeepsTheFirstBytes()
    {
        // The bytes under an id cannot differ, so a second add is a no-op and
        // does not count against the capacity twice.
        var cache = new PictureCache(2);
        var first = new byte[] { 1 };

        cache.Add("a", first);
        cache.Add("a", new byte[] { 1 });

        Assert.Same(first, cache.Get("a"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TheOldestPictureGoesFirstPastCapacity()
    {
        var cache = new PictureCache(2);
        cache.Add("a", new byte[] { 1 });
        cache.Add("b", new byte[] { 2 });
        cache.Add("c", new byte[] { 3 });

        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("b"));
        Assert.NotNull(cache.Get("c"));
        Assert.Equal(2, cache.Count);
    }

    // --- which roster hash to hold ---------------------------------------------

    private static Dictionary<string, string> Fields(string? hash) =>
        hash is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [MirrorProtocol.RosterHashField] = hash };

    [Fact]
    public void AHashTheServerSentIsHeldOnceEveryPictureArrived() =>
        Assert.Equal("abc", RemoteMirrorClient.HashToHold(Fields("abc"), allPictures: true));

    [Theory]
    [InlineData("abc", false)]   // a picture is still missing: ask in full again
    [InlineData(null, true)]     // an older server sent no hash
    [InlineData("", true)]       // an empty one is not a hash
    public void NoHashIsHeldOtherwise(string? hash, bool allPictures) =>
        Assert.Null(RemoteMirrorClient.HashToHold(Fields(hash), allPictures));

    [Fact]
    public void NoHashIsHeldForAReplyWithNoFields() =>
        Assert.Null(RemoteMirrorClient.HashToHold(null, allPictures: true));
}
