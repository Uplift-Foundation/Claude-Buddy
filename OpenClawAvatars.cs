using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ClaudeBuddy
{
    // Agent pictures, decoded once and kept at the size an orb actually draws
    // them.
    //
    // The gateway hands these over inside agents.list as base64 data URIs, at
    // whatever size their author made them — 512 to 1024 pixels, about 8 MB
    // across seven agents. An orb is 36 points across, so almost all of that is
    // thrown away immediately; what is kept is a handful of small frames.
    //
    // SkiaSharp rather than Avalonia's own Bitmap because some of these are
    // animated GIFs and Bitmap gives you frame one and no way to ask for the
    // rest. It is not a new dependency: Avalonia's renderer is Skia, so the
    // native library is already inside the bundle and already signed — which is
    // the same packaging question that decided the Ed25519 library earlier, and
    // here it was already answered.
    public static class OpenClawAvatars
    {
        // Sized for the largest place these are drawn — the 68pt portrait in the
        // chat panel header — at 2x for Retina. The orb draws the same frames at
        // 36pt and simply downsamples.
        //
        // Measured rather than guessed: the one animated avatar here is 24
        // frames, so this costs about 1.9 MB for it and 81 KB for each still
        // one. Decoding at orb size instead would save that and make the
        // portrait soft, which is the wrong way round — you look at the
        // portrait and glance at the orb.
        private const int Size = 144;

        // A still image is one frame with no duration.
        public sealed record Avatar(IReadOnlyList<Bitmap> Frames, IReadOnlyList<int> DelaysMs)
        {
            public bool IsAnimated => Frames.Count > 1;

            public int TotalMs => DelaysMs.Sum();
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Avatar?> Cache = new(StringComparer.OrdinalIgnoreCase);

        // Null when this agent has no picture, or when its picture couldn't be
        // read — both mean "fall back to the emoji", and neither is worth
        // failing over.
        public static Avatar? For(string agentId, byte[]? bytes)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(agentId, out var cached)) return cached;
            }

            var decoded = bytes is null || bytes.Length == 0 ? null : Decode(bytes);

            lock (Gate)
            {
                Cache[agentId] = decoded;
                return decoded;
            }
        }

        public static void Forget(string agentId)
        {
            lock (Gate) Cache.Remove(agentId);
        }

        // One picture cut from several, for an orb that stands for a
        // conversation rather than for an agent.
        //
        // A part is one member of the room: their picture if they have one, and
        // the colour their ring already wears if they do not. A member with no
        // picture still gets a wedge — leaving them out would draw a room of
        // three as a room of two, and the colour is the same answer to "which
        // agent" that the ring gives everywhere else in the app.
        //
        // Null when nothing here can be drawn: no parts at all, or not one
        // picture between them. The second is deliberate rather than a
        // fallthrough — a pie made entirely of flat colours says nothing the
        // room's own ring does not already say, and the channel's initials are
        // more use than a colour wheel.
        //
        // Static, taking each picture's first frame. An animated avatar keeps
        // animating on its own orb; a composite of four independent frame
        // timelines would have to be re-rendered on the fastest of them, and
        // that is a Skia pass per tick per room for motion nobody can read in a
        // quarter of an orb.
        public sealed record Part(byte[]? Picture, string Colour);

        private static readonly Dictionary<string, Avatar?> Composites =
            new(StringComparer.Ordinal);

        // cacheKey identifies the *set* — who is in it and what colour they are
        // — so a membership change is a different key rather than a stale hit.
        // The scan hands the same set back a couple of times a second, and
        // re-cutting the pie each time would rebuild the orb's brush on every
        // tick.
        public static Avatar? Composite(string cacheKey, IReadOnlyList<Part> parts)
        {
            lock (Gate)
            {
                if (Composites.TryGetValue(cacheKey, out var cached)) return cached;
            }

            var drawn = Draw(parts);

            lock (Gate)
            {
                Composites[cacheKey] = drawn;
                return drawn;
            }
        }

        private static Avatar? Draw(IReadOnlyList<Part> parts)
        {
            using var rendered = Render(parts);
            return rendered is null ? null : new Avatar(new[] { ToBitmap(rendered) }, new[] { 0 });
        }

        // The pie itself, as pixels and nothing else.
        //
        // Split from Draw above so it can be checked: everything interesting
        // here — which wedge a portrait lands in, whether a member with no
        // picture gets their colour, where the seams fall — is a question about
        // pixels, and asking it of an Avalonia Bitmap means a windowing
        // toolkit for arithmetic that has none in it. An SKBitmap answers
        // GetPixel in a plain unit test.
        internal static SKBitmap? Render(IReadOnlyList<Part> parts)
        {
            if (parts.Count == 0) return null;

            var count = Math.Min(parts.Count, AvatarPie.MaxParts);

            var pictures = new SKBitmap?[count];
            var any = false;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var bytes = parts[i].Picture;
                    pictures[i] = bytes is null || bytes.Length == 0 ? null : DecodeStill(bytes);
                    any |= pictures[i] is not null;
                }

                if (!any) return null;

                var info = new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
                var canvas = new SKBitmap(info);

                using (var surface = new SKCanvas(canvas))
                {
                    surface.Clear(SKColors.Transparent);

                    for (var i = 0; i < count; i++)
                    {
                        surface.Save();

                        using (var wedge = Wedge(i, count))
                        {
                            surface.ClipPath(wedge, SKClipOperation.Intersect, antialias: true);
                        }

                        var picture = pictures[i];
                        if (picture is not null) Cover(surface, picture, i, count);
                        else surface.Clear(Parse(parts[i].Colour));

                        surface.Restore();
                    }

                    Divide(surface, count);
                }

                return canvas;
            }
            finally
            {
                foreach (var picture in pictures) picture?.Dispose();
            }
        }

        // Cut past the edge of the circle rather than up to it, so the seam
        // between two wedges is a straight cut all the way out and does not
        // stop a pixel short of the rim. Nothing outside the circle is ever
        // seen — an orb draws this as the fill of an ellipse inscribed in the
        // frame — which is also why the layout rectangle below uses the real
        // circle instead: that is where the picture has to end up, and each
        // wedge's rectangle contains the whole of its sector of it.
        private static SKPath Wedge(int index, int count)
        {
            var path = new SKPath();

            if (count <= 1)
            {
                path.AddRect(new SKRect(0, 0, Size, Size));
                return path;
            }

            var (start, sweep) = AvatarPie.Angles(index, count);

            var half = Size / 2f;
            var reach = (float)Size;   // past the corners, which are half*sqrt(2) away
            var oval = new SKRect(half - reach, half - reach, half + reach, half + reach);

            path.MoveTo(half, half);
            path.ArcTo(oval, (float)start, (float)sweep, false);
            path.Close();

            return path;
        }

        // The picture, scaled to cover its wedge's rectangle and centred in it —
        // cropped rather than squashed, which is what an orb does with a single
        // avatar too (Stretch.UniformToFill) and the only way two portraits of
        // different shapes end up looking like they belong together.
        private static void Cover(SKCanvas surface, SKBitmap picture, int index, int count)
        {
            var (x, y, width, height) = AvatarPie.Bounds(index, count, Size);

            var scale = Math.Max(width / picture.Width, height / picture.Height);
            var drawnWidth = (float)(picture.Width * scale);
            var drawnHeight = (float)(picture.Height * scale);

            var centreX = (float)(x + width / 2);
            var centreY = (float)(y + height / 2);

            var destination = new SKRect(
                centreX - drawnWidth / 2,
                centreY - drawnHeight / 2,
                centreX + drawnWidth / 2,
                centreY + drawnHeight / 2);

            using var paint = new SKPaint { IsAntialias = true };
            using var image = SKImage.FromBitmap(picture);

            // Through SKImage rather than DrawBitmap, which has no overload
            // taking sampling options — and the default sampling on a portrait
            // scaled down by five or ten is visibly gritty.
            surface.DrawImage(
                image, destination, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }

        // A seam between the wedges. Two dark portraits meeting with nothing
        // between them read as one picture that has gone wrong; a line says the
        // division is deliberate. Translucent black rather than the background
        // colour, because the orb is drawn over whatever is behind it and there
        // is no background colour to match.
        private static void Divide(SKCanvas surface, int count)
        {
            if (count <= 1) return;

            using var paint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 0x66),
                StrokeWidth = Size / 36f,   // one point at the 36pt an orb is drawn
                IsStroke = true,
                IsAntialias = true,
            };

            var half = Size / 2f;

            for (var i = 0; i < count; i++)
            {
                var (start, _) = AvatarPie.Angles(i, count);
                var radians = start * Math.PI / 180;

                surface.DrawLine(
                    half,
                    half,
                    half + (float)(Size * Math.Cos(radians)),
                    half + (float)(Size * Math.Sin(radians)),
                    paint);
            }
        }

        // An agent's palette colour, which is a "#RRGGBB" string by the time it
        // reaches here. Grey when it is missing or unparseable, which is what an
        // agent with no colour assigned yet looks like — a wedge that is plainly
        // somebody, without claiming to be a particular somebody.
        private static SKColor Parse(string? hex) =>
            !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var colour)
                ? colour
                : new SKColor(0x55, 0x55, 0x55, 0xFF);

        // The first frame only. SKBitmap.Decode gives exactly that for an
        // animated GIF, which is the whole of what a composite wants — the
        // frame walking in DecodeFrames above exists to animate one orb, and
        // there is nothing here to animate.
        private static SKBitmap? DecodeStill(byte[] bytes)
        {
            try { return SKBitmap.Decode(bytes); }
            catch { return null; }
        }

        // Excluded from coverage: exists to be the try/catch, and the catch is
        // for Skia rather than for anything this code decides. Bytes that are not
        // a picture at all, and a picture truncated partway through, both come
        // back as a null codec or an empty frame list — DecodeFrames handles
        // those and is measured, and OpenClawAvatarsTests asserts both.
        //
        // What is left is a header Skia accepts and then throws on, which cannot
        // be produced from inside a test without shipping a corrupt file as a
        // fixture. Kept because a picture that won't decode is not a reason to
        // lose the orb — falling back to the emoji is the whole contract of this
        // class.
        [ExcludeFromCodeCoverage]
        private static Avatar? Decode(byte[] bytes)
        {
            try { return DecodeFrames(bytes); }
            catch { return null; }
        }

        private static Avatar? DecodeFrames(byte[] bytes)
        {
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec is null) return null;

                var frames = new List<Bitmap>();
                var delays = new List<int>();

                var count = Math.Max(1, codec.FrameCount);

                // A guard rather than a real limit: nobody's avatar needs three
                // hundred frames, and a corrupt header claiming it shouldn't be
                // able to spend all afternoon in here.
                count = Math.Min(count, 120);

                var info = new SKImageInfo(
                    codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

                // GIF frames are often deltas against the one before, so the
                // canvas is reused and each frame is composited onto it rather
                // than decoded standalone.
                using var canvas = new SKBitmap(info);

                for (var i = 0; i < count; i++)
                {
                    var options = new SKCodecOptions(i);

                    if (codec.FrameCount > 0 && i > 0)
                    {
                        var required = codec.FrameInfo[i].RequiredFrame;
                        if (required >= 0) options = new SKCodecOptions(i, required);
                    }

                    var result = codec.GetPixels(info, canvas.GetPixels(), info.RowBytes, options);
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) break;

                    frames.Add(ToBitmap(canvas));

                    var duration = codec.FrameCount > 0 ? codec.FrameInfo[i].Duration : 0;

                    // Browsers treat 0 and 10ms as "the author meant 100ms", and
                    // a great many GIFs rely on that. Without it Zara spins.
                    delays.Add(duration <= 10 ? 100 : duration);
                }

                return frames.Count == 0 ? null : new Avatar(frames, delays);
            }
        }

        // Scaled down on the way in, so what is retained is 72x72 rather than
        // the megabyte it arrived as.
        private static Bitmap ToBitmap(SKBitmap source)
        {
            var info = new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var scaled = source.Resize(info, SKFilterQuality.High);

            var target = scaled ?? source;

            var bitmap = new WriteableBitmap(
                new PixelSize(target.Width, target.Height),
                new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var buffer = bitmap.Lock())
            {
                var bytes = target.Bytes;
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer.Address, bytes.Length);
            }

            return bitmap;
        }
    }
}
