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

        private static Avatar? Decode(byte[] bytes)
        {
            try
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
            catch
            {
                // A picture that won't decode is not a reason to lose the orb.
                return null;
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
