namespace ClaudeBuddy
{
    // How a circle is divided between several pictures.
    //
    // A room orb stands for a conversation rather than for one agent, so the
    // one thing it can usefully wear is everyone who is in it. That means
    // cutting the circle into wedges and giving each member one — and the
    // question of *where* a wedge is, and which rectangle of the picture ends
    // up inside it, is arithmetic with no bitmap in it at all.
    //
    // Pure and double-valued for the same reason OrbArrangement is: the mistake
    // it can make is a face cropped to an ear, or a wedge that starts somewhere
    // other than where the one before it ended, and neither is visible from
    // reading the code that calls it. OpenClawAvatars does the drawing and this
    // decides the shape.
    public static class AvatarPie
    {
        // How many pictures a composite is allowed to hold.
        //
        // Not a technical limit — the maths below works for any count. It is
        // that an orb is 36 points across, so a fifth of it is about seven
        // points of face, and seven points of face is a smudge. Four is where
        // a picture still reads as a picture, and it is what every other app
        // that draws a group portrait settles on for the same reason.
        public const int MaxParts = 4;

        // Where a wedge starts and how far it goes, in degrees, in the
        // convention Skia's arcs use: zero at three o'clock, increasing
        // clockwise because y grows downward.
        //
        // Started at twelve o'clock rather than at three, so that two members
        // split the orb down the middle rather than across it. A left/right
        // split is the one everybody recognises as two people; a top/bottom one
        // reads as a single picture with a band through it.
        public static (double Start, double Sweep) Angles(int index, int count)
        {
            if (count <= 0) return (-90, 360);

            var sweep = 360.0 / count;
            return (-90 + index * sweep, sweep);
        }

        // The smallest rectangle containing a wedge, inside a square of the
        // given size.
        //
        // This is what the picture is fitted to, rather than the whole square,
        // and that is the difference between seeing half of somebody's face and
        // seeing their face. Fitted to the whole square, a 50/50 split shows the
        // left half of one portrait beside the right half of another — two half
        // faces, each of which may be an ear. Fitted to the wedge's own
        // rectangle, each portrait is centred in the space it actually occupies
        // and is merely cropped narrower.
        //
        // Corners are included where the wedge reaches one, which is why the
        // cardinal directions below are checked rather than only the two edges:
        // a quadrant's rectangle is a quarter of the square, and its widest
        // point is the arc bulging out to three o'clock, not either straight
        // edge.
        public static (double X, double Y, double Width, double Height) Bounds(
            int index, int count, double size)
        {
            var radius = size / 2;
            var (start, sweep) = Angles(index, count);
            var end = start + sweep;

            var minX = radius;
            var maxX = radius;
            var minY = radius;
            var maxY = radius;

            void Include(double degrees)
            {
                var radians = degrees * Math.PI / 180;
                var x = radius + radius * Math.Cos(radians);
                var y = radius + radius * Math.Sin(radians);

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            Include(start);
            Include(end);

            // Every quarter turn the wedge sweeps through is an extreme of the
            // arc — the point where it is furthest right, down, left or up.
            // The loop runs from -90 because that is where the first wedge
            // starts, and to 360 because the last one ends there.
            for (var degrees = -90.0; degrees <= 360.0; degrees += 90)
            {
                if (degrees >= start && degrees <= end) Include(degrees);
            }

            return (minX, minY, maxX - minX, maxY - minY);
        }
    }
}
