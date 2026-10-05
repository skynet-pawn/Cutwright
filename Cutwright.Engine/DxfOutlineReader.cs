using netDxf;
using netDxf.Entities;

namespace Cutwright
{
    // Reads closed outlines out of a DXF using netDxf directly.
    //
    // Flat patterns are usually exported as loose Line/Arc segments rather than closed
    // polylines, so segments are chained end-to-end by matching endpoints before they mean
    // anything as a shape. The largest resulting loop is the part outline; anything else that
    // closed is a hole.
    internal static class DxfOutlineReader
    {
        // Arc/circle tessellation step. Fine enough that a hole reads as round when drawn, coarse
        // enough not to bloat the point count - this feeds area/corner classification, not a
        // cut path.
        private const double ArcStepDegrees = 6.0;

        // Endpoint match tolerance, in inches, for treating two segment ends as the same point.
        // Real exports are rarely perfectly coincident - CAD round-tripping, spline
        // approximation, and unit conversion all leave sub-thousandth gaps - so exact matching
        // rejects outlines that are visually closed.
        private const double JoinTolerance = 0.005;

        // A chain whose two ends land within this distance is treated as closed. Looser than the
        // join tolerance to accommodate drawings whose outline genuinely doesn't quite meet.
        private const double ClosureTolerance = 0.05;

        public sealed class Outline
        {
            public List<(double X, double Y)> Outer { get; set; } = new();
            public List<List<(double X, double Y)>> Holes { get; } = new();
        }

        public static Outline Read(string path)
        {
            DxfDocument doc = DxfDocument.Load(path)
                ?? throw new InvalidOperationException("netDxf could not parse the file.");

            // Everything downstream (BOM dimensions, sheet sizes) is in inches.
            double scale = doc.DrawingVariables.InsUnits switch
            {
                netDxf.Units.DrawingUnits.Millimeters => 1.0 / 25.4,
                netDxf.Units.DrawingUnits.Centimeters => 1.0 / 2.54,
                netDxf.Units.DrawingUnits.Meters => 1.0 / 0.0254,
                netDxf.Units.DrawingUnits.Feet => 12.0,
                _ => 1.0
            };

            var edges = new List<List<(double X, double Y)>>();
            var closedLoops = new List<List<(double X, double Y)>>();

            // Scale is applied here, before chaining, so the join/closure tolerances below are
            // always in inches regardless of the drawing's own units.
            foreach (var entity in doc.Entities.All)
                Flatten(entity, Transform.Identity.WithScale(scale), edges, closedLoops);

            ChainEdgesIntoLoops(edges, closedLoops);

            if (closedLoops.Count == 0)
                throw new InvalidOperationException("No closed outline could be formed from the DXF geometry.");

            // Largest-area loop is the part; the rest are interior features.
            int outerIndex = 0;
            double outerArea = -1.0;
            for (int i = 0; i < closedLoops.Count; i++)
            {
                double area = PolygonArea(closedLoops[i]);
                if (area > outerArea)
                {
                    outerArea = area;
                    outerIndex = i;
                }
            }

            var result = new Outline { Outer = closedLoops[outerIndex] };

            for (int i = 0; i < closedLoops.Count; i++)
            {
                if (i != outerIndex && closedLoops[i].Count >= 3)
                    result.Holes.Add(closedLoops[i]);
            }

            return result;
        }

        public static double PolygonArea(List<(double X, double Y)> points)
        {
            double area = 0;
            int n = points.Count;

            for (int i = 0; i < n; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % n];
                area += p1.X * p2.Y - p2.X * p1.Y;
            }

            return Math.Abs(area) / 2.0;
        }

        // Maps a point from an entity's local space up through nested block-reference (Insert)
        // transforms into world space. Non-uniform scale on an insert isn't supported (radius and
        // angle handling assumes Scale.X == Scale.Y).
        private sealed class Transform
        {
            public static readonly Transform Identity = new((x, y) => (x, y), 1.0, 0.0);

            public Func<double, double, (double X, double Y)> MapPoint { get; }
            public double Scale { get; }
            public double RotationDegrees { get; }

            private Transform(Func<double, double, (double X, double Y)> mapPoint, double scale, double rotationDegrees)
            {
                MapPoint = mapPoint;
                Scale = scale;
                RotationDegrees = rotationDegrees;
            }

            // Folds a uniform unit conversion into the transform so every emitted point, and
            // every radius, is already in inches.
            public Transform WithScale(double factor)
            {
                if (factor == 1.0)
                    return this;

                var parentMap = MapPoint;
                return new Transform((x, y) =>
                {
                    var p = parentMap(x, y);
                    return (p.X * factor, p.Y * factor);
                }, Scale * factor, RotationDegrees);
            }

            public Transform Nest(Insert insert)
            {
                Vector3 origin = insert.Block.Origin;
                Vector3 insertScale = insert.Scale;
                double rad = insert.Rotation * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                var parentMap = MapPoint;

                (double X, double Y) LocalToParent(double x, double y)
                {
                    double lx = (x - origin.X) * insertScale.X;
                    double ly = (y - origin.Y) * insertScale.Y;
                    return parentMap(lx * cos - ly * sin + insert.Position.X,
                                     lx * sin + ly * cos + insert.Position.Y);
                }

                return new Transform(LocalToParent, Scale * insertScale.X, RotationDegrees + insert.Rotation);
            }
        }

        private static List<(double X, double Y)> TessellateArc(double cx, double cy, double radius, double startDeg, double endDeg)
        {
            double sweep = endDeg - startDeg;
            if (sweep <= 0)
                sweep += 360.0;

            int segments = Math.Max(2, (int)Math.Ceiling(sweep / ArcStepDegrees));
            var points = new List<(double, double)>(segments + 1);

            for (int i = 0; i <= segments; i++)
            {
                double angle = (startDeg + sweep * i / segments) * Math.PI / 180.0;
                points.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }

            return points;
        }

        // Sorts geometry into edges (open runs that still need chaining) and loops that are
        // already inherently closed (circles, closed polylines).
        private static void Flatten(EntityObject entity, Transform transform,
            List<List<(double X, double Y)>> edges, List<List<(double X, double Y)>> closedLoops)
        {
            switch (entity)
            {
                case Line line:
                    edges.Add(new List<(double, double)>
                    {
                        transform.MapPoint(line.StartPoint.X, line.StartPoint.Y),
                        transform.MapPoint(line.EndPoint.X, line.EndPoint.Y)
                    });
                    break;

                case Arc arc:
                    {
                        var (cx, cy) = transform.MapPoint(arc.Center.X, arc.Center.Y);
                        edges.Add(TessellateArc(cx, cy, arc.Radius * transform.Scale,
                            arc.StartAngle + transform.RotationDegrees, arc.EndAngle + transform.RotationDegrees));
                        break;
                    }

                case Circle circle:
                    {
                        var (cx, cy) = transform.MapPoint(circle.Center.X, circle.Center.Y);
                        closedLoops.Add(TessellateArc(cx, cy, circle.Radius * transform.Scale, 0.0, 360.0));
                        break;
                    }

                case Polyline2D poly:
                    {
                        var points = poly.Vertexes
                            .Select(v => transform.MapPoint(v.Position.X, v.Position.Y))
                            .ToList();

                        if (points.Count < 2)
                            break;

                        if (poly.IsClosed)
                            closedLoops.Add(points);
                        else
                            edges.Add(points);
                        break;
                    }

                case Spline spline:
                    {
                        var points = spline.PolygonalVertexes(64)
                            .Select(v => transform.MapPoint(v.X, v.Y))
                            .ToList();

                        if (points.Count < 2)
                            break;

                        if (spline.IsClosed)
                            closedLoops.Add(points);
                        else
                            edges.Add(points);
                        break;
                    }

                case Insert insert when insert.Block != null:
                    {
                        Transform child = transform.Nest(insert);
                        foreach (var nested in insert.Block.Entities)
                            Flatten(nested, child, edges, closedLoops);
                        break;
                    }
            }
        }

        private static double Distance((double X, double Y) a, (double X, double Y) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // Chains edges end-to-end into closed loops, joining endpoints that are within
        // JoinTolerance rather than requiring exact coincidence - real exports leave sub-
        // thousandth gaps and an exact match rejects outlines that are visually closed.
        //
        // Assumes simple non-branching outlines: where several unused ends sit within tolerance,
        // the nearest is followed. A run whose ends meet within ClosureTolerance counts as closed;
        // anything still open is discarded, since it isn't a shape (typically bend lines, centre
        // marks, or dimension witness lines).
        private static void ChainEdgesIntoLoops(List<List<(double X, double Y)>> edges,
            List<List<(double X, double Y)>> closedLoops)
        {
            int n = edges.Count;
            var used = new bool[n];

            for (int i = 0; i < n; i++)
            {
                if (used[i] || edges[i].Count < 2)
                    continue;

                used[i] = true;
                var chain = new List<(double X, double Y)>(edges[i]);

                // Extend from the running end until nothing joins on.
                for (int guard = 0; guard < n; guard++)
                {
                    var tail = chain[^1];

                    if (chain.Count >= 3 && Distance(tail, chain[0]) <= ClosureTolerance)
                        break;

                    int nextIndex = -1;
                    bool nextAtStart = false;
                    double nextDistance = double.MaxValue;

                    for (int j = 0; j < n; j++)
                    {
                        if (used[j] || edges[j].Count < 2)
                            continue;

                        double toStart = Distance(tail, edges[j][0]);
                        if (toStart <= JoinTolerance && toStart < nextDistance)
                        {
                            nextDistance = toStart;
                            nextIndex = j;
                            nextAtStart = true;
                        }

                        double toEnd = Distance(tail, edges[j][^1]);
                        if (toEnd <= JoinTolerance && toEnd < nextDistance)
                        {
                            nextDistance = toEnd;
                            nextIndex = j;
                            nextAtStart = false;
                        }
                    }

                    if (nextIndex < 0)
                        break;

                    used[nextIndex] = true;
                    var next = edges[nextIndex];

                    // Skip the shared endpoint so it isn't duplicated in the chain.
                    if (nextAtStart)
                    {
                        for (int k = 1; k < next.Count; k++)
                            chain.Add(next[k]);
                    }
                    else
                    {
                        for (int k = next.Count - 2; k >= 0; k--)
                            chain.Add(next[k]);
                    }
                }

                if (chain.Count >= 3 && Distance(chain[^1], chain[0]) <= ClosureTolerance)
                {
                    // Drop a duplicated closing vertex; the loop is implicitly closed.
                    if (Distance(chain[^1], chain[0]) <= JoinTolerance)
                        chain.RemoveAt(chain.Count - 1);

                    if (chain.Count >= 3)
                        closedLoops.Add(chain);
                }
            }
        }
    }
}
