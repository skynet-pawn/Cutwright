namespace Cutwright
{
    // One member of an interleaved cluster: which quarter turn it sits at, and where its bounding
    // box lands inside the cluster's own footprint.
    internal sealed class InterleaveMember
    {
        public int QuarterTurns { get; init; }
        public double DX { get; init; }
        public double DY { get; init; }
    }

    internal sealed class InterleaveCluster
    {
        public List<InterleaveMember> Members { get; } = new();
        public double FootprintWidth { get; set; }
        public double FootprintLength { get; set; }
    }

    // Finds how many copies of a concave part will interleave into one another, and where.
    //
    // The packer downstream only ever places rectangles, and that does not change here: a cluster
    // is still handed over as one bounding box. What changes is how much part fits inside that
    // box. Work an L-bracket by hand and the shape of the answer appears - a copy turned 180
    // degrees, flush on one axis, slid along the other until its strips meet the original's - so
    // the search is built around exactly that move: pick a rotation, pick an axis, hold the other
    // axis somewhere, and slide inward until something is in the way.
    //
    // Copies are added one at a time, greedily, each tested against everything already placed.
    // That is deliberately looser than following a fixed repeating step: a taper shingles with a
    // constant step, while an L alternates 0 and 180 degrees and only repeats every second copy,
    // and growing greedily discovers both without either being modelled explicitly. It also
    // catches the case a fixed step would get wrong - the third copy colliding with the first.
    internal static class ClusterBuilder
    {
        // Work bound, not a nesting limit. The growth loop's own stopping conditions - a copy no
        // longer paying for itself, or the cluster outgrowing the sheet - almost always bite well
        // before this.
        private const int MaxClusterSize = 32;

        // Below this there is no pocket worth searching, and rasterizing to find that out is
        // wasted effort. A rectangle is 0; a right-triangle gusset is around 0.5.
        private const double MinConcavity = 0.15;

        // Search resolution. Coarse enough that a big part does not allocate an enormous grid,
        // fine enough that the offset it finds is not leaving useful material on the floor - and
        // it only ever costs yield, never clearance, because every rounding decision in
        // PartRaster goes outward.
        private const double MinCellSize = 0.01;
        private const double MaxCellSize = 0.125;
        private const int CellsAcrossPart = 128;

        // How many positions the off-axis sweep tries before refining around the best of them.
        // Only reached for shapes no flush slide interleaves at all. The refinement pass walks the
        // neighbourhood of the winner cell by cell, so this only has to be dense enough not to
        // step over the pocket entirely.
        private const int SweepSamples = 32;

        // Worth searching only for shapes that are neither their own bounding box nor a gusset -
        // gussets already pair exactly, analytically, and for free.
        public static bool IsCandidate(PartGeometry geometry) =>
            geometry.Kind == PartShapeKind.Other &&
            geometry.Outer.Count >= 3 &&
            geometry.Concavity >= MinConcavity;

        // Grows the longest cluster this geometry supports. Returns null when no copy interleaves
        // usefully, in which case the caller should nest the part on its own as before.
        //
        // The result is a growth sequence, so any prefix of it is also a valid cluster - which is
        // what lets a quantity of ten be split into clusters without searching again.
        public static InterleaveCluster? Build(PartGeometry geometry, double spacing,
            double usableWidth, double usableLength, bool allowRotation)
        {
            double maxDim = Math.Max(geometry.Width, geometry.Length);
            if (maxDim <= 0)
                return null;

            double cell = Math.Clamp(maxDim / CellsAcrossPart, MinCellSize, MaxCellSize);
            int spacingCells = (int)Math.Ceiling(spacing / cell);
            int padCells = spacingCells + 2;

            int turns = allowRotation ? 4 : 1;
            var solid = new PartRaster[turns];
            var grown = new PartRaster[turns];

            for (int q = 0; q < turns; q++)
            {
                var outline = geometry.RotatedOuter(q);
                solid[q] = PartRaster.FromOutline(outline, cell, padCells);
                grown[q] = solid[q].Dilate(spacingCells);
            }

            // The cluster is capped at the usable sheet, and one new copy can reach at most its
            // own long dimension beyond the current bounds, so this covers everywhere a member
            // could legally be placed. Anything off-grid reads as clear, which is why the margin
            // matters rather than just being slack.
            double margin = maxDim + spacing + (padCells + 2) * cell;
            var accumulated = PartRaster.Empty(cell, -margin, -margin,
                usableWidth + 2 * margin, usableLength + 2 * margin);

            var search = new Search
            {
                Geometry = geometry,
                Accumulated = accumulated,
                Solid = solid,
                Grown = grown,
                Turns = turns,
                Cell = cell,
                Spacing = spacing,
                UsableWidth = usableWidth,
                UsableLength = usableLength,
                AllowRotation = allowRotation
            };

            var members = new List<WorkMember>();
            AddFirstMember(members, search);

            while (members.Count < MaxClusterSize && TryGrow(members, search))
            {
            }

            if (members.Count < 2)
                return null;

            return ToCluster(members, geometry);
        }

        // Rebuilds a cluster from the first `count` members of a growth sequence, re-normalized so
        // its footprint starts at the origin.
        public static InterleaveCluster Prefix(InterleaveCluster cluster, PartGeometry geometry, int count)
        {
            count = Math.Clamp(count, 1, cluster.Members.Count);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            for (int i = 0; i < count; i++)
            {
                var member = cluster.Members[i];
                var (w, l) = geometry.RotatedExtents(member.QuarterTurns);

                minX = Math.Min(minX, member.DX);
                minY = Math.Min(minY, member.DY);
                maxX = Math.Max(maxX, member.DX + w);
                maxY = Math.Max(maxY, member.DY + l);
            }

            var result = new InterleaveCluster
            {
                FootprintWidth = maxX - minX,
                FootprintLength = maxY - minY
            };

            for (int i = 0; i < count; i++)
            {
                var member = cluster.Members[i];
                result.Members.Add(new InterleaveMember
                {
                    QuarterTurns = member.QuarterTurns,
                    DX = member.DX - minX,
                    DY = member.DY - minY
                });
            }

            return result;
        }

        // Everything the search needs that does not change as the cluster grows.
        private sealed class Search
        {
            public PartGeometry Geometry = null!;
            public PartRaster Accumulated = null!;
            public PartRaster[] Solid = null!;
            public PartRaster[] Grown = null!;
            public int Turns;
            public double Cell;
            public double Spacing;
            public double UsableWidth;
            public double UsableLength;
            public bool AllowRotation;
        }

        // A member as the search sees it: where it sits in working space, plus the exact polygon
        // it will be cut as, kept so later candidates can be verified against it.
        private sealed class WorkMember
        {
            public int QuarterTurns;
            public double X;
            public double Y;
            public List<(double X, double Y)> Outer = null!;
            public (double MinX, double MinY, double MaxX, double MaxY) Bounds;
        }

        // One placement the search is considering.
        private sealed class Candidate
        {
            public int Order;
            public int QuarterTurns;
            public bool AlongX;
            public bool FromFar;
            public int FixedIndex;
            public double X;
            public double Y;
            public int Col;
            public int Row;
            public double Score;
        }

        private static bool TryGrow(List<WorkMember> members, Search search)
        {
            var bounds = ClusterBounds(members, search.Geometry);
            double current = (bounds.MaxX - bounds.MinX + search.Spacing) *
                             (bounds.MaxY - bounds.MinY + search.Spacing);
            int count = members.Count;

            // Flush against a cluster edge first. That is where the answer is for most shapes -
            // an L, a taper, anything whose pocket opens onto its own bounding box - and it costs
            // a few dozen slides to find out.
            var candidates = CollectFlush(search, bounds, current, count);

            // Nothing flush worked. Some shapes only interleave off-axis: a symmetric U-channel
            // carries its walls on the outside, so a wall has to land in the other part's mouth,
            // which no flush position can reach. Sweep the off-axis position coarsely, then refine
            // around whatever looked best.
            if (candidates.Count == 0)
                candidates = CollectSweep(search, bounds, current, count);

            if (candidates.Count == 0)
                return false;

            // Cheapest first, ties broken by enumeration order so the same BOM always produces the
            // same nest.
            candidates.Sort((a, b) =>
            {
                int byScore = a.Score.CompareTo(b.Score);
                return byScore != 0 ? byScore : a.Order.CompareTo(b.Order);
            });

            foreach (var candidate in candidates)
            {
                var world = search.Geometry.ToWorld(candidate.X, candidate.Y, candidate.QuarterTurns * 90.0);
                var candidateBounds = PolygonDistance.Bounds(world.Outer);

                if (!ClearsEveryMember(members, world.Outer, candidateBounds, search.Spacing))
                    continue;

                search.Accumulated.Add(search.Grown[candidate.QuarterTurns], candidate.Col, candidate.Row);

                members.Add(new WorkMember
                {
                    QuarterTurns = candidate.QuarterTurns,
                    X = candidate.X,
                    Y = candidate.Y,
                    Outer = world.Outer,
                    Bounds = candidateBounds
                });

                return true;
            }

            return false;
        }

        private static List<Candidate> CollectFlush(Search search,
            (double MinX, double MinY, double MaxX, double MaxY) bounds, double current, int count)
        {
            var candidates = new List<Candidate>();
            int order = 0;

            for (int q = 0; q < search.Turns; q++)
            {
                var (partWidth, partLength) = search.Geometry.RotatedExtents(q);

                for (int axis = 0; axis < 2; axis++)
                {
                    bool alongX = axis == 1;

                    // Both flush sides, because which one works depends on the shape's handedness
                    // and there is no cheap way to know in advance.
                    double lowFlush = alongX ? bounds.MinY : bounds.MinX;
                    double highFlush = alongX ? bounds.MaxY - partLength : bounds.MaxX - partWidth;

                    foreach (double flush in new[] { lowFlush, highFlush })
                    {
                        int fixedIndex = FixedIndexOf(search, search.Solid[q], alongX, flush);

                        for (int direction = 0; direction < 2; direction++)
                        {
                            order++;

                            var candidate = Probe(search, bounds, current, count, q, partWidth,
                                partLength, alongX, direction == 1, fixedIndex, order);

                            if (candidate != null)
                                candidates.Add(candidate);
                        }
                    }
                }
            }

            return candidates;
        }

        private static List<Candidate> CollectSweep(Search search,
            (double MinX, double MinY, double MaxX, double MaxY) bounds, double current, int count)
        {
            var candidates = new List<Candidate>();
            int order = 1000;

            for (int q = 0; q < search.Turns; q++)
            {
                var (partWidth, partLength) = search.Geometry.RotatedExtents(q);

                for (int axis = 0; axis < 2; axis++)
                {
                    bool alongX = axis == 1;
                    var (low, high) = OffAxisRange(search, search.Solid[q], bounds, alongX, partWidth, partLength);

                    if (high < low)
                        continue;

                    int stride = Math.Max(1, (high - low) / SweepSamples);
                    Candidate? bestOfSweep = null;

                    for (int fixedIndex = low; fixedIndex <= high; fixedIndex += stride)
                    {
                        for (int direction = 0; direction < 2; direction++)
                        {
                            order++;

                            var candidate = Probe(search, bounds, current, count, q, partWidth,
                                partLength, alongX, direction == 1, fixedIndex, order);

                            if (candidate == null)
                                continue;

                            candidates.Add(candidate);

                            if (bestOfSweep == null || candidate.Score < bestOfSweep.Score)
                                bestOfSweep = candidate;
                        }
                    }

                    if (bestOfSweep == null || stride == 1)
                        continue;

                    // The sweep only sampled every few cells; walk the neighbourhood of the best
                    // one cell at a time so the placement is as tight as the grid allows.
                    for (int fixedIndex = bestOfSweep.FixedIndex - stride;
                         fixedIndex <= bestOfSweep.FixedIndex + stride;
                         fixedIndex++)
                    {
                        if (fixedIndex < low || fixedIndex > high)
                            continue;

                        order++;

                        var candidate = Probe(search, bounds, current, count, q, partWidth,
                            partLength, alongX, bestOfSweep.FromFar, fixedIndex, order);

                        if (candidate != null)
                            candidates.Add(candidate);
                    }
                }
            }

            return candidates;
        }

        // Runs one slide and turns it into a scored candidate, or nothing if it does not clear, does
        // not fit the sheet, or does not pay for itself.
        private static Candidate? Probe(Search search,
            (double MinX, double MinY, double MaxX, double MaxY) bounds, double current, int count,
            int quarterTurns, double partWidth, double partLength, bool alongX, bool fromFar,
            int fixedIndex, int order)
        {
            if (!TrySlide(search, search.Solid[quarterTurns], bounds, partWidth, partLength,
                    alongX, fromFar, fixedIndex, out double x, out double y, out int col, out int row))
            {
                return null;
            }

            double newWidth = Math.Max(bounds.MaxX, x + partWidth) - Math.Min(bounds.MinX, x);
            double newLength = Math.Max(bounds.MaxY, y + partLength) - Math.Min(bounds.MinY, y);

            bool fits = newWidth + search.Spacing <= search.UsableWidth &&
                        newLength + search.Spacing <= search.UsableLength;

            if (!fits && search.AllowRotation)
            {
                fits = newLength + search.Spacing <= search.UsableWidth &&
                       newWidth + search.Spacing <= search.UsableLength;
            }

            if (!fits)
                return null;

            // A cluster is judged by what it costs per part, not by whether one more copy is
            // cheaper than nesting that copy alone. Those are not the same test, and the
            // difference matters: a chain of individually-worthwhile additions can still end up
            // worse per part than stopping early and letting the packer tile the shorter clusters
            // itself. Written as a cross-multiplication to keep it to one comparison.
            double score = (newWidth + search.Spacing) * (newLength + search.Spacing);
            if (score * count >= current * (count + 1))
                return null;

            return new Candidate
            {
                Order = order,
                QuarterTurns = quarterTurns,
                AlongX = alongX,
                FromFar = fromFar,
                FixedIndex = fixedIndex,
                X = x,
                Y = y,
                Col = col,
                Row = row,
                Score = score
            };
        }

        // The raster only proposes; this decides. Every accepted offset is checked against the
        // real outlines of everything already in the cluster - and against the same ToWorld()
        // output the DXF writer will emit - so what gets exported is what was verified.
        //
        // Only outer contours are compared. Holes sit inside their own outline by definition, so
        // two outlines that clear each other cannot have a hole in common.
        private static bool ClearsEveryMember(List<WorkMember> members, List<(double X, double Y)> outer,
            (double MinX, double MinY, double MaxX, double MaxY) bounds, double spacing)
        {
            foreach (var member in members)
            {
                if (PolygonDistance.BoxesFarApart(member.Bounds, bounds, spacing))
                    continue;

                if (!PolygonDistance.Clears(member.Outer, outer, spacing))
                    return false;
            }

            return true;
        }

        // Slides a copy in from outside the cluster along one axis, at a fixed position on the
        // other, and stops where it first meets something.
        //
        // Taking the last clear position before the first obstruction is a slide-in, the same move
        // a person makes fitting one part into another by eye. It also means the scan usually ends
        // after a handful of steps rather than sweeping the whole axis, which is what keeps the
        // search cheap enough to run at this resolution.
        private static bool TrySlide(Search search, PartRaster part,
            (double MinX, double MinY, double MaxX, double MaxY) bounds,
            double partWidth, double partLength, bool alongX, bool fromFar, int fixedIndex,
            out double x, out double y, out int col, out int row)
        {
            x = 0;
            y = 0;
            col = 0;
            row = 0;

            var accumulated = search.Accumulated;
            double cell = search.Cell;
            double spacing = search.Spacing;

            double clusterSpan = alongX ? bounds.MaxX - bounds.MinX : bounds.MaxY - bounds.MinY;
            double partSpan = alongX ? partWidth : partLength;

            // Where the slide starts: clear of the cluster by the spacing, plus a cell so the
            // first position tested is genuinely outside it. Both index lookups floor, which only
            // ever moves the start further out.
            double travelStart = fromFar
                ? (alongX ? bounds.MaxX : bounds.MaxY) + spacing + cell
                : (alongX ? bounds.MinX : bounds.MinY) - partSpan - spacing - cell;

            int travelIndex = alongX
                ? accumulated.ColumnOf(travelStart + part.MinX)
                : accumulated.RowOf(travelStart + part.MinY);

            int step = fromFar ? -1 : 1;

            // Travelling deeper than the far side of the cluster cannot help: once the copy is
            // inside the cluster's span on this axis, going further only pushes the combined
            // bounding box out the other side. Capping here is also what stops a slide that never
            // meets anything from walking the whole grid, which is most of a sweep.
            int maxSteps = (int)Math.Ceiling((clusterSpan + spacing + cell) / cell) + 3;

            int bestTravel = int.MinValue;

            for (int s = 0; s < maxSteps; s++)
            {
                int testCol = alongX ? travelIndex : fixedIndex;
                int testRow = alongX ? fixedIndex : travelIndex;

                if (accumulated.Overlaps(part, testCol, testRow))
                    break;

                bestTravel = travelIndex;
                travelIndex += step;
            }

            if (bestTravel == int.MinValue)
                return false;

            col = alongX ? bestTravel : fixedIndex;
            row = alongX ? fixedIndex : bestTravel;

            x = accumulated.XOfColumn(col) - part.MinX;
            y = accumulated.YOfRow(row) - part.MinY;
            return true;
        }

        private static int FixedIndexOf(Search search, PartRaster part, bool alongX, double position) =>
            alongX
                ? search.Accumulated.RowOf(position + part.MinY)
                : search.Accumulated.ColumnOf(position + part.MinX);

        // Off-axis positions worth sweeping. Outside this range the copy does not overlap the
        // cluster on that axis at all, so it is not interleaving - it is just sitting alongside,
        // which the packer already does better than a cluster would.
        private static (int Low, int High) OffAxisRange(Search search, PartRaster part,
            (double MinX, double MinY, double MaxX, double MaxY) bounds, bool alongX,
            double partWidth, double partLength)
        {
            double low = alongX
                ? bounds.MinY - partLength + search.Cell
                : bounds.MinX - partWidth + search.Cell;

            double high = alongX
                ? bounds.MaxY - search.Cell
                : bounds.MaxX - search.Cell;

            return (FixedIndexOf(search, part, alongX, low), FixedIndexOf(search, part, alongX, high));
        }

        private static void AddFirstMember(List<WorkMember> members, Search search)
        {
            var world = search.Geometry.ToWorld(0, 0, 0);
            var part = search.Grown[0];

            search.Accumulated.Add(part, search.Accumulated.ColumnOf(part.MinX),
                search.Accumulated.RowOf(part.MinY));

            members.Add(new WorkMember
            {
                QuarterTurns = 0,
                X = 0,
                Y = 0,
                Outer = world.Outer,
                Bounds = PolygonDistance.Bounds(world.Outer)
            });
        }

        // Bounding box of the members' own boxes, which is what the packer will be given - not the
        // outline bounds, so it stays consistent with how a single part is measured.
        private static (double MinX, double MinY, double MaxX, double MaxY) ClusterBounds(
            List<WorkMember> members, PartGeometry geometry)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var member in members)
            {
                var (w, l) = geometry.RotatedExtents(member.QuarterTurns);

                minX = Math.Min(minX, member.X);
                minY = Math.Min(minY, member.Y);
                maxX = Math.Max(maxX, member.X + w);
                maxY = Math.Max(maxY, member.Y + l);
            }

            return (minX, minY, maxX, maxY);
        }

        private static InterleaveCluster ToCluster(List<WorkMember> members, PartGeometry geometry)
        {
            var raw = new InterleaveCluster();

            foreach (var member in members)
            {
                raw.Members.Add(new InterleaveMember
                {
                    QuarterTurns = member.QuarterTurns,
                    DX = member.X,
                    DY = member.Y
                });
            }

            return Prefix(raw, geometry, raw.Members.Count);
        }
    }
}
