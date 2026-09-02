using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests
{
    // The pie itself: several agents' pictures cut into one orb.
    //
    // Asked of OpenClawAvatars.Render rather than of the orb, because every
    // question here is about pixels — which wedge a portrait landed in, whether
    // a member with no picture got their colour, whether a fifth member got in.
    // Render exists split out of Draw for exactly this: an SKBitmap answers
    // GetPixel with no windowing toolkit anywhere near it.
    //
    // The pictures below are solid colours rather than portraits, so a pixel
    // sampled well inside a wedge names the member who owns it. Sampling stays
    // away from the seams, which are antialiased and are nobody's colour.
    public class AvatarCompositeTests
    {
        // 144, the size the composite is cut at — see OpenClawAvatars.Size.
        private const int Size = 144;

        private static byte[] Picture(byte r, byte g, byte b)
        {
            var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            bitmap.Erase(new SKColor(r, g, b, 0xFF));

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            return data.ToArray();
        }

        private static readonly byte[] Red = Picture(0xE0, 0x20, 0x20);
        private static readonly byte[] Blue = Picture(0x20, 0x20, 0xE0);
        private static readonly byte[] Green = Picture(0x20, 0xE0, 0x20);
        private static readonly byte[] Yellow = Picture(0xE0, 0xE0, 0x20);

        private static OpenClawAvatars.Part Part(byte[]? picture, string colour = "") =>
            new(picture, colour);

        private static void Same(SKColor expected, SKColor actual, string where) =>
            Assert.True(
                Math.Abs(expected.Red - actual.Red) < 12
                && Math.Abs(expected.Green - actual.Green) < 12
                && Math.Abs(expected.Blue - actual.Blue) < 12,
                $"{where}: expected {expected}, got {actual}");

        // --- what gets drawn ------------------------------------------------

        // The 50/50 the feature was asked for. The first member takes the right
        // half because the wedges start at twelve o'clock and run clockwise.
        [Fact]
        public void TwoMembersTakeAHalfOfTheOrbEach()
        {
            using var pie = OpenClawAvatars.Render(new[] { Part(Red), Part(Blue) });

            Assert.NotNull(pie);

            Same(new SKColor(0xE0, 0x20, 0x20), pie!.GetPixel(Size * 3 / 4, Size / 2), "the right half");
            Same(new SKColor(0x20, 0x20, 0xE0), pie.GetPixel(Size / 4, Size / 2), "the left half");
        }

        // Four is quadrants, clockwise from the top right.
        [Fact]
        public void FourMembersTakeAQuadrantOfTheOrbEach()
        {
            using var pie = OpenClawAvatars.Render(
                new[] { Part(Red), Part(Blue), Part(Green), Part(Yellow) });

            Assert.NotNull(pie);

            Same(new SKColor(0xE0, 0x20, 0x20), pie!.GetPixel(Size * 3 / 4, Size / 4), "top right");
            Same(new SKColor(0x20, 0x20, 0xE0), pie.GetPixel(Size * 3 / 4, Size * 3 / 4), "bottom right");
            Same(new SKColor(0x20, 0xE0, 0x20), pie.GetPixel(Size / 4, Size * 3 / 4), "bottom left");
            Same(new SKColor(0xE0, 0xE0, 0x20), pie.GetPixel(Size / 4, Size / 4), "top left");
        }

        // A member with no picture is still a member. Their wedge is the colour
        // their ring already wears, so the division counts them rather than
        // handing their share to whoever does have a picture.
        [Fact]
        public void AMemberWithNoPictureGetsTheirColour()
        {
            using var pie = OpenClawAvatars.Render(
                new[] { Part(null, "#3366CC"), Part(Red) });

            Assert.NotNull(pie);

            Same(new SKColor(0x33, 0x66, 0xCC), pie!.GetPixel(Size * 3 / 4, Size / 2), "the colour wedge");
            Same(new SKColor(0xE0, 0x20, 0x20), pie.GetPixel(Size / 4, Size / 2), "the picture wedge");
        }

        // An agent the palette has not reached yet — the ordinary state for the
        // first poll after a gateway connects. Grey is plainly somebody without
        // claiming to be a particular somebody.
        [Fact]
        public void AMemberWithNoColourEitherGetsGrey()
        {
            using var pie = OpenClawAvatars.Render(new[] { Part(null), Part(Red) });

            Assert.NotNull(pie);
            Same(new SKColor(0x55, 0x55, 0x55), pie!.GetPixel(Size * 3 / 4, Size / 2), "the grey wedge");
        }

        // A colour that is not a colour is the same answer as no colour. The
        // palette only ever produces "#RRGGBB", so this is about the string
        // arriving from somewhere else entirely rather than about the palette
        // being wrong — and a wedge of nothing would be a hole in the orb.
        [Fact]
        public void AColourThatWillNotParseGetsGreyToo()
        {
            using var pie = OpenClawAvatars.Render(new[] { Part(null, "not a colour"), Part(Red) });

            Assert.NotNull(pie);
            Same(new SKColor(0x55, 0x55, 0x55), pie!.GetPixel(Size * 3 / 4, Size / 2), "the grey wedge");
        }

        // Cut at the size the chat panel's portrait needs, and downsampled to
        // 36pt by the orb — the same bargain a single avatar already makes.
        [Fact]
        public void TheCompositeIsCutAtPortraitSize()
        {
            using var pie = OpenClawAvatars.Render(new[] { Part(Red), Part(Blue) });

            Assert.NotNull(pie);
            Assert.Equal(Size, pie!.Width);
            Assert.Equal(Size, pie.Height);
        }

        // Nothing inside the orb is left uncovered — including with three
        // wedges, where the rectangles the pictures are fitted to do not tile
        // the square and only their sectors of the circle meet.
        //
        // The circle is what matters and the corners are not: an orb draws this
        // as the fill of an ellipse inscribed in the frame, so everything
        // outside that circle is clipped away before anyone sees it. A gap
        // *inside* it would read as a lighter wedge of the ring.
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void EveryPixelOfTheCircleIsPainted(int members)
        {
            var parts = new[] { Part(Red), Part(Blue), Part(Green), Part(Yellow) }
                .Take(members)
                .ToArray();

            using var pie = OpenClawAvatars.Render(parts);

            Assert.NotNull(pie);

            const double Radius = Size / 2.0;

            for (var x = 0; x < Size; x += 2)
            {
                for (var y = 0; y < Size; y += 2)
                {
                    var dx = x + 0.5 - Radius;
                    var dy = y + 0.5 - Radius;

                    // A pixel-wide margin inside the rim, which is where the
                    // ellipse's own antialiasing is and is nobody's to fill.
                    if (dx * dx + dy * dy > (Radius - 1.5) * (Radius - 1.5)) continue;

                    Assert.True(pie!.GetPixel(x, y).Alpha > 0,
                        $"({x}, {y}) was left transparent with {members} members");
                }
            }
        }

        // One picture is not a division of anything: it fills the frame, corners
        // and all, with no seam drawn across it.
        //
        // A room never asks for this — a channel with one member hands back that
        // member's own avatar instead, which keeps whatever animation it has —
        // but Render is what decides that a single part is a picture rather than
        // a wedge with three quarters of nothing beside it, and that decision is
        // worth holding still.
        [Fact]
        public void OneMemberFillsTheWholeOrb()
        {
            using var pie = OpenClawAvatars.Render(new[] { Part(Red) });

            Assert.NotNull(pie);

            Same(new SKColor(0xE0, 0x20, 0x20), pie!.GetPixel(Size / 2, Size / 2), "the middle");
            Same(new SKColor(0xE0, 0x20, 0x20), pie.GetPixel(1, 1), "the top left corner");
            Same(new SKColor(0xE0, 0x20, 0x20), pie.GetPixel(Size - 2, Size - 2), "the bottom right corner");
        }

        // --- what does not get drawn ----------------------------------------

        // Four wedges, however many are in the channel. A fifth is about seven
        // points of face on a 36pt orb, which is a smudge — see AvatarPie.
        [Fact]
        public void OnlyTheFirstFourMembersGetAWedge()
        {
            using var pie = OpenClawAvatars.Render(new[]
            {
                Part(Red), Part(Red), Part(Red), Part(Red),
                Part(Blue), Part(Blue),
            });

            Assert.NotNull(pie);

            for (var x = 0; x < Size; x += 4)
            {
                for (var y = 0; y < Size; y += 4)
                {
                    var pixel = pie!.GetPixel(x, y);
                    Assert.True(pixel.Blue < 0x90,
                        $"a fifth member reached ({x}, {y}): {pixel}");
                }
            }
        }

        // Not one picture between them is not worth drawing: a pie of flat
        // colours says nothing the room's own ring already says, and null here
        // is what sends the orb back to the channel's initials.
        [Fact]
        public void AllColourAndNoPictureDrawsNothing()
        {
            using var pie = OpenClawAvatars.Render(
                new[] { Part(null, "#3366CC"), Part(null, "#CC6633") });

            Assert.Null(pie);
        }

        [Fact]
        public void NoMembersAtAllDrawsNothing()
        {
            using var pie = OpenClawAvatars.Render(Array.Empty<OpenClawAvatars.Part>());

            Assert.Null(pie);
        }

        // Bytes that are not a picture are the same answer as no picture: the
        // member keeps their wedge in their colour, and the composite survives.
        // A gateway hands these over as base64 inside agents.list, so a
        // truncated one is a transport failure rather than anything this code
        // did.
        [Fact]
        public void AnUnreadablePictureFallsBackToTheColour()
        {
            using var pie = OpenClawAvatars.Render(new[]
            {
                Part(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "#3366CC"),
                Part(Red),
            });

            Assert.NotNull(pie);
            Same(new SKColor(0x33, 0x66, 0xCC), pie!.GetPixel(Size * 3 / 4, Size / 2), "the unreadable wedge");
        }

        // An empty byte array reaches here from an agent whose avatar field was
        // present and blank, which is different from the field being absent and
        // has to mean the same thing.
        [Fact]
        public void AnEmptyPictureFallsBackToTheColourToo()
        {
            using var pie = OpenClawAvatars.Render(new[]
            {
                Part(Array.Empty<byte>(), "#3366CC"),
                Part(Red),
            });

            Assert.NotNull(pie);
            Same(new SKColor(0x33, 0x66, 0xCC), pie!.GetPixel(Size * 3 / 4, Size / 2), "the empty wedge");
        }

        // --- the seams ------------------------------------------------------

        // A line between the wedges, so two dark portraits meeting do not read
        // as one picture that has gone wrong. Checked as "darker than either
        // neighbour" rather than as a colour, since it is translucent black over
        // whatever is underneath.
        [Fact]
        public void ASeamSeparatesTheWedges()
        {
            using var pie = OpenClawAvatars.Render(new[] { Part(Red), Part(Blue) });

            Assert.NotNull(pie);

            var seam = pie!.GetPixel(Size / 2, Size / 4);
            var right = pie.GetPixel(Size * 3 / 4, Size / 4);

            Assert.True(seam.Red < right.Red, $"the seam ({seam}) is no darker than the wedge ({right})");
        }
    }
}
