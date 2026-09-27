using Xunit;

namespace ClaudeBuddy.Tests;

// CB-216: the server side of pictures-by-id. A roster is built for every peer's
// ask every ten seconds, and the pictures in it were 11-15 MB GIFs read and
// hashed on every build. The store reads a picture again only when its file
// changes, and answers AVATAR only for ids it has offered.
public class PeerAvatarStoreTests
{
    private readonly Dictionary<string, (long Length, DateTime Written)> _files = new();
    private readonly Dictionary<string, byte[]> _contents = new();
    private int _reads;

    private PeerAvatarStore Store() => new(
        path => _files.TryGetValue(path, out var stat) ? stat : null,
        path => { _reads++; return _contents.GetValueOrDefault(path); });

    private void Write(string path, byte[] bytes, int minute = 0)
    {
        _files[path] = (bytes.Length, new DateTime(2026, 9, 27, 12, minute, 0, DateTimeKind.Utc));
        _contents[path] = bytes;
    }

    [Fact]
    public void AnUnchangedFileIsReadOnce()
    {
        Write("/p/ava.gif", new byte[] { 1, 2, 3 });
        var store = Store();

        var first = store.At("/p/ava.gif");
        var second = store.At("/p/ava.gif");

        Assert.Equal(1, _reads);
        Assert.Equal(MirrorProtocol.AvatarIdOf(new byte[] { 1, 2, 3 }), first!.Value.Id);
        Assert.Same(first.Value.Bytes, second!.Value.Bytes);
    }

    [Theory]
    [InlineData(false)]   // same length, rewritten later
    [InlineData(true)]    // a different length
    public void AChangedFileIsReadAgainAndGetsANewId(bool resize)
    {
        Write("/p/ava.gif", new byte[] { 1, 2, 3 });
        var store = Store();
        var before = store.At("/p/ava.gif")!.Value.Id;

        Write("/p/ava.gif", resize ? new byte[] { 9, 9, 9, 9 } : new byte[] { 9, 9, 9 }, minute: 1);
        var after = store.At("/p/ava.gif")!.Value.Id;

        Assert.Equal(2, _reads);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void AFileThatIsGoneOrUnreadableHasNoPicture()
    {
        var store = Store();
        Assert.Null(store.At("/p/missing.gif"));

        _files["/p/refused.gif"] = (3, DateTime.UtcNow);   // stats, but will not read
        Assert.Null(store.At("/p/refused.gif"));
    }

    [Fact]
    public void AnOfferedPictureIsFoundByItsIdAndNothingElseIs()
    {
        Write("/p/ava.gif", new byte[] { 1, 2, 3 });
        var store = Store();
        var id = store.At("/p/ava.gif")!.Value.Id;

        Assert.Equal(new byte[] { 1, 2, 3 }, store.ById(id));
        Assert.Null(store.ById(MirrorProtocol.AvatarIdOf(new byte[] { 4 })));
        Assert.Null(store.ById(null));
        Assert.Null(store.ById(""));
    }

    [Fact]
    public void ARereadPictureReplacesItsOldIdRatherThanKeepingBoth()
    {
        Write("/p/ava.gif", new byte[] { 1, 2, 3 });
        var store = Store();
        var old = store.At("/p/ava.gif")!.Value.Id;

        Write("/p/ava.gif", new byte[] { 7, 7, 7 }, minute: 1);
        store.At("/p/ava.gif");

        Assert.Null(store.ById(old));
    }

    [Fact]
    public void TheOldestPictureIsForgottenPastCapacity()
    {
        var store = Store();
        for (var i = 0; i <= PeerAvatarStore.Capacity; i++)
        {
            Write($"/p/{i}.gif", new[] { (byte)i });
            store.At($"/p/{i}.gif");
        }

        Assert.Null(store.ById(MirrorProtocol.AvatarIdOf(new byte[] { 0 })));
        Assert.NotNull(store.ById(MirrorProtocol.AvatarIdOf(new[] { (byte)PeerAvatarStore.Capacity })));

        // Forgotten means read again next time, never a wrong answer.
        store.At("/p/0.gif");
        Assert.Equal(PeerAvatarStore.Capacity + 2, _reads);
    }
}
