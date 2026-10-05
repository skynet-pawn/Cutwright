namespace Cutwright
{
    // Exact clearance checks between two placed outlines.
    //
    // The raster search that proposes an interleave offset is deliberately conservative, so in
    // principle it can never propose something that fails here. But the nest DXF is imported
    // straight into SigmaNest and cut from, so "in principle" isn't good enough: every offset the
    // raster accepts is re-checked against the true polygons before it joins a cluster, using the
    // very same ToWorld() output the DXF writer will emit. If it passes here, the geometry that
    // gets cut is the geometry that was verified.
    internal static class PolygonDistance
    {
        // Slack for treating a computed distance as meeting the requirement, absorbing the last
        // bit or two of floating-point noise in the segment maths.
        private const double Epsilon = 1e-9;

        // True when no edge of `a` crosses an edge of `b`, every edge pair is at least `spacing`
        // apart, and neither contour lies wholly inside the other.
        //
        // Bails on the first violation instead of computing the real minimum - the caller only
        // ever asks a yes/no question, and the answer is usually "no" early.
        public static bool Clears(List<(double X, double Y)> a, List<(double X, double Y)> b, double spacing)
        {
            if (a.Count < 3 || b.Count < 3)
                return false;

            int na = a.Count;
            int nb = b.Count;

            for (int i = 0; i < na; i++)
            {
                var a1 = a[i];
                var a2 = a[(i + 1) % na];

                for (int j = 0; j < nb; j++)
                {
                    var b1 = b[j];
                    var b2 = b[(j + 1) % nb];

                    // Crossing edges are a failure at any spacing, including zero: two outlines
                    // may butt together for a common-line cut, but they may never pass through
                    // each other. Distance alone can't tell those apart - both read as zero.
                    if (SegmentsIntersect(a1, a2, b1, b2))
                        return false;

                    if (spacing > 0 && SegmentDistance(a1, a2, b1, b2) < spacing - Epsilon)
                        return false;
                }
            }

            // No edges cross, so the contours are either separate or one is completely inside the
            // other. One point settles which.
            if (Contains(a, b[0]) || Contains(b, a[0]))
                return false;

            return true;
        }

        // Axis-aligned pre-filter. Two outlines whose bounding boxes are more than `spacing`
        // apart cannot possibly violate anything, which is the common case in a long cluster -
        // most member pairs are nowhere near each other.
        public static bool BoxesFarApart((double MinX, double MinY, double MaxX, double MaxY) a,
            (double MinX, double MinY, double MaxX, double MaxY) b, double spacing)
        {
            return a.MinX - b.MaxX > spacing || b.MinX - a.MaxX > spacing ||
                   a.MinY - b.MaxY > spacing || b.MinY - a.MaxY > spacing;
        }

        public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(
            List<(double X, double Y)> points)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var p in points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            return (minX, minY, maxX, maxY);
        }

        // Even-odd ray cast. Winding is unnormalized on outlines chained out of a DXF, so parity
        // is the only test that works for either direction.
        private static bool Contains(List<(double X, double Y)> polygon, (double X, double Y) point)
        {
            bool inside = false;
            int n = polygon.Count;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];

                if (pi.Y > point.Y != pj.Y > point.Y &&
                    point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static bool SegmentsIntersect((double X, double Y) p1, (double X, double Y) p2,
            (double X, double Y) q1, (double X, double Y) q2)
        {
            double d1 = Cross(q1, q2, p1);
            double d2 = Cross(q1, q2, p2);
            double d3 = Cross(p1, p2, q1);
            double d4 = Cross(p1, p2, q2);

            // Strict sign change on both segments: a proper crossing. Touching endpoints and
            // collinear overlap give a zero and are left to the distance check, so butting
            // outlines aren't rejected out of hand.
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static double SegmentDistance((double X, double Y) p1, (double X, double Y) p2,
            (double X, double Y) q1, (double X, double Y) q2)
        {
            double best = PointSegmentDistance(p1, q1, q2);
            best = Math.Min(best, PointSegmentDistance(p2, q1, q2));
            best = Math.Min(best, PointSegmentDistance(q1, p1, p2));
            best = Math.Min(best, PointSegmentDistance(q2, p1, p2));
            return best;
        }

        private static double PointSegmentDistance((double X, double Y) p,
            (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;

            if (lengthSquared <= 0)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
            t = Math.Clamp(t, 0.0, 1.0);

            double cx = a.X + t * dx - p.X;
            double cy = a.Y + t * dy - p.Y;
            return Math.Sqrt(cx * cx + cy * cy);
        }
    }
}
