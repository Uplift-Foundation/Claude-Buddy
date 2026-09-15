using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// The `.npy` reader, the weighted average and the writer, over bytes and
// nothing else (CB-136).
//
// A format nobody here controls gets a parser with a case per arm, which is
// the rule this repository already applies to two transcript parsers and a
// tmux pane. It matters more here than usual for a reason worth stating: a
// wrong answer is not an exception, it is a voice. Shift the body by two bytes
// and every float is garbage the engine loads happily and speaks; read a
// Fortran-ordered array as C-ordered and the average is a transposed smear.
// Neither can be caught by looking at the result.
//
// The fixtures are built to the shape of the real files rather than from the
// spec: `af_sky.npy` on this machine is a 128-byte version-1.0 prelude with
// `{'descr': '<f4', 'fortran_order': False, 'shape': (510, 1, 256), }` in it,
// over 130,560 little-endian float32s.
public class NumpyVoiceTests
{
    // A version 1.0 file, built the way numpy builds one, so the reader is
    // being asked about the format rather than about this test's idea of it.
    private static byte[] File1(string descr, bool fortran, int[] shape, byte[] body) =>
        File1Raw(
            $"{{'descr': '{descr}', 'fortran_order': {(fortran ? "True" : "False")}, " +
            $"'shape': ({string.Join(", ", shape)}{(shape.Length == 1 ? "," : "")}), }}",
            body);

    // The same, with the header dictionary written out by hand — for the cases
    // that are about a header this reader should refuse. Built from bytes
    // rather than by editing a good file's text, because the magic's first
    // byte is 0x93 and an ASCII round trip quietly turns it into a question
    // mark, which is a different refusal than the one being asked about.
    private static byte[] File1Raw(string dictionary, byte[] body)
    {
        var padding = (64 - ((10 + dictionary.Length + 1) % 64)) % 64;
        var header = dictionary + new string(' ', padding) + "\n";

        var bytes = new byte[10 + header.Length + body.Length];
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 0);
        bytes[0] = 0x93;
        bytes[6] = 1;
        bytes[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)header.Length);
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 10);
        body.CopyTo(bytes, 10 + header.Length);

        return bytes;
    }

    private static byte[] Float32Body(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }

    private static byte[] Float16Body(params float[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2, 2), (Half)values[i]);
        return bytes;
    }

    internal static byte[] Voice(int[] shape, params float[] values) =>
        File1("<f4", fortran: false, shape, Float32Body(values));

    private static NumpyVoices.Tensor Read(byte[] bytes)
    {
        var tensor = NumpyVoices.Read(bytes, out var refusal);
        Assert.Null(refusal);
        Assert.NotNull(tensor);
        return tensor!;
    }

    private static string Refusal(byte[] bytes)
    {
        Assert.Null(NumpyVoices.Read(bytes, out var refusal));
        Assert.NotNull(refusal);
        return refusal!;
    }

    // --- reading -----------------------------------------------------------

    [Fact]
    public void AFloat32VoiceReadsBackAsItsShapeAndItsValues()
    {
        var tensor = Read(Voice(new[] { 2, 1, 3 }, 1f, 2f, 3f, 4f, 5f, 6f));

        Assert.Equal(new[] { 2, 1, 3 }, tensor.Shape);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, tensor.Values);
    }

    [Fact]
    public void AOneDimensionalShapeWithNumpysTrailingCommaIsStillOneDimension()
    {
        var tensor = Read(Voice(new[] { 3 }, 7f, 8f, 9f));

        Assert.Equal(new[] { 3 }, tensor.Shape);
        Assert.Equal(new[] { 7f, 8f, 9f }, tensor.Values);
    }

    // Included even though no voice on this machine is half-precision. A
    // future model shipping fp16 packs read as float32 is not an error — it is
    // garbage of exactly the right length, and the engine would speak it.
    [Fact]
    public void AFloat16VoiceIsWidenedRatherThanMisread()
    {
        var tensor = Read(File1("<f2", fortran: false, new[] { 4 }, Float16Body(0.5f, -1.5f, 2f, 0f)));

        Assert.Equal(new[] { 0.5f, -1.5f, 2f, 0f }, tensor.Values);
    }

    // Version 2 and 3 widened the header length from two bytes to four, and
    // that offset is exactly the two bytes that would shift every float.
    [Fact]
    public void AVersionTwoHeaderIsFourBytesLongAndTheBodyStillLinesUp()
    {
        var dictionary = "{'descr': '<f4', 'fortran_order': False, 'shape': (3,), }";
        var padding = (64 - ((12 + dictionary.Length + 1) % 64)) % 64;
        var header = dictionary + new string(' ', padding) + "\n";
        var body = Float32Body(4f, 5f, 6f);

        var bytes = new byte[12 + header.Length + body.Length];
        bytes[0] = 0x93;
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 1);
        bytes[6] = 2;
        bytes[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)header.Length);
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 12);
        body.CopyTo(bytes, 12 + header.Length);

        Assert.Equal(new[] { 4f, 5f, 6f }, Read(bytes).Values);
    }

    // --- the refusals ------------------------------------------------------

    [Fact]
    public void SomethingThatIsNotAnNpyFileSaysSo()
    {
        Assert.Contains("not a .npy", Refusal(Encoding.ASCII.GetBytes("I am a PNG, honestly")));
        Assert.Contains("not a .npy", Refusal(Array.Empty<byte>()));
    }

    [Fact]
    public void AVersionThisDoesNotKnowIsRefusedRatherThanGuessedAt()
    {
        var bytes = Voice(new[] { 2 }, 1f, 2f);
        bytes[6] = 9;

        Assert.Contains("unsupported .npy version 9", Refusal(bytes));
    }

    [Fact]
    public void AHeaderLongerThanTheFileIsRefused()
    {
        var bytes = Voice(new[] { 2 }, 1f, 2f);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 60000);

        Assert.Contains("truncated", Refusal(bytes));
    }

    // A version-2 header length large enough to come back negative once it is
    // an int. Not a hypothetical: the field is unsigned four bytes wide and
    // the slice that reads the header would throw rather than refuse.
    [Fact]
    public void AVersionTwoHeaderLengthThatOverflowsAnIntIsRefused()
    {
        var bytes = new byte[32];
        bytes[0] = 0x93;
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 1);
        bytes[6] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0xFFFFFFFF);

        Assert.Contains("truncated", Refusal(bytes));
    }

    // A header dict written without numpy's trailing comma, so the last value
    // ends at the closing brace rather than at a comma. Real enough to be
    // worth reading: nothing but numpy's own writer guarantees that comma.
    [Fact]
    public void ADictWithNoTrailingCommaStillReadsItsLastValue()
    {
        var tensor = Read(File1Raw(
            "{'descr': '<f4', 'fortran_order': False, 'shape': (3,)}", Float32Body(1f, 2f, 3f)));

        Assert.Equal(new[] { 3 }, tensor.Shape);
    }

    // A key with no colon after it anywhere — which has to be the *last* key
    // to be reached at all, and that is the point. Written the obvious way,
    // `{'descr' '<f4', 'fortran_order': ...}`, this test passed while proving
    // nothing: the scan for a colon runs to the end of the header, found
    // fortran_order's, and read `False` as the dtype. The refusal message was
    // the one being asserted and the arm under test was never entered.
    [Theory]
    [InlineData("{'fortran_order': False, 'shape': (2,), 'descr'}")]     // no colon after it
    [InlineData("{'descr': , 'fortran_order': False, 'shape': (2,), }")] // a key with no value
    public void AKeyThisCannotReadAValueFromIsTheSameAsAMissingOne(string dictionary) =>
        Assert.Contains("dtype", Refusal(File1Raw(dictionary, Float32Body(1f, 2f))));

    [Fact]
    public void AVersionTwoFileTooShortToHoldItsOwnLengthIsRefused()
    {
        var bytes = new byte[11];
        bytes[0] = 0x93;
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 1);
        bytes[6] = 2;

        Assert.Contains("truncated", Refusal(bytes));
    }

    [Fact]
    public void ADtypeThisCannotDecodeIsRefusedByName()
    {
        Assert.Contains("<f8", Refusal(File1("<f8", false, new[] { 1 }, new byte[8])));
        Assert.Contains("dtype", Refusal(File1(">f4", false, new[] { 1 }, new byte[4])));
    }

    // Refused rather than transposed: reading one is a handful of lines and
    // getting it wrong is inaudible, and no voice file that exists is written
    // this way.
    [Fact]
    public void AFortranOrderedArrayIsRefused() =>
        Assert.Contains("Fortran", Refusal(File1("<f4", fortran: true, new[] { 2 }, Float32Body(1f, 2f))));

    [Fact]
    public void AHeaderMissingAKeyIsRefusedForThatKey()
    {
        var body = Float32Body(1f, 2f);

        Assert.Contains("dtype", Refusal(
            File1Raw("{'fortran_order': False, 'shape': (2,), }", body)));
        Assert.Contains("fortran_order", Refusal(
            File1Raw("{'descr': '<f4', 'shape': (2,), }", body)));
        Assert.Contains("shape", Refusal(
            File1Raw("{'descr': '<f4', 'fortran_order': False, }", body)));
    }

    [Theory]
    [InlineData("(x,)")]      // not a number
    [InlineData("(-2,)")]     // not a count
    [InlineData("2")]         // not a tuple
    [InlineData("()")]        // no dimensions at all
    public void AShapeThatIsNotATupleOfCountsIsRefused(string shape) =>
        Assert.Contains("shape", Refusal(File1Raw(
            $"{{'descr': '<f4', 'fortran_order': False, 'shape': {shape}, }}", Float32Body(1f, 2f))));

    // A corrupt header claiming an enormous shape is refused rather than
    // allocated. A voice is half a megabyte; the ceiling is four hundred times
    // that.
    [Fact]
    public void AShapeClaimingMoreElementsThanAnyVoiceHasIsRefused() =>
        Assert.Contains("is not a voice",
            Refusal(File1("<f4", false, new[] { NumpyVoices.MaxElements + 1 }, Array.Empty<byte>())));

    [Fact]
    public void AShapeWithAZeroInItIsRefused() =>
        Assert.Contains("is not a voice", Refusal(File1("<f4", false, new[] { 0 }, Array.Empty<byte>())));

    [Fact]
    public void ABodyThatIsNotTheLengthTheShapeClaimsIsRefused() =>
        Assert.Contains("not the length",
            Refusal(File1("<f4", false, new[] { 4 }, Float32Body(1f, 2f))));

    // --- averaging ----------------------------------------------------------

    [Fact]
    public void FiftyFiftyIsTheMidpointOfEveryElement()
    {
        var a = Read(Voice(new[] { 4 }, 0f, 1f, -2f, 10f));
        var b = Read(Voice(new[] { 4 }, 1f, 3f, 2f, 0f));

        var mixed = NumpyVoices.Average(new[] { (a, 0.5), (b, 0.5) }, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(new[] { 0.5f, 2f, 0f, 5f }, mixed!.Values);
        Assert.Equal(new[] { 4 }, mixed.Shape);
    }

    [Fact]
    public void AnUnevenSplitLeansTowardTheHeavierVoice()
    {
        var a = Read(Voice(new[] { 2 }, 0f, 100f));
        var b = Read(Voice(new[] { 2 }, 10f, 0f));

        var mixed = NumpyVoices.Average(new[] { (a, 0.75), (b, 0.25) }, out _);

        Assert.Equal(new[] { 2.5f, 75f }, mixed!.Values);
    }

    [Fact]
    public void FourEqualPartsAverageAllFour()
    {
        var parts = new[] { 4f, 8f, 12f, 16f }
            .Select(v => (Read(Voice(new[] { 1 }, v)), 0.25))
            .ToArray();

        Assert.Equal(new[] { 10f }, NumpyVoices.Average(parts, out _)!.Values);
    }

    // Two voices of different shapes are two different models' style vectors.
    // Averaging them has no meaning, and the engine would load the result and
    // speak nonsense.
    [Fact]
    public void TensorsOfDifferentShapesRefuseToAverage()
    {
        var a = Read(Voice(new[] { 2 }, 1f, 2f));
        var b = Read(Voice(new[] { 2, 1 }, 1f, 2f));

        Assert.Null(NumpyVoices.Average(new[] { (a, 0.5), (b, 0.5) }, out var refusal));
        Assert.Contains("different shapes", refusal);
    }

    [Fact]
    public void AveragingNothingIsRefusedRatherThanZero()
    {
        Assert.Null(NumpyVoices.Average(
            Array.Empty<(NumpyVoices.Tensor, double)>(), out var refusal));
        Assert.Contains("nothing to average", refusal);
    }

    // --- writing -------------------------------------------------------------

    // The round trip is the real assertion: what this writes is what this
    // reads, which is what makes a blend of a blend an ordinary voice rather
    // than a special case.
    [Fact]
    public void WhatIsWrittenIsWhatIsReadBack()
    {
        var original = Read(Voice(new[] { 510, 1, 2 }, Enumerable.Range(0, 1020).Select(i => i * 0.25f).ToArray()));

        var again = Read(NumpyVoices.Write(original));

        Assert.Equal(original.Shape, again.Shape);
        Assert.Equal(original.Values, again.Values);
    }

    // A one-dimensional tensor, written and read back. Its own case because
    // the shape a 1-D array is written with needs Python's trailing comma —
    // `(3,)` is a tuple and `(3)` is the number three — and the round trip is
    // the only thing that can catch it, since this reader is forgiving of a
    // shape it would itself never write.
    [Fact]
    public void AOneDimensionalTensorSurvivesTheRoundTripWithItsTrailingComma()
    {
        var original = Read(Voice(new[] { 3 }, 1f, 2f, 3f));

        var bytes = NumpyVoices.Write(original);
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        Assert.Contains("'shape': (3,)", Encoding.ASCII.GetString(bytes, 10, headerLength));

        var again = Read(bytes);
        Assert.Equal(new[] { 3 }, again.Shape);
        Assert.Equal(new[] { 1f, 2f, 3f }, again.Values);
    }

    // ...and it is written the way numpy writes one, which is what makes the
    // engine load it beside its own bundled voices rather than merely tolerate
    // it. Measured against `af_sky.npy`: a 128-byte prelude, version 1.0, and
    // a header ending on a 64-byte boundary.
    [Fact]
    public void AWrittenFileHasNumpysOwnPreludeAndAlignment()
    {
        var bytes = NumpyVoices.Write(Read(Voice(new[] { 510, 1, 256 }, new float[510 * 256])));

        Assert.Equal(new byte[] { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' }, bytes[..6]);
        Assert.Equal(1, bytes[6]);
        Assert.Equal(0, bytes[7]);

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        Assert.Equal(0, (10 + headerLength) % 64);

        // The same prelude and the same body length as the real voice files on
        // this machine, to the byte.
        Assert.Equal(128, 10 + headerLength);
        Assert.Equal(522_368, bytes.Length);

        var header = Encoding.ASCII.GetString(bytes, 10, headerLength);
        Assert.Contains("'descr': '<f4'", header);
        Assert.Contains("'fortran_order': False", header);
        Assert.Contains("'shape': (510, 1, 256)", header);
        Assert.EndsWith("\n", header);
    }
}
