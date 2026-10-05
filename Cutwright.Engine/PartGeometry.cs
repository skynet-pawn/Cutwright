namespace Cutwright
{
    internal enum PartShapeKind
    {
        // The part is (near enough) its own bounding box.
        Rectangle = 0,

        // A right triangle - overwhelmingly a gusset. Two congruent right triangles combine on
        // the hypotenuse into exactly the rectangle of their two legs, so these get paired up
        // before packing instead of each wasting its bounding box.
        RightTriangle = 1,

        // Anything else. Nested by bounding box, which is conservative but rare enough not to
        // move an estimate.
        Other = 2
    }

    // A part's outline in its own local coordinate space: one outer contour plus zero or more
    // hole contours. Either synthesized as a rectangle from BOM width/length, or read from a DXF.
    internal class PartGeometry
    {
        // A polygon this close to filling its bounding box is treated as a rectangle - covers
        // rounded corners, small notches, and tessellation noise.
        private const double RectangleAreaRatio = 0.95;

        // A triangle is exactly half its bounding box; allow slack for clipped corners.
        private const double TriangleAreaRatioMin = 0.42;
        private const double TriangleAreaRatioMax = 0.58;

        public List<(double X, double Y)> Outer { get; private set; } = new();
        public List<List<(double X, double Y)>> Holes { get; } = new();

        public PartShapeKind Kind { get; private set; } = PartShapeKind.Rectangle;

        // Bounding-box footprint, which is what the packer places.
        public double Width { get; private set; }
        public double Length { get; private set; }

        // True enclosed area of the outer contour. Used to sanity-check that two paired gussets
        // genuinely fit the rectangle they're being paired into.
        public double PolygonArea { get; private set; }

        // For a right triangle, which bounding-box corner the right angle sits on:
        // 0 = (0,0), 1 = (W,0), 2 = (W,L), 3 = (0,L). Each paired copy is anchored to its own
        // right-angle corner, so this determines where in the shared rectangle it goes.
        public int RightAngleCorner { get; private set; }

        // How much the bounding box has to grow for the outline to genuinely fit inside a
        // corner-to-corner triangle. Exactly 1.0 for a sharp gusset; slightly over for a filleted
        // one, whose rounded acute corners pull the bounding box in and leave the outline bulging
        // a little past the ideal diagonal. Two gussets only tile a rectangle this size or larger.
        public double PairInflation { get; private set; } = 1.0;

        public double PairFootprintWidth => Width * PairInflation;
        public double PairFootprintLength => Length * PairInflation;

        // Fraction of the bounding box the outline does not occupy. This is the pocket an
        // interleave has to work with, so it doubles as the pre-filter for whether searching
        // one is worth the effort at all.
        public double Concavity
        {
            get
            {
                double boxArea = Width * Length;
                return boxArea <= 0 ? 0.0 : Math.Max(0.0, 1.0 - PolygonArea / boxArea);
            }
        }

        // Outer contour turned by a whole number of quarter turns and re-normalized so its
        // bounding box starts at the origin.
        //
        // Done as exact coordinate swaps rather than through ToWorld's trig, because these points
        // feed the raster search and the sign of a 6e-17 cosine is not something a grid fill
        // should have an opinion about. The result still matches ToWorld at the same angle - both
        // rotate about the local origin and then pull the rotated bounding box back to the
        // placement point - so an offset verified here is the offset that gets cut.
        public List<(double X, double Y)> RotatedOuter(int quarterTurns)
        {
            var rotated = Rotate(Outer, quarterTurns);

            double minX = rotated.Min(p => p.X);
            double minY = rotated.Min(p => p.Y);
            return rotated.Select(p => (p.X - minX, p.Y - minY)).ToList();
        }

        // Bounding-box extents after that many quarter turns: odd turns swap the two axes.
        public (double Width, double Length) RotatedExtents(int quarterTurns) =>
            (quarterTurns & 1) == 0 ? (Width, Length) : (Length, Width);

        // Every rotation a nest ever places a part at is a quarter turn - gussets flip 180,
        // interleaved members take any of the four, and the packer adds 90 - so this is the one
        // place placement code converts a placed rotation back to the turn count RotatedExtents
        // and RotatedOuter both key off.
        public static int QuarterTurns(double rotationDegrees) =>
            ((int)Math.Round(rotationDegrees / 90.0) % 4 + 4) % 4;

        private static List<(double X, double Y)> Rotate(List<(double X, double Y)> points, int quarterTurns)
        {
            return (((quarterTurns % 4) + 4) % 4) switch
            {
                1 => points.Select(p => (-p.Y, p.X)).ToList(),
                2 => points.Select(p => (-p.X, -p.Y)).ToList(),
                3 => points.Select(p => (p.Y, -p.X)).ToList(),
                _ => points.ToList()
            };
        }

        public static PartGeometry FromRectangle(double width, double length)
        {
            var geometry = new PartGeometry
            {
                Kind = PartShapeKind.Rectangle,
                Width = width,
                Length = length,
                PolygonArea = width * length
            };

            geometry.Outer.Add((0, 0));
            geometry.Outer.Add((width, 0));
            geometry.Outer.Add((width, length));
            geometry.Outer.Add((0, length));
            return geometry;
        }

        public static PartGeometry FromDxf(string path)
        {
            var outline = DxfOutlineReader.Read(path);

            // Holes are normalized against the outer contour's own original (pre-shift) position,
            // not geometry.Outer - by the time geometry.Outer exists it has already been shifted to
            // start at the origin, so its own min is trivially (0, 0) and using it as the reference
            // here was a no-op: holes were left in the DXF's raw absolute coordinates instead of
            // being carried along by the same shift as the outer contour. Harmless only when a
            // part's outline happened to already sit with its bounding box at the origin in the
            // source file - anywhere else, every hole rendered and cut at the wrong position.
            var geometry = new PartGeometry();
            geometry.Outer = Normalize(outline.Outer);
            foreach (var hole in outline.Holes)
                geometry.Holes.Add(Normalize(hole, outline.Outer));

            geometry.Classify();
            return geometry;
        }

        // Shifts a contour so the outer contour's bounding box starts at the origin, keeping
        // holes in the same relative position.
        private static List<(double X, double Y)> Normalize(List<(double X, double Y)> points,
            List<(double X, double Y)>? relativeTo = null)
        {
            var reference = relativeTo ?? points;
            double minX = reference.Min(p => p.X);
            double minY = reference.Min(p => p.Y);
            return points.Select(p => (p.X - minX, p.Y - minY)).ToList();
        }

        private void Classify()
        {
            Width = Outer.Max(p => p.X);
            Length = Outer.Max(p => p.Y);

            double boxArea = Width * Length;
            if (boxArea <= 0)
            {
                Kind = PartShapeKind.Other;
                return;
            }

            PolygonArea = DxfOutlineReader.PolygonArea(Outer);
            double ratio = PolygonArea / boxArea;

            // Anything filling nearly its whole bounding box nests as that box, whatever its
            // corner count - covers filleted rectangles, small notches, and clipped corners.
            if (ratio >= RectangleAreaRatio)
            {
                Kind = PartShapeKind.Rectangle;
                return;
            }

            // Deliberately no corner count here. Counting corners on a filleted outline is
            // unreliable: a fillet is an arc tessellated into many small turns, and straight
            // segments contribute no interior vertices, so there is no flat stretch between one
            // fillet and the next to separate them on.
            //
            // Instead, measure how far the outline reaches past a corner-to-corner diagonal. Once
            // the rectangle is grown by that much, the shape provably fits inside one half of it,
            // its 180-degree rotation fits the other half, and the two halves meet only along the
            // diagonal - so the pair cannot overlap whatever the outline actually looks like.
            if (ratio >= TriangleAreaRatioMin && ratio <= TriangleAreaRatioMax &&
                TryFitRightTriangle(Outer, Width, Length, out int corner, out double inflation))
            {
                Kind = PartShapeKind.RightTriangle;
                RightAngleCorner = corner;
                PairInflation = inflation;
                return;
            }

            Kind = PartShapeKind.Other;
        }

        // Finds which bounding-box corner the outline's right angle sits on, and how much the box
        // must grow for the outline to fit entirely within that corner's half.
        //
        // For a corner-anchored right triangle every point satisfies (say, for the origin corner)
        // x/W + y/L <= 1. Taking the largest value that expression reaches over the outline gives
        // the inflation factor directly: growing the box to (t*W, t*L) turns the inequality into
        // (x/W + y/L)/t <= 1, which holds by construction. Sharp gussets give t = 1.
        private static bool TryFitRightTriangle(List<(double X, double Y)> points, double width, double length,
            out int corner, out double inflation)
        {
            corner = 0;
            inflation = 1.0;

            if (width <= 0 || length <= 0)
                return false;

            // Beyond this the outline isn't really a corner-to-corner triangle, and the rectangle
            // two of them would need is big enough that pairing stops being worth it.
            const double maxInflation = 1.15;

            // The four ways the right angle can sit on a bounding-box corner.
            var tests = new Func<double, double, double>[]
            {
                (x, y) => x / width + y / length,                      // right angle at (0,0)
                (x, y) => (width - x) / width + y / length,            // at (width,0)
                (x, y) => (width - x) / width + (length - y) / length, // at (width,length)
                (x, y) => x / width + (length - y) / length            // at (0,length)
            };

            double best = double.MaxValue;
            int bestCorner = -1;

            for (int c = 0; c < tests.Length; c++)
            {
                double reach = points.Max(p => tests[c](p.X, p.Y));

                if (reach < best)
                {
                    best = reach;
                    bestCorner = c;
                }
            }

            if (bestCorner < 0 || best > maxInflation)
                return false;

            corner = bestCorner;
            inflation = Math.Max(1.0, best);
            return true;
        }

        private static double NormalizeAngle(double radians)
        {
            while (radians > Math.PI) radians -= 2 * Math.PI;
            while (radians < -Math.PI) radians += 2 * Math.PI;
            return radians;
        }

        // Rotates about the local origin then translates so the rotated bounding box's minimum
        // corner lands exactly on (x, y) - so a placed part's X/Y always means "bottom-left of
        // the part as it sits on the sheet", which is what the packer produces and what both the
        // canvas and the DXF export want.
        public (List<(double X, double Y)> Outer, List<List<(double X, double Y)>> Holes) ToWorld(
            double x, double y, double rotationDegrees)
        {
            // Every rotation a nest actually produces is a quarter turn, and routing those through
            // trig leaves a shear of about 1e-16 on the result - Math.Sin(Math.PI) is not zero.
            // That is enough to turn two parts meant to abut exactly on a shared line, like the
            // hypotenuses of a paired gusset, into two parts that cross by a hair. Exact
            // coefficients for the quarter turns; trig only if something ever asks for an angle in
            // between.
            double cos, sin;
            int quarterTurns = (int)Math.Round(rotationDegrees / 90.0);

            if (Math.Abs(rotationDegrees - quarterTurns * 90.0) < 1e-9)
            {
                (cos, sin) = ((quarterTurns % 4 + 4) % 4) switch
                {
                    1 => (0.0, 1.0),
                    2 => (-1.0, 0.0),
                    3 => (0.0, -1.0),
                    _ => (1.0, 0.0)
                };
            }
            else
            {
                double rad = rotationDegrees * Math.PI / 180.0;
                cos = Math.Cos(rad);
                sin = Math.Sin(rad);
            }

            (double X, double Y) Rotate((double X, double Y) p) => (p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);

            var rotatedOuter = Outer.Select(Rotate).ToList();
            var rotatedHoles = Holes.Select(h => h.Select(Rotate).ToList()).ToList();

            double minX = rotatedOuter.Min(p => p.X);
            double minY = rotatedOuter.Min(p => p.Y);
            double dx = x - minX;
            double dy = y - minY;

            return (rotatedOuter.Select(p => (p.X + dx, p.Y + dy)).ToList(),
                    rotatedHoles.Select(h => h.Select(p => (p.X + dx, p.Y + dy)).ToList()).ToList());
        }
    }
}
