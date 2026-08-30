using System.Text;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Covers PeerProtocol — the wire format two copies of this app use over a direct
// connection, replacing the one that existed to survive a language model
// retyping base64 by hand.
//
// What is worth testing here is different from what was worth testing in
// MirrorProtocol, and the difference is the point. That format's promise was
// "nothing that failed its digest is ever handed on", because the courier could
// mangle text; the tampering cases were the feature. TLS makes that promise for
// this one, so what is left to get wrong is *framing* — where one message ends
// and the next begins — and the refusals that stop a confused or hostile peer
// turning a length field into an allocation.
public class PeerProtocolTests
{
    private static PeerProtocol.PeerMessage Sample(string type = PeerProtocol.Fetch) =>
        PeerProtocol.Message(type, "abc12345", name: "job-hunter-mac-mini");

    // --- the shape -----------------------------------------------------------

    [Fact]
    public async Task AMessageRoundTripsThroughItsOwnReader()
    {
        using var stream = new MemoryStream();
        await PeerProtocol.WriteAsync(stream, Sample());

        stream.Position = 0;
        var back = await PeerProtocol.ReadAsync(stream);

        Assert.NotNull(back);
        Assert.Equal(PeerProtocol.Fetch, back!.Type);
        Assert.Equal("abc12345", back.Id);
        Assert.Equal("job-hunter-mac-mini", back.Name);
        Assert.Equal(PeerProtocol.Version, back.Version);
    }

    // A body is left as raw JSON precisely so the four things that carry one — a
    // roster, a window, a delta, a line of input — need nothing in common.
    [Fact]
    public async Task ABodySurvivesAsWhateverItWas()
    {
        var body = PeerProtocol.BodyOf(new { turns = new[] { "one", "two" }, gen = 7 });

        using var stream = new MemoryStream();
        await PeerProtocol.WriteAsync(
            stream, PeerProtocol.Message(PeerProtocol.Window, "id", body: body));

        stream.Position = 0;
        var back = await PeerProtocol.ReadAsync(stream);

        Assert.NotNull(back!.Body);
        Assert.Equal(7, back.Body!.Value.GetProperty("gen").GetInt32());
        Assert.Equal(2, back.Body!.Value.GetProperty("turns").GetArrayLength());
    }

    // The old format could not carry a newline at all — a relay pane only moved
    // lines, so a transcript had to be base64'd to survive. This one carries the
    // text itself, which is most of why it is faster.
    [Fact]
    public async Task TextWithNewlinesAndQuotesNeedsNoEncoding()
    {
        var awkward = "line one\nline \"two\"\r\n\ttabbed \\ backslash — em dash 🙂";

        using var stream = new MemoryStream();
        await PeerProtocol.WriteAsync(
            stream,
            PeerProtocol.Message(PeerProtocol.Input, "id", body: PeerProtocol.BodyOf(awkward)));

        stream.Position = 0;
        var back = await PeerProtocol.ReadAsync(stream);

        Assert.Equal(awkward, back!.Body!.Value.GetString());
    }

    // --- framing -------------------------------------------------------------

    // The property the whole format rests on: a reader takes exactly one message
    // and leaves the next one untouched, however they were packed.
    [Fact]
    public async Task MessagesBackToBackAreReadOneAtATime()
    {
        using var stream = new MemoryStream();
        await PeerProtocol.WriteAsync(stream, PeerProtocol.Message(PeerProtocol.Hello, "first"));
        await PeerProtocol.WriteAsync(stream, PeerProtocol.Message(PeerProtocol.Roster, "second"));
        await PeerProtocol.WriteAsync(stream, PeerProtocol.Message(PeerProtocol.Ok, "third"));

        stream.Position = 0;

        Assert.Equal("first", (await PeerProtocol.ReadAsync(stream))!.Id);
        Assert.Equal("second", (await PeerProtocol.ReadAsync(stream))!.Id);
        Assert.Equal("third", (await PeerProtocol.ReadAsync(stream))!.Id);
        Assert.Null(await PeerProtocol.ReadAsync(stream));
    }

    // A socket hands over whatever has arrived, not whatever was sent. A reader
    // that assumed one read per message would work on a MemoryStream and fail on
    // a network — so this asserts against a stream that deliberately dribbles.
    [Fact]
    public async Task AMessageSplitAcrossManyReadsStillArrivesWhole()
    {
        var body = PeerProtocol.BodyOf(new string('x', 5000));

        using var whole = new MemoryStream();
        await PeerProtocol.WriteAsync(
            whole, PeerProtocol.Message(PeerProtocol.Window, "split", body: body));

        using var dribbling = new DribblingStream(whole.ToArray(), perRead: 7);
        var back = await PeerProtocol.ReadAsync(dribbling);

        Assert.Equal("split", back!.Id);
        Assert.Equal(5000, back.Body!.Value.GetString()!.Length);
    }

    // A clean hangup between messages is an ordinary disconnect, not a fault to
    // report — distinct from the two dirty cases below.
    [Fact]
    public async Task AnEmptyStreamIsAHangupRatherThanAnError()
    {
        using var stream = new MemoryStream();
        Assert.Null(await PeerProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task AStreamThatDiesInsideTheHeaderIsAnError()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0 });

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await PeerProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task AStreamThatDiesInsideTheBodyIsAnError()
    {
        using var whole = new MemoryStream();
        await PeerProtocol.WriteAsync(whole, Sample());

        // Everything except the last byte: a length that promises more than
        // arrives, which is what a dropped connection mid-message looks like.
        var truncated = whole.ToArray()[..^1];

        using var stream = new MemoryStream(truncated);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await PeerProtocol.ReadAsync(stream));
    }

    // --- refusing an absurd length --------------------------------------------

    // The one place a peer can make this process do something expensive by
    // saying so. Both arms are real: a negative length is a garbled or hostile
    // sender, and an oversized one would otherwise be honoured as an allocation.
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(PeerProtocol.MaxMessageBytes + 1)]
    public async Task AnImpossibleLengthIsRefusedBeforeAnythingIsAllocated(int announced)
    {
        var header = new byte[PeerProtocol.HeaderBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, announced);

        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await PeerProtocol.ReadAsync(stream));
    }

    // The ceiling is generous against the real case rather than tight: the
    // largest window measured on a real machine was 524KB of transcript, out of
    // a 6MB file.
    [Fact]
    public void TheCeilingClearsARealTranscriptWindowComfortably() =>
        Assert.True(PeerProtocol.MaxMessageBytes > 8 * 1024 * 1024);

    [Fact]
    public void EncodingSomethingOverTheCeilingIsRefusedRatherThanTruncated()
    {
        var huge = PeerProtocol.BodyOf(new string('x', PeerProtocol.MaxMessageBytes + 1));

        Assert.Throws<InvalidOperationException>(
            () => PeerProtocol.Encode(PeerProtocol.Message(PeerProtocol.Window, "id", body: huge)));
    }

    // --- the header itself -----------------------------------------------------

    // Big-endian, and asserted rather than assumed: this is the one field two
    // machines must agree on byte for byte, and the two could differ in
    // architecture.
    [Fact]
    public void TheLengthIsFourBytesBigEndian()
    {
        var encoded = PeerProtocol.Encode(PeerProtocol.Message(PeerProtocol.Ok, "id"));
        var announced = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(encoded);

        Assert.Equal(PeerProtocol.HeaderBytes, 4);
        Assert.Equal(encoded.Length - PeerProtocol.HeaderBytes, announced);
    }

    // --- ids -------------------------------------------------------------------

    [Fact]
    public void IdsAreShortAndDoNotRepeat()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => PeerProtocol.NewId()).ToList();

        Assert.All(ids, id => Assert.Equal(8, id.Length));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // A stream that returns a few bytes per read, the way a socket does.
    private sealed class DribblingStream(byte[] data, int perRead) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= data.Length) return 0;

            var take = Math.Min(Math.Min(perRead, count), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, take);
            _at += take;

            return take;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at >= data.Length) return ValueTask.FromResult(0);

            var take = Math.Min(Math.Min(perRead, buffer.Length), data.Length - _at);
            data.AsSpan(_at, take).CopyTo(buffer.Span);
            _at += take;

            return ValueTask.FromResult(take);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
