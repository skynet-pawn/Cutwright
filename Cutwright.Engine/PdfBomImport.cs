using Tabula;
using Tabula.Extractors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Cutwright
{
    // One ruled table Tabula found on one page. The estimator picks which of these is the real
    // bill of materials - there is no reliable way to know that automatically, since a technical
    // data package carries plenty of other ruled tables (a revision history, a table of contents,
    // a legend, and - on a document like this one - a partial BOM repeated on every detail sheet
    // alongside the master list). What FindTables can do is throw out the tables that obviously
    // are not it.
    internal sealed class PdfTableCandidate
    {
        public required int Page { get; init; }
        public required List<string[]> Rows { get; init; }

        public int RowCount => Rows.Count;
        public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Length;

        // The most informative row to show in a picker list - the header row if there is an
        // obviously blank banner row above it (a merged "BILL OF MATERIALS" title spanning the
        // whole width, common on real drawings), the first row otherwise.
        public string Preview
        {
            get
            {
                string[] row = Rows.Count > 1 && Rows[0].Count(c => c.Length > 0) <= 1 ? Rows[1] : Rows[0];
                string text = string.Join("  |  ", row.Select(c => c.Trim()));
                return text.Length <= 140 ? text : text[..140] + "...";
            }
        }
    }

    // Finds candidate bill-of-materials tables in a PDF, using Tabula's lattice algorithm - the
    // one that follows ruled lines rather than guessing at whitespace gaps. Every real customer
    // drawing sample seen so far rules its BOM table, which is what makes this worth automating;
    // a drawing that does not rule its table needs the manual Import BOM from CSV path through the
    // standalone Tabula app instead, same as before.
    internal static class PdfBomImport
    {
        // Below this many rows a table is almost always a title block field, not a parts list.
        private const int MinRows = 3;

        // A table this wide or wider is symptomatic of the failure mode this filtering exists to
        // catch: several unrelated ruled regions on a busy drawing page, sharing enough ruling
        // lines that Tabula's lattice detector merges them into one table with a column for every
        // distinct edge it found.
        private const int MaxColumns = 20;

        // How much of a table has to actually hold text for it to be worth showing. A drafting
        // grid or a mostly-blank form has the same ruled structure as a real table and passes every
        // other filter here, so this is what tells them apart.
        private const double MinDensity = 0.3;

        // Above this, a single cell holds far more than one BOM line's worth of text. Seen on a
        // real drawing: the true BOM table's own ruling lines got
        // merged by Tabula's lattice detector with an adjacent title-block/tolerance-note region,
        // producing a second, corrupted candidate on the same page whose first cell ran every
        // description, dimension, and page of boilerplate together into one multi-thousand-
        // character string - and which briefly outranked the real table since it also had more
        // rows. A real BOM cell, even a verbose purchased-part description, does not run this long.
        private const int MaxCellLength = 250;

        // Every ruled table in the document that looks like a real data table rather than a title
        // block field, a drafting grid, or several ruled regions merged into one - largest first,
        // since the actual bill of materials is reliably among the biggest tables on whichever
        // page carries it. Errors are per page: one page whose ruling lines confuse Tabula's
        // intersection finder must not lose every candidate on every other page.
        public static List<PdfTableCandidate> FindTables(string path, out List<string> errors)
        {
            errors = new List<string>();
            var candidates = new List<PdfTableCandidate>();

            PdfDocument document;
            try
            {
                document = PdfDocument.Open(path);
            }
            catch (Exception e)
            {
                errors.Add($"Could not open '{System.IO.Path.GetFileName(path)}': {e.Message}");
                return candidates;
            }

            using (document)
            for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                try
                {
                    PageArea page = ObjectExtractor.Extract(document, pageNumber);
                    IExtractionAlgorithm lattice = new SpreadsheetExtractionAlgorithm();

                    foreach (Table table in lattice.Extract(page))
                    {
                        PdfTableCandidate? candidate = AsCandidate(table, pageNumber);
                        if (candidate is not null)
                            candidates.Add(candidate);
                    }
                }
                catch (Exception e)
                {
                    errors.Add($"Page {pageNumber} could not be read: {e.Message}");
                }
            }

            return candidates.OrderByDescending(c => c.RowCount).ToList();
        }

        private static PdfTableCandidate? AsCandidate(Table table, int pageNumber)
        {
            var rows = table.Rows
                .Select(r => r.Select(c => c.GetText().Replace("\n", " ").Trim()).ToArray())
                .ToList();

            return EvaluateCandidate(rows, pageNumber);
        }

        // The filtering rules on their own, apart from Tabula's own Table type - so they can be
        // tested against plain rows without building Tabula's cell/rectangle object graph just to
        // exercise a size and density check.
        internal static PdfTableCandidate? EvaluateCandidate(List<string[]> rows, int pageNumber)
        {
            if (rows.Count < MinRows)
                return null;

            // A real table from a ruled grid has the same cell count in every row; several
            // unrelated ruled regions merged together routinely do not.
            int columns = rows[0].Length;
            if (columns < 2 || columns > MaxColumns)
                return null;
            if (rows.Any(r => r.Length != columns))
                return null;

            int filledCells = rows.Sum(r => r.Count(c => c.Length > 0));
            double density = filledCells / (double)(rows.Count * columns);
            if (density < MinDensity)
                return null;

            if (rows.Any(r => r.Any(c => c.Length > MaxCellLength)))
                return null;

            return new PdfTableCandidate { Page = pageNumber, Rows = rows };
        }
    }
}
