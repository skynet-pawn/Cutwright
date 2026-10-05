namespace Cutwright
{
    // One rectangular footprint to place on a sheet.
    internal class PackItem
    {
        public double Width { get; init; }
        public double Length { get; init; }

        // Set for expanded metal, whose diamond direction ties the part's Length axis to the
        // sheet's Length axis - swapping the two would produce a nest that can't be cut from
        // the material as ordered.
        public bool AllowRotation { get; init; } = true;

        // Caller payload, handed back untouched on the placement.
        public object? Tag { get; init; }

        public double Area => Width * Length;
    }

    internal class PackPlacement
    {
        public int SheetIndex { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public bool Rotated { get; init; }
        public object? Tag { get; init; }
    }

    internal class PackResult
    {
        public List<PackPlacement> Placements { get; } = new();
        public List<PackItem> Unplaced { get; } = new();
        public int SheetCount { get; set; }
    }

    // The order parts are fed to the packer in. A constructive packer's result depends entirely
    // on this: whichever part claims space first shapes every placement after it, and no single
    // order is best on every job. Benchmarking against another nesting program showed the cost is about one sheet
    // on jobs that are a handful of large awkward plates - many-small-part jobs were unaffected,
    // because there the backfill absorbs a bad early choice.
    //
    // Declaration order is significant: PackBest keeps the first strategy on a tie, so
    // MaxSideThenArea must stay first to preserve the nests this produced before.
    internal enum PackOrder
    {
        // Longest edge first. Keeps a long part from being stranded across two sheets.
        MaxSideThenArea = 0,

        // Biggest footprint first. Beats MaxSide when the job's largest parts are chunky rather
        // than long - a 30x60 claims more useful space than a 5x70.
        AreaThenMaxSide,

        // Compromise between the two: penalises long-and-thin less than MaxSide does.
        PerimeterThenArea,

        // Axis-aligned orders. These matter because the free-space split is axis-aligned too:
        // grouping parts by a shared edge length leaves remnants that stack instead of
        // fragmenting into slivers.
        WidthThenLength,
        LengthThenWidth
    }

    // Deterministic rectangle packer. Same input always produces the same nest, which matters
    // for an estimating tool - two people quoting the same BOM must get the same sheet count.
    //
    // Runs in two modes:
    //  * FreePlacement - MaxRects style, parts may sit anywhere. Used for laser/plasma/router.
    //  * Shear         - guillotine, every cut spans the full piece. Free space is split in two
    //                    rather than four so the layout stays shear-cuttable by construction.
    internal static class SheetPacker
    {
        private struct FreeRect
        {
            public double X, Y, Width, Length;
            public double Area => Width * Length;
        }

        // Packs the job once per ordering strategy and keeps the best result. Still deterministic
        // - a fixed set of orderings tried in a fixed sequence, with ties going to the earliest -
        // so the same BOM still always produces the same sheet count. This is what a search-based
        // nester buys with minutes of stochastic search, except bounded: the work is a small fixed
        // multiple of one pack, not a time budget.
        public static PackResult PackBest(IEnumerable<PackItem> items, double sheetWidth, double sheetLength,
            double spacing, CutMethod cutMethod)
        {
            // Materialised once: the strategies re-order the same items rather than re-enumerating
            // a source that may not be replayable.
            var pool = items.ToList();

            var orders = Enum.GetValues<PackOrder>();
            var candidates = new PackResult[orders.Length];

            // The five packs are wholly independent - each builds its own ordering, its own sheets
            // and its own result, and reads nothing but the shared immutable item list - so they
            // run at once. On a large job this is the difference between five sequential passes and
            // roughly one: 52,000 parts measured at 4.3s serial.
            //
            // Determinism is untouched because nothing is decided here. Results land in a fixed
            // slot and the winner is chosen below in declaration order, so the answer does not
            // depend on which pack finished first.
            Parallel.For(0, orders.Length, i =>
            {
                candidates[i] = Pack(pool, sheetWidth, sheetLength, spacing, cutMethod, orders[i]);
            });

            PackResult? best = null;

            foreach (var candidate in candidates)
            {
                // Placing everything outranks using fewer sheets: a dropped part is a wrong quote,
                // not an inefficient one.
                if (best is null ||
                    candidate.Unplaced.Count < best.Unplaced.Count ||
                    (candidate.Unplaced.Count == best.Unplaced.Count && candidate.SheetCount < best.SheetCount))
                {
                    best = candidate;
                }
            }

            return best!;
        }

        public static PackResult Pack(IEnumerable<PackItem> items, double sheetWidth, double sheetLength,
            double spacing, CutMethod cutMethod, PackOrder order = PackOrder.MaxSideThenArea)
        {
            var result = new PackResult();

            // Sorting largest-first is what keeps big parts from being stranded on their own
            // sheet: they claim space early, then smaller parts backfill the gaps left behind.
            // Which measure of "largest" is used is the strategy - see PackOrder.
            var ordered = Order(items, order);

            // Smallest footprint area still to come, at each point in the run. A sheet whose
            // largest free rectangle is under this can never take another part, so it can be set
            // aside for good rather than re-scanned by everything that follows.
            var smallestRemaining = SmallestRemainingArea(ordered, spacing);

            var sheets = new List<SheetState>();

            for (int index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];

                // Spacing is reserved as part of the footprint, so parts land at least `spacing`
                // apart without the packer needing to reason about neighbours.
                double footprintW = item.Width + spacing;
                double footprintL = item.Length + spacing;

                bool fitsUnrotated = footprintW <= sheetWidth && footprintL <= sheetLength;
                bool fitsRotated = item.AllowRotation && footprintL <= sheetWidth && footprintW <= sheetLength;

                if (!fitsUnrotated && !fitsRotated)
                {
                    result.Unplaced.Add(item);
                    continue;
                }

                if (!TryPlace(sheets, item, footprintW, footprintL, cutMethod, result))
                {
                    // Nothing open had room; start a new sheet. The fit check above guarantees
                    // an empty sheet can take it, so this cannot loop.
                    var opened = new SheetState
                    {
                        Free = { new FreeRect { X = 0, Y = 0, Width = sheetWidth, Length = sheetLength } }
                    };

                    opened.Remeasure();
                    sheets.Add(opened);

                    TryPlace(sheets, item, footprintW, footprintL, cutMethod, result);
                }

                // Anything that cannot hold the smallest part left is finished with.
                double floor = smallestRemaining[index + 1];

                foreach (var sheet in sheets)
                {
                    if (!sheet.Retired && sheet.MaxFreeArea < floor)
                        sheet.Retired = true;
                }
            }

            result.SheetCount = sheets.Count;
            return result;
        }

        // Suffix minimum of footprint area, so index i holds the smallest footprint among items i
        // onwards. The final entry is positive infinity: once the last part is placed there is
        // nothing left to fit, so every sheet retires and the scan stops entirely.
        private static double[] SmallestRemainingArea(List<PackItem> ordered, double spacing)
        {
            var smallest = new double[ordered.Count + 1];
            smallest[ordered.Count] = double.PositiveInfinity;

            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                double footprint = (ordered[i].Width + spacing) * (ordered[i].Length + spacing);
                smallest[i] = Math.Min(footprint, smallest[i + 1]);
            }

            return smallest;
        }

        // All orderings are largest-first, which is fundamental to constructive packing - they
        // differ only in what "largest" measures. OrderBy is a stable sort, so items that tie on
        // every key stay in the caller's order and the result stays reproducible.
        private static List<PackItem> Order(IEnumerable<PackItem> items, PackOrder order) => order switch
        {
            PackOrder.MaxSideThenArea => items
                .OrderByDescending(i => Math.Max(i.Width, i.Length))
                .ThenByDescending(i => i.Area)
                .ToList(),

            PackOrder.AreaThenMaxSide => items
                .OrderByDescending(i => i.Area)
                .ThenByDescending(i => Math.Max(i.Width, i.Length))
                .ToList(),

            PackOrder.PerimeterThenArea => items
                .OrderByDescending(i => i.Width + i.Length)
                .ThenByDescending(i => i.Area)
                .ToList(),

            PackOrder.WidthThenLength => items
                .OrderByDescending(i => i.Width)
                .ThenByDescending(i => i.Length)
                .ToList(),

            PackOrder.LengthThenWidth => items
                .OrderByDescending(i => i.Length)
                .ThenByDescending(i => i.Width)
                .ToList(),

            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown pack order.")
        };

        // One open sheet's free space, plus the largest free rectangle on it.
        //
        // A large job spends nearly all its time here. TryPlace walks the sheets in order and stops
        // at the first that fits, so once a few hundred sheets are open every new part scans all of
        // them - and each scan walks that sheet's free rectangles in both orientations. Measured on
        // a 52,000 part job: 3,000 identical parts filled 834 sheets, and the packer re-scanned the
        // full run of them for every part after the first.
        //
        // Both uses of MaxFreeArea below are conservative: a rectangle cannot hold a footprint
        // larger in area than itself, so a sheet failing the test could not have taken the part.
        // Nothing about which sheet is chosen changes, so the nest is placement-identical - only
        // the work to reach it is smaller.
        private sealed class SheetState
        {
            public List<FreeRect> Free { get; init; } = new();

            public double MaxFreeArea;

            // Set once no remaining part could fit, after which the sheet is never looked at again.
            public bool Retired;

            public void Remeasure()
            {
                double max = 0.0;

                foreach (var rect in Free)
                {
                    double area = rect.Area;
                    if (area > max)
                        max = area;
                }

                MaxFreeArea = max;
            }
        }

        private static bool TryPlace(List<SheetState> sheets, PackItem item,
            double footprintW, double footprintL, CutMethod cutMethod, PackResult result)
        {
            int bestSheet = -1;
            int bestRect = -1;
            bool bestRotated = false;
            double bestPrimary = double.MaxValue;
            double bestSecondary = double.MaxValue;

            double footprintArea = footprintW * footprintL;

            for (int s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];

                // Retired sheets hold nothing any remaining part can use; the area test rules this
                // part out of this sheet without walking its rectangles.
                if (sheet.Retired || sheet.MaxFreeArea < footprintArea)
                    continue;

                var free = sheet.Free;

                for (int r = 0; r < free.Count; r++)
                {
                    var rect = free[r];

                    for (int orientation = 0; orientation < 2; orientation++)
                    {
                        bool rotated = orientation == 1;
                        if (rotated && !item.AllowRotation)
                            continue;

                        double w = rotated ? footprintL : footprintW;
                        double l = rotated ? footprintW : footprintL;

                        if (w > rect.Width || l > rect.Length)
                            continue;

                        // Best-short-side-fit: prefer the placement leaving the smallest sliver
                        // on either axis, which keeps remaining free space usefully chunky.
                        double leftoverW = rect.Width - w;
                        double leftoverL = rect.Length - l;
                        double primary = Math.Min(leftoverW, leftoverL);
                        double secondary = Math.Max(leftoverW, leftoverL);

                        if (primary < bestPrimary || (primary == bestPrimary && secondary < bestSecondary))
                        {
                            bestPrimary = primary;
                            bestSecondary = secondary;
                            bestSheet = s;
                            bestRect = r;
                            bestRotated = rotated;
                        }
                    }
                }

                // Fill earlier sheets before later ones so a sheet is genuinely finished before
                // the next is opened, rather than parts being scattered thinly across many.
                if (bestSheet >= 0)
                    break;
            }

            if (bestSheet < 0)
                return false;

            var target = sheets[bestSheet].Free;
            var chosen = target[bestRect];
            double placedW = bestRotated ? footprintL : footprintW;
            double placedL = bestRotated ? footprintW : footprintL;

            result.Placements.Add(new PackPlacement
            {
                SheetIndex = bestSheet,
                X = chosen.X,
                Y = chosen.Y,
                Rotated = bestRotated,
                Tag = item.Tag
            });

            var placed = new FreeRect { X = chosen.X, Y = chosen.Y, Width = placedW, Length = placedL };

            if (cutMethod == CutMethod.Shear)
                SplitGuillotine(target, bestRect, placed);
            else
                SplitFree(target, placed);

            sheets[bestSheet].Remeasure();

            return true;
        }

        // Guillotine split: the chosen free rectangle becomes exactly two new rectangles, so
        // every boundary remains a full-length cut. Splitting along the shorter leftover axis
        // keeps the larger remnant intact.
        private static void SplitGuillotine(List<FreeRect> free, int index, FreeRect placed)
        {
            var rect = free[index];
            free.RemoveAt(index);

            double leftoverW = rect.Width - placed.Width;
            double leftoverL = rect.Length - placed.Length;

            if (leftoverW <= leftoverL)
            {
                // Cut across the width first: a full-height strip beside the part, then the
                // remainder above it spanning only the part's width.
                if (leftoverW > 0)
                    free.Add(new FreeRect { X = rect.X + placed.Width, Y = rect.Y, Width = leftoverW, Length = rect.Length });
                if (leftoverL > 0)
                    free.Add(new FreeRect { X = rect.X, Y = rect.Y + placed.Length, Width = placed.Width, Length = leftoverL });
            }
            else
            {
                if (leftoverL > 0)
                    free.Add(new FreeRect { X = rect.X, Y = rect.Y + placed.Length, Width = rect.Width, Length = leftoverL });
                if (leftoverW > 0)
                    free.Add(new FreeRect { X = rect.X + placed.Width, Y = rect.Y, Width = leftoverW, Length = placed.Length });
            }
        }

        // Free placement: every free rectangle overlapping the placed part is cut back around
        // it, so overlapping candidate spaces are kept and the part can be tucked anywhere.
        private static void SplitFree(List<FreeRect> free, FreeRect placed)
        {
            for (int i = free.Count - 1; i >= 0; i--)
            {
                var rect = free[i];

                bool overlaps = placed.X < rect.X + rect.Width && placed.X + placed.Width > rect.X &&
                                placed.Y < rect.Y + rect.Length && placed.Y + placed.Length > rect.Y;

                if (!overlaps)
                    continue;

                free.RemoveAt(i);

                if (placed.X > rect.X)
                    free.Add(new FreeRect { X = rect.X, Y = rect.Y, Width = placed.X - rect.X, Length = rect.Length });

                if (placed.X + placed.Width < rect.X + rect.Width)
                    free.Add(new FreeRect
                    {
                        X = placed.X + placed.Width,
                        Y = rect.Y,
                        Width = rect.X + rect.Width - (placed.X + placed.Width),
                        Length = rect.Length
                    });

                if (placed.Y > rect.Y)
                    free.Add(new FreeRect { X = rect.X, Y = rect.Y, Width = rect.Width, Length = placed.Y - rect.Y });

                if (placed.Y + placed.Length < rect.Y + rect.Length)
                    free.Add(new FreeRect
                    {
                        X = rect.X,
                        Y = placed.Y + placed.Length,
                        Width = rect.Width,
                        Length = rect.Y + rect.Length - (placed.Y + placed.Length)
                    });
            }

            PruneContained(free);
        }

        // Drops free rectangles wholly inside another, which MaxRects-style splitting produces
        // in quantity and which would otherwise make placement search progressively slower.
        private static void PruneContained(List<FreeRect> free)
        {
            for (int i = free.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < free.Count; j++)
                {
                    if (i == j)
                        continue;

                    if (free[i].X >= free[j].X && free[i].Y >= free[j].Y &&
                        free[i].X + free[i].Width <= free[j].X + free[j].Width &&
                        free[i].Y + free[i].Length <= free[j].Y + free[j].Length)
                    {
                        free.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}
