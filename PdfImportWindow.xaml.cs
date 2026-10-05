using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace Cutwright
{
    // Lets the estimator pick which ruled table(s) Tabula found in a PDF are the actual bill of
    // materials, then hands the merged rows to CsvImportWindow - the exact same column-role
    // assignment and build/write path Import BOM from CSV already uses, so nothing downstream of
    // "here are some rows" needs to know these came from a PDF rather than a CSV.
    public partial class PdfImportWindow : Window
    {
        private readonly string path;
        private readonly ObservableCollection<PdfTableRow> rows = new();

        public PdfImportWindow(string path)
        {
            InitializeComponent();
            this.path = path;

            List<PdfTableCandidate> candidates;
            List<string> errors;

            // Extraction runs synchronously - a couple of seconds on a real multi-page drawing,
            // which is short enough that a busy cursor is all this needs, not a background worker.
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                candidates = PdfBomImport.FindTables(path, out errors);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            foreach (PdfTableCandidate candidate in candidates)
                rows.Add(new PdfTableRow { Candidate = candidate });

            CandidatesGrid.ItemsSource = rows;

            if (rows.Count == 0)
            {
                errors.Add("No ruled tables were found. This drawing's table likely has no " +
                           "visible grid lines - use Import BOM from CSV with a manual Tabula " +
                           "export instead.");
                ImportButton.IsEnabled = false;
            }

            if (errors.Count > 0)
            {
                StatusText.Text = string.Join(" ", errors);
                StatusText.Visibility = Visibility.Visible;
            }
        }

        private void ImportSelected(object sender, RoutedEventArgs e)
        {
            var selected = rows.Where(r => r.Selected).ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show("Check at least one table to import.", "Nothing selected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var mergedRows = selected.SelectMany(r => r.Candidate.Rows);
            var table = CsvTable.FromRows(mergedRows);

            string fileName = System.IO.Path.GetFileName(path);
            string pages = string.Join(", ", selected.Select(r => r.Candidate.Page));

            Log.Info($"PDF import: {fileName} -> {selected.Count} table(s) from page(s) {pages}, " +
                     $"{table.Rows.Count} row(s) merged.");

            new CsvImportWindow(table, $"{fileName}, page(s) {pages}").Show();
            this.Close();
        }
    }

    // One row of the candidate-table picker. Plain, not INotifyPropertyChanged - nothing reads
    // Selected live while the grid is open, only ImportSelected, once, after the checkboxes are set.
    internal sealed class PdfTableRow
    {
        public required PdfTableCandidate Candidate { get; init; }
        public bool Selected { get; set; }

        public int Page => Candidate.Page;
        public int RowCount => Candidate.RowCount;
        public int ColumnCount => Candidate.ColumnCount;
        public string Preview => Candidate.Preview;
    }
}
