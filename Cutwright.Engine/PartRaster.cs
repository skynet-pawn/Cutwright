namespace Cutwright
{
    // A bitmask occupancy grid for one part outline, used to search for interleave offsets.
    //
    // Exact polygon offsetting is the "correct" way to do this and the wrong tool for the job:
    // robust polygon clipping is a lot of floating-point edge cases to get wrong, and this runs
    // inside a nest that has to stay deterministic. A grid of bits is neither of those things.
    //
    // Every rounding decision here goes outward - a cell is occupied if the outline touches it at
    // all, and clearance is applied by growing the occupied set rather than shrinking it. So a
    // clear raster test always implies real clearance, never the other way round. The cost is a
    // fraction of a cell of yield; the benefit is that the search can never propose an offset
    // that is geometrically too tight.
    internal sealed class PartRaster
    {
        private readonly ulong[] bits;

        public double CellSize { get; }

        // World coordinate of column 0 / row 0's low edge, so a cell index can be turned back
        // into a real offset once the search has picked one.
        public double MinX { get; }
        public double MinY { get; }

        public int Cols { get; }
        public int Rows { get; }
        public int WordsPerRow { get; }

        private PartRaster(double cellSize, double minX, double minY, int cols, int rows)
        {
            CellSize = cellSize;
            MinX = minX;
            MinY = minY;
            Cols = Math.Max(cols, 1);
            Rows = Math.Max(rows, 1);
            WordsPerRow = (Cols + 63) / 64;
            bits = new ulong[WordsPerRow * Rows];
        }

        // An all-clear grid covering a known region - the accumulator a cluster is built up in.
        public static PartRaster Empty(double cellSize, double minX, double minY, double width, double height)
        {
            int cols = (int)Math.Ceiling(width / cellSize) + 1;
            int rows = (int)Math.Ceiling(height / cellSize) + 1;
            return new PartRaster(cellSize, minX, minY, cols, rows);
        }

        // Burns an outline into a grid. The outline is expected already normalized to its bounding
        // box, so the grid is the part's own box plus padCells of margin on every side - margin
        // the dilation pass needs somewhere to grow into.
        public static PartRaster FromOutline(List<(double X, double Y)> outline, double cellSize, int padCells)
        {
            var bounds = PolygonDistance.Bounds(outline);

            double minX = bounds.MinX - padCells * cellSize;
            double minY = bounds.MinY - padCells * cellSize;
            int cols = (int)Math.Ceiling((bounds.MaxX - bounds.MinX) / cellSize) + 2 * padCells + 2;
            int rows = (int)Math.Ceiling((bounds.MaxY - bounds.MinY) / cellSize) + 2 * padCells + 2;

            var raster = new PartRaster(cellSize, minX, minY, cols, rows);

            // Boundary first: every cell any edge passes through. This is what makes the interior
            // fill below safe to do by parity at the cell centre - any cell the outline actually
            // crosses is already marked, so a centre-point test can only misjudge cells the
            // outline does not touch.
            int n = outline.Count;
            for (int i = 0; i < n; i++)
                raster.MarkSegment(outline[i], outline[(i + 1) % n]);

            raster.FillInterior(outline);
            return raster;
        }

        public int ColumnOf(double x) => (int)Math.Floor((x - MinX) / CellSize);

        public int RowOf(double y) => (int)Math.Floor((y - MinY) / CellSize);

        // World coordinate of the low edge of a column or row, for turning a chosen cell offset
        // back into a real placement.
        public double XOfColumn(int col) => MinX + col * CellSize;

        public double YOfRow(int row) => MinY + row * CellSize;

        private void Set(int col, int row)
        {
            if (col < 0 || col >= Cols || row < 0 || row >= Rows)
                return;

            bits[row * WordsPerRow + (col >> 6)] |= 1UL << (col & 63);
        }

        // Chebyshev dilation by a number of cells, which is how part spacing gets enforced:
        // growing one side's occupied set by ceil(spacing / cell) cells covers everything within
        // that distance of it, because Chebyshev distance never exceeds Euclidean. A clear test
        // against a grown raster therefore guarantees true clearance of at least the spacing.
        //
        // Separable, so it is two cheap passes rather than a box scan per cell.
        public PartRaster Dilate(int cells)
        {
            if (cells <= 0)
                return this;

            var result = new PartRaster(CellSize, MinX, MinY, Cols, Rows);
            Array.Copy(bits, result.bits, bits.Length);

            // Horizontal: smear each row one bit either way, that many times, carrying across the
            // word boundary as it goes.
            var scratch = new ulong[WordsPerRow];
            for (int row = 0; row < Rows; row++)
            {
                int b = row * WordsPerRow;

                for (int pass = 0; pass < cells; pass++)
                {
                    for (int w = 0; w < WordsPerRow; w++)
                    {
                        ulong current = result.bits[b + w];
                        ulong fromLeft = w > 0 ? result.bits[b + w - 1] >> 63 : 0UL;
                        ulong fromRight = w < WordsPerRow - 1 ? result.bits[b + w + 1] << 63 : 0UL;
                        scratch[w] = current | (current << 1) | (current >> 1) | fromLeft | fromRight;
                    }

                    Array.Copy(scratch, 0, result.bits, b, WordsPerRow);
                }
            }

            // Vertical: OR together the window of rows around each row. Done from a snapshot so
            // the smear cannot feed itself and grow further than asked.
            var source = new ulong[result.bits.Length];
            Array.Copy(result.bits, source, source.Length);

            for (int row = 0; row < Rows; row++)
            {
                int b = row * WordsPerRow;
                int from = Math.Max(0, row - cells);
                int to = Math.Min(Rows - 1, row + cells);

                for (int other = from; other <= to; other++)
                {
                    if (other == row)
                        continue;

                    int ob = other * WordsPerRow;
                    for (int w = 0; w < WordsPerRow; w++)
                        result.bits[b + w] |= source[ob + w];
                }
            }

            return result;
        }

        // True when the other raster, positioned so its cell (0,0) sits on this raster's cell
        // (colOffset, rowOffset), has any occupied cell in common with this one.
        //
        // Anything falling outside this raster counts as clear: the accumulator is allocated to
        // cover everywhere a cluster could legally reach, so off-grid means off-cluster.
        public bool Overlaps(PartRaster other, int colOffset, int rowOffset)
        {
            var (firstWord, lastWord) = WordSpan(other, colOffset);

            for (int otherRow = 0; otherRow < other.Rows; otherRow++)
            {
                int row = otherRow + rowOffset;
                if (row < 0 || row >= Rows)
                    continue;

                int b = row * WordsPerRow;
                int ob = otherRow * other.WordsPerRow;

                for (int w = firstWord; w <= lastWord; w++)
                {
                    ulong word = bits[b + w];
                    if (word == 0)
                        continue;

                    if ((word & ShiftedWord(other, ob, w, colOffset)) != 0)
                        return true;
                }
            }

            return false;
        }

        // The span of this raster's words the other raster can actually reach at that offset.
        //
        // Worth being careful about: the accumulator a cluster is built in covers a whole sheet
        // plus margin, so a row of it is far wider than any one part. Walking the full row per
        // slide step - almost all of it empty and nowhere near the part - was costing an order of
        // magnitude more than the test itself.
        private (int First, int Last) WordSpan(PartRaster other, int colOffset)
        {
            int firstColumn = Math.Max(0, colOffset);
            int lastColumn = Math.Min(Cols - 1, colOffset + other.Cols - 1);

            if (lastColumn < firstColumn)
                return (0, -1);

            return (firstColumn >> 6, lastColumn >> 6);
        }

        // ORs the other raster into this one at a cell offset, committing a placement.
        public void Add(PartRaster other, int colOffset, int rowOffset)
        {
            var (firstWord, lastWord) = WordSpan(other, colOffset);

            for (int otherRow = 0; otherRow < other.Rows; otherRow++)
            {
                int row = otherRow + rowOffset;
                if (row < 0 || row >= Rows)
                    continue;

                int b = row * WordsPerRow;
                int ob = otherRow * other.WordsPerRow;

                for (int w = firstWord; w <= lastWord; w++)
                    bits[b + w] |= ShiftedWord(other, ob, w, colOffset);
            }
        }

        // The word of the other raster's row lining up with this raster's word w once shifted by
        // colOffset columns. This raster's word w spans columns [64w, 64w+63], which are the other
        // raster's columns [64w - colOffset, ...], so the answer straddles two of its words.
        private static ulong ShiftedWord(PartRaster other, int otherRowBase, int w, int colOffset)
        {
            long start = 64L * w - colOffset;

            // Arithmetic shift and mask, so this floors correctly for negative offsets.
            int index = (int)(start >> 6);
            int bit = (int)(start & 63);

            ulong low = other.WordAt(otherRowBase, index);
            if (bit == 0)
                return low;

            ulong high = other.WordAt(otherRowBase, index + 1);
            return (low >> bit) | (high << (64 - bit));
        }

        private ulong WordAt(int rowBase, int index) =>
            index < 0 || index >= WordsPerRow ? 0UL : bits[rowBase + index];

        // Walks the cells a segment passes through, marking each one. Standard grid traversal:
        // step to whichever of the next column or row boundary the segment reaches first.
        private void MarkSegment((double X, double Y) p0, (double X, double Y) p1)
        {
            int col = ColumnOf(p0.X);
            int row = RowOf(p0.Y);
            int endCol = ColumnOf(p1.X);
            int endRow = RowOf(p1.Y);

            Set(col, row);
            if (col == endCol && row == endRow)
                return;

            double dx = p1.X - p0.X;
            double dy = p1.Y - p0.Y;

            int stepCol = dx >= 0 ? 1 : -1;
            int stepRow = dy >= 0 ? 1 : -1;

            double nextX = MinX + (dx >= 0 ? col + 1 : col) * CellSize;
            double nextY = MinY + (dy >= 0 ? row + 1 : row) * CellSize;

            double tX = dx != 0 ? (nextX - p0.X) / dx : double.MaxValue;
            double tY = dy != 0 ? (nextY - p0.Y) / dy : double.MaxValue;
            double stepX = dx != 0 ? CellSize / Math.Abs(dx) : double.MaxValue;
            double stepY = dy != 0 ? CellSize / Math.Abs(dy) : double.MaxValue;

            // The traversal terminates on reaching the end cell; the cap is a guard against a
            // degenerate segment (a coincident or non-finite point) spinning here forever.
            int guard = Cols + Rows + 4;

            while (guard-- > 0)
            {
                if (tX <= tY)
                {
                    if (tX > 1.0)
                        return;

                    col += stepCol;
                    tX += stepX;
                }
                else
                {
                    if (tY > 1.0)
                        return;

                    row += stepRow;
                    tY += stepY;
                }

                Set(col, row);

                if (col == endCol && row == endRow)
                    return;
            }
        }

        // Even-odd span fill at each row's centre line. Winding is unnormalized on outlines
        // chained out of a DXF, so parity is the only fill rule that works either way round.
        private void FillInterior(List<(double X, double Y)> outline)
        {
            int n = outline.Count;
            var crossings = new List<double>();

            for (int row = 0; row < Rows; row++)
            {
                double y = MinY + (row + 0.5) * CellSize;
                crossings.Clear();

                for (int i = 0; i < n; i++)
                {
                    var p1 = outline[i];
                    var p2 = outline[(i + 1) % n];

                    if (p1.Y > y == p2.Y > y)
                        continue;

                    crossings.Add(p1.X + (y - p1.Y) * (p2.X - p1.X) / (p2.Y - p1.Y));
                }

                if (crossings.Count < 2)
                    continue;

                crossings.Sort();

                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int from = ColumnOf(crossings[k]);
                    int to = ColumnOf(crossings[k + 1]);

                    for (int col = from; col <= to; col++)
                        Set(col, row);
                }
            }
        }
    }
}
