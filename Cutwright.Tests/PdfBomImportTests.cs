using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cutwright.Tests
{
    // The filtering rules that separate a real bill-of-materials table from the noise Tabula's
    // lattice mode also finds on a real drawing - title block fields, drafting grids, and several
    // unrelated ruled regions merged into one table. Driven against plain rows rather than an
    // actual PDF: EvaluateCandidate is the part of PdfBomImport that has any judgment call in it,
    // and it does not need Tabula's own cell/rectangle types to exercise that judgment.
    public sealed class PdfBomImportTests
    {
        private static string[] Row(params string[] cells) => cells;

        // A real, if small, four-column bill of materials - the shape a customer's table almost
        // always has: a header, then data rows, every row the same width.
        private static List<string[]> RealBom() => new()
        {
            Row("Item", "Description", "QTY.", "Weight"),
            Row("01", "HSS 8\" X 3\" X .125", "2", "31.89"),
            Row("02", "HSS 8\" X 3\" X .125", "1", "16.92"),
            Row("03", "HSS 8\" X 3\" X .125", "4", "9.37"),
        };

        [Fact]
        public void ARealBomTablePassesEveryFilter()
        {
            var candidate = PdfBomImport.EvaluateCandidate(RealBom(), pageNumber: 2);

            Assert.NotNull(candidate);
            Assert.Equal(2, candidate!.Page);
            Assert.Equal(4, candidate.RowCount);
            Assert.Equal(4, candidate.ColumnCount);
        }

        [Fact]
        public void AShortTableIsRejectedAsATitleBlockField()
        {
            var rows = new List<string[]>
            {
                Row("Part/Drwg Number"),
                Row("71411"),
            };

            Assert.Null(PdfBomImport.EvaluateCandidate(rows, pageNumber: 1));
        }

        [Fact]
        public void ATableWithRaggedRowWidthsIsRejected()
        {
            // The failure mode this guards: several unrelated ruled regions sharing enough ruling
            // lines that Tabula's lattice detector merges them into one table, so no single row
            // width describes the whole thing.
            var rows = RealBom();
            rows.Add(Row("04", "extra"));

            Assert.Null(PdfBomImport.EvaluateCandidate(rows, pageNumber: 2));
        }

        [Fact]
        public void AVeryWideTableIsRejectedAsMergedRegions()
        {
            var wideRow = Enumerable.Range(1, 25).Select(i => $"c{i}").ToArray();
            var rows = Enumerable.Range(0, 5).Select(_ => wideRow).ToList();

            Assert.Null(PdfBomImport.EvaluateCandidate(rows, pageNumber: 3));
        }

        [Fact]
        public void ATableWithAMergedBoilerplateCellIsRejected()
        {
            // Seen on a real drawing: the true BOM table's ruling
            // lines got merged by Tabula's lattice detector with the adjacent title-block/
            // tolerance-note region on the same page, producing a second, corrupted table whose
            // first cell ran every description and dimension together with a page of boilerplate
            // into one multi-thousand-character string - and which outranked the real table since
            // it also came out with more rows. Reproduced here at a smaller scale: one cell far
            // longer than any real BOM cell should ever be.
            var rows = RealBom();
            rows[1][1] = rows[1][1] + new string('X', 300);

            Assert.Null(PdfBomImport.EvaluateCandidate(rows, pageNumber: 2));
        }

        [Fact]
        public void AMostlyBlankTableIsRejectedAsADraftingGrid()
        {
            // Same ruled shape as a real table, but empty - a drafting grid or an unfilled form
            // rather than data.
            var rows = new List<string[]>
            {
                Row("", "", "", ""),
                Row("", "", "", ""),
                Row("", "", "", "one"),
                Row("", "", "", ""),
            };

            Assert.Null(PdfBomImport.EvaluateCandidate(rows, pageNumber: 4));
        }

        [Fact]
        public void PreviewSkipsAMergedBannerRowAheadOfTheRealHeader()
        {
            // "BILL OF MATERIALS" spanning the full width as its own row above the header is what
            // a real drawing's title looks like once Tabula reads it back - one non-empty cell in
            // an otherwise blank row.
            var rows = new List<string[]>
            {
                Row("BILL OF MATERIALS", "", "", ""),
                Row("Item", "Description", "QTY.", "Weight"),
                Row("01", "HSS 8\" X 3\" X .125", "2", "31.89"),
            };

            var candidate = PdfBomImport.EvaluateCandidate(rows, pageNumber: 2);

            Assert.NotNull(candidate);
            Assert.Equal("Item  |  Description  |  QTY.  |  Weight", candidate!.Preview);
        }

        [Fact]
        public void PreviewFallsBackToTheFirstRowWhenThereIsNoBannerRow()
        {
            var candidate = PdfBomImport.EvaluateCandidate(RealBom(), pageNumber: 2);

            Assert.NotNull(candidate);
            Assert.Equal("Item  |  Description  |  QTY.  |  Weight", candidate!.Preview);
        }

        [Fact]
        public void MergingTwoSelectedTablesConcatenatesRowsInOrder()
        {
            // What the picker window does when the estimator checks more than one candidate - a
            // real BOM whose "BILL OF MATERIALS" header repeats partway down a page splits into
            // exactly two blocks like this.
            var first = PdfBomImport.EvaluateCandidate(RealBom(), pageNumber: 2)!;
            var second = PdfBomImport.EvaluateCandidate(new List<string[]>
            {
                Row("Item", "Description", "QTY.", "Weight"),
                Row("70", "HRS 1/4\" x 1 7/8\"", "2", "0.20"),
                Row("71", "Laser HRS 1/4\" x 2\"", "1", "0.75"),
            }, pageNumber: 2)!;

            var merged = CsvTable.FromRows(first.Rows.Concat(second.Rows));

            Assert.Equal(7, merged.Rows.Count);
            Assert.Equal("70", merged.Rows[5][0]);
            Assert.Equal("71", merged.Rows[6][0]);
        }
    }
}
