using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ClaudeBuddy
{
    // The one file format a Kokoro voice is: a NumPy `.npy` array of style
    // vectors, `(510, 1, 256)` little-endian float32 on every voice this
    // engine ships. Read, averaged and written back here so the app can build
    // a blended voice without asking the engine for anything — see VoiceBlends,
    // which is the only caller.
    //
    // Pure over bytes, deliberately and for two reasons. The first is the usual
    // one: a format nobody here controls gets a parser with a case per arm,
    // which CLAUDE.md asks for by name. The second is sharper — a wrong answer
    // here is not an exception, it is a voice. Mis-read the header by two bytes
    // and every float is shifted; treat a Fortran-ordered array as C-ordered
    // and the average is a transposed smear. Both produce a file the engine
    // loads happily and speaks in a voice nobody chose, and neither can be
    // caught by looking at it. So every shape this does not positively
    // understand is refused by name rather than guessed at.
    //
    // What the format actually is, since the refusals below only make sense
    // against it: six magic bytes `\x93NUMPY`, a major and a minor version
    // byte, then a header length — two bytes for version 1, four for versions
    // 2 and 3 — then that many bytes of a Python dict literal, then the raw
    // elements. The dict carries `descr` (the dtype), `fortran_order` and
    // `shape`, and the whole prelude is padded so the data starts on a 64-byte
    // boundary. Measured against the real files rather than taken from the
    // spec: `af_sky.npy` on this machine is 522,368 bytes, a 128-byte prelude
    // over 130,560 float32s, and `descr` reads `'<f4'`.
    internal static class NumpyVoices
    {
        // `\x93NUMPY`, as bytes rather than as a string: the first byte is not
        // ASCII and a source file that spells it in a char literal is a source
        // file whose encoding matters.
        private static readonly byte[] Magic = { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };

        // numpy's own alignment rule, and the reason a written file is
        // byte-identical in shape to a bundled one rather than merely readable.
        private const int Alignment = 64;

        // A bound on what will be read into memory at all. A voice is half a
        // megabyte; sixteen is four hundred times the largest thing this is
        // ever handed, and it is here so a corrupt header claiming a shape of
        // (2^31, 1, 256) is refused rather than allocated.
        internal const int MaxElements = 4 * 1024 * 1024;

        // A voice tensor: its shape, and its elements in C order. Float rather
        // than Half or double throughout, because float32 is what every real
        // voice file holds and what the engine reads back.
        internal sealed record Tensor(IReadOnlyList<int> Shape, float[] Values);

        // The dtypes this understands, and only these. `<f2` is included on
        // purpose even though no voice on this machine uses it: a future model
        // shipping half-precision packs would otherwise be read as float32,
        // which is not an error — it is garbage of exactly the right length,
        // and the engine would speak it.
        private const string Float32 = "<f4";
        private const string Float16 = "<f2";

        // Null with a reason rather than an exception. Every caller here is on
        // a path whose worst allowed outcome is "speak in the user's own
        // voice", and a reason is what turns that from silence into a line in
        // persona.log.
        internal static Tensor? Read(byte[] bytes, out string? refusal)
        {
            refusal = null;

            if (bytes.Length < 10 || !bytes.AsSpan(0, 6).SequenceEqual(Magic))
            {
                refusal = "not a .npy file";
                return null;
            }

            var major = bytes[6];

            // Version 1 writes a two-byte little-endian header length; 2 and 3
            // widened it to four. Nothing else exists, and a version this does
            // not know is a format this cannot claim to have read.
            int headerLength, headerStart;
            if (major == 1)
            {
                headerStart = 10;
                headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
            }
            else if (major is 2 or 3)
            {
                if (bytes.Length < 12) { refusal = "truncated .npy header"; return null; }
                headerStart = 12;
                headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            }
            else
            {
                refusal = $"unsupported .npy version {major}";
                return null;
            }

            if (headerLength < 0 || headerStart + headerLength > bytes.Length)
            {
                refusal = "truncated .npy header";
                return null;
            }

            var header = Encoding.ASCII.GetString(bytes, headerStart, headerLength);

            var descr = DictValue(header, "descr");
            if (descr is not (Float32 or Float16))
            {
                refusal = $"unsupported .npy dtype {descr ?? "(missing)"}";
                return null;
            }

            var order = DictValue(header, "fortran_order");
            if (order is null)
            {
                refusal = "no fortran_order in the .npy header";
                return null;
            }

            // Refused rather than transposed. Reading one is a handful of
            // lines; getting it wrong is inaudible, and no voice file that
            // exists is written this way — so the arm that would exercise the
            // transpose would only ever be exercised by its own test.
            if (!order.Equals("False", StringComparison.Ordinal))
            {
                refusal = "a Fortran-ordered .npy is not a voice this can blend";
                return null;
            }

            var shape = Shape(header);
            if (shape is null)
            {
                refusal = "no shape in the .npy header";
                return null;
            }

            long count = 1;
            foreach (var dimension in shape) count *= dimension;

            if (count is <= 0 or > MaxElements)
            {
                refusal = $"a .npy of {count} elements is not a voice";
                return null;
            }

            var itemSize = descr == Float32 ? 4 : 2;
            var dataStart = headerStart + headerLength;
            if (bytes.Length - dataStart != count * itemSize)
            {
                refusal = "the .npy body is not the length its shape claims";
                return null;
            }

            var values = new float[count];
            var data = bytes.AsSpan(dataStart);

            for (var i = 0; i < count; i++)
            {
                values[i] = descr == Float32
                    ? BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4))
                    : (float)BinaryPrimitives.ReadHalfLittleEndian(data.Slice(i * 2, 2));
            }

            return new Tensor(shape, values);
        }

        // The weighted average, which is the whole of what a blend *is*.
        //
        // Summed in double and written back as float32. The inputs are float32
        // and so is the output, so this buys nothing in the two-part case — it
        // is there for four parts, where accumulating a quarter of each of four
        // half-million-element arrays in single precision loses low bits for no
        // reason. The cost is one array of doubles that lives for the length of
        // this call.
        //
        // Shapes must agree exactly. Two voices of different shapes are two
        // different models' style vectors, and there is no meaning to averaging
        // them — the engine would load the result and speak nonsense.
        internal static Tensor? Average(
            IReadOnlyList<(Tensor Tensor, double Weight)> parts, out string? refusal)
        {
            refusal = null;

            if (parts.Count == 0)
            {
                refusal = "nothing to average";
                return null;
            }

            var shape = parts[0].Tensor.Shape;

            foreach (var (tensor, _) in parts)
            {
                if (!tensor.Shape.SequenceEqual(shape))
                {
                    refusal = $"voice tensors of different shapes ({Describe(shape)} and {Describe(tensor.Shape)})";
                    return null;
                }
            }

            var total = new double[parts[0].Tensor.Values.Length];

            foreach (var (tensor, weight) in parts)
            {
                var values = tensor.Values;
                for (var i = 0; i < total.Length; i++) total[i] += values[i] * weight;
            }

            var averaged = new float[total.Length];
            for (var i = 0; i < total.Length; i++) averaged[i] = (float)total[i];

            return new Tensor(shape, averaged);
        }

        // A version 1.0 file, float32, C order, padded the way numpy pads —
        // which is what makes a written blend indistinguishable in shape from
        // a bundled voice rather than merely something the engine tolerates.
        internal static byte[] Write(Tensor tensor)
        {
            var dictionary =
                "{'descr': '" + Float32 + "', 'fortran_order': False, 'shape': (" +
                string.Join(", ", tensor.Shape.Select(d => d.ToString(CultureInfo.InvariantCulture))) +
                (tensor.Shape.Count == 1 ? "," : "") +
                "), }";

            // The trailing newline is part of the header numpy writes, and the
            // padding goes in front of it so the header still reads as one
            // line. `10 +` is the magic, the version pair and the two-byte
            // length; the whole prelude ends on a 64-byte boundary.
            var padding = (Alignment - ((10 + dictionary.Length + 1) % Alignment)) % Alignment;
            var header = dictionary + new string(' ', padding) + "\n";

            var bytes = new byte[10 + header.Length + tensor.Values.Length * 4];

            Magic.CopyTo(bytes, 0);
            bytes[6] = 1;
            bytes[7] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)header.Length);
            Encoding.ASCII.GetBytes(header, 0, header.Length, bytes, 10);

            var body = bytes.AsSpan(10 + header.Length);
            for (var i = 0; i < tensor.Values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(body.Slice(i * 4, 4), tensor.Values[i]);
            }

            return bytes;
        }

        private static string Describe(IReadOnlyList<int> shape) =>
            "(" + string.Join(", ", shape) + ")";

        // One value out of the header's Python dict literal, by key.
        //
        // Not a Python parser and deliberately not a regex either: the header
        // is written by numpy to a fixed shape, the three keys are known, and
        // the values are a quoted string, a bare `True`/`False` and a tuple.
        // Reading to the next comma-at-depth-zero handles all three without
        // knowing anything about Python, and anything it cannot find is a
        // refusal rather than a default.
        private static string? DictValue(string header, string key)
        {
            var marker = "'" + key + "'";
            var at = header.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return null;

            var colon = header.IndexOf(':', at + marker.Length);
            if (colon < 0) return null;

            var depth = 0;
            var start = colon + 1;
            var end = start;

            for (; end < header.Length; end++)
            {
                var c = header[end];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ',' && depth == 0) break;

                // The closing brace of the dict itself, met at depth -1.
                if (depth < 0) break;
            }

            var value = header[start..end].Trim();
            return value.Length == 0 ? null : value.Trim('\'');
        }

        // `'shape': (510, 1, 256)` — the tuple, as dimensions. A trailing comma
        // is legal Python for a one-tuple and numpy writes one, so empty
        // fragments are dropped rather than read as a zero dimension.
        private static IReadOnlyList<int>? Shape(string header)
        {
            var value = DictValue(header, "shape");
            if (value is null) return null;

            var inside = value.Trim();
            if (!inside.StartsWith('(') || !inside.EndsWith(')')) return null;

            var dimensions = new List<int>();

            foreach (var part in inside[1..^1].Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.Length == 0) continue;
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var dimension)) return null;
                dimensions.Add(dimension);
            }

            return dimensions.Count == 0 ? null : dimensions;
        }
    }
}
