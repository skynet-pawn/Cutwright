using System.Globalization;
using System.IO;
using CsvHelper;
using CsvHelper.Configuration;

namespace Cutwright
{
    // A CSV read into rows of cells, exactly as it came off disk.
    //
    // Read with CsvHelper rather than by splitting on commas. That is not a tidying-up: a comma
    // inside a quoted field used to break the row, and it happens constantly in real part
    // descriptions - "Formed Laser HRS 3/16" x 3" ... c/w (2) Bends, Profile (29)". Measured on one
    // of the sample files in this repository, 12 of its 61 rows came apart, the description
    // truncating at the comma and every field after it shifting one column left, so quantity was
    // read out of the wrong cell.
    //
    // Rows are kept as arrays of whatever width they arrived at. Tabula's output is routinely
    // ragged - short rows, trailing empty cells - so nothing here assumes a rectangle, and reading
    // a cell that is not there gives an empty string instead of throwing.
    internal sealed class CsvTable
    {
        // Why the file could not be read, for the caller to show. This used to be a bare
        // "Error: File Does Not Exist!" dialog raised from a constructor, which left the caller
        // holding an apparently valid but empty table and no way to know why.
        public List<string> Errors { get; } = new();

        public List<string[]> Rows { get; } = new();

        // Widest row in the file, which is how many columns the estimator is asked to describe.
        public int ColumnCount { get; private set; }

        public static CsvTable Read(string path)
        {
            var table = new CsvTable();

            if (!File.Exists(path))
            {
                table.Errors.Add($"'{Path.GetFileName(path)}' does not exist, so there is no table " +
                                 "to import.");
                return table;
            }

            // No header record: only one of the four sample files has a header row, so the first
            // line is data until the estimator says otherwise. Bad data is kept rather than thrown
            // away, because a row that looks malformed to a parser is often a real part.
            var configuration = new CsvConfiguration(CultureInfo.CurrentCulture)
            {
                HasHeaderRecord = false,
                BadDataFound = null,
                MissingFieldFound = null,
                DetectColumnCountChanges = false
            };

            try
            {
                using var reader = new StreamReader(path);
                using var parser = new CsvParser(reader, configuration);

                while (parser.Read())
                {
                    string[] record = parser.Record ?? Array.Empty<string>();

                    // A trailing newline reads as one empty cell; that is not a part.
                    if (record.Length == 0 || record.All(string.IsNullOrWhiteSpace))
                        continue;

                    table.Rows.Add(record);

                    if (record.Length > table.ColumnCount)
                        table.ColumnCount = record.Length;
                }
            }
            catch (Exception ex)
            {
                table.Errors.Add($"Could not read '{Path.GetFileName(path)}': {ex.Message}");
                return table;
            }

            if (table.Rows.Count == 0)
                table.Errors.Add($"'{Path.GetFileName(path)}' has no rows to import.");

            return table;
        }

        // Built directly from rows already in memory - a PDF table Tabula extracted, rather than
        // something read off disk. Ragged rows are still expected, same as a CSV.
        public static CsvTable FromRows(IEnumerable<string[]> rows)
        {
            var table = new CsvTable();

            foreach (string[] record in rows)
            {
                table.Rows.Add(record);

                if (record.Length > table.ColumnCount)
                    table.ColumnCount = record.Length;
            }

            if (table.Rows.Count == 0)
                table.Errors.Add("No rows were selected to import.");

            return table;
        }

        // The cell at a row and column, or an empty string when the row is short. Ragged rows are
        // the norm here, so this is the only way cells are read.
        public string Cell(int row, int column)
        {
            if (row < 0 || row >= Rows.Count)
                return string.Empty;

            string[] cells = Rows[row];

            return column < 0 || column >= cells.Length
                ? string.Empty
                : cells[column] ?? string.Empty;
        }
    }
}
