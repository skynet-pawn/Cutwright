using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace Cutwright
{
    // Types in a bill of materials with no source file to read one from - an internal fixture, a
    // quick quote, anything that starts from nothing rather than a CSV export or an existing
    // spreadsheet. Deliberately its own window rather than folded into Load/Save: both of those
    // work on parts that already exist somewhere to read (FileParser.Parse, an app-written
    // template), and this is the one path where nothing does yet.
    //
    // Mirrors CsvImportWindow.BuildBillOfMaterials - typed grid rows stand in for CSV rows, and
    // everything downstream (title block, per-unit quantity contract, DET ordering, the write
    // itself) is the exact same path BomFromCsv already uses.
    public partial class NewBomWindow : Window
    {
        private readonly ObservableCollection<NewBomPartRow> rows = new();

        public NewBomWindow()
        {
            InitializeComponent();
            PartsGrid.ItemsSource = rows;
        }

        private void BuildBillOfMaterials(object sender, RoutedEventArgs e)
        {
            // The grid always carries one trailing blank row for CanUserAddRows to work from;
            // that row, and any other left untouched, has no description and is left out rather
            // than counted as a warning.
            var typed = rows.Where(r => !string.IsNullOrWhiteSpace(r.Description)).ToList();

            int noQuantity = typed.Count(r => r.Qty <= 0);

            var bom = new BillOfMaterials
            {
                // Same contract BomFromCsv uses: the quantity typed here is per-unit, and
                // WriteToFile writes it straight into the Per unit column, multiplied by Units for
                // the row's Total. Units defaults to 1 so an untouched title block reproduces
                // exactly what was typed, with nothing multiplied on top of it.
                Units = 1
            };

            foreach (var row in typed.Where(r => r.Qty > 0))
            {
                bom.parts.Add(new Part(0, row.Qty, row.Description.Trim(),
                    (row.PartNumber ?? string.Empty).Trim(), row.Width, row.Length, 0f));
            }

            if (bom.parts.Count == 0)
            {
                MessageBox.Show(
                    noQuantity > 0
                        ? $"{noQuantity} row(s) have a description but no quantity above zero, and there is nothing else to build."
                        : "Type in at least one part before building.",
                    "Nothing to build", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (noQuantity > 0)
            {
                var proceed = MessageBox.Show(
                    $"{noQuantity} row(s) have a description but no quantity above zero and will be left out. Build the rest anyway?",
                    "Some rows will be skipped", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.OK)
                    return;
            }

            Log.Info($"New BOM: typed {bom.parts.Count} part(s) by hand.");

            if (BomDetailsWindow.AskAndWrite(bom, this))
                this.Close();
        }
    }

    // One row of the manual-entry grid. Plain auto-properties, not INotifyPropertyChanged -
    // nothing reads these live while the grid is open, only BuildBillOfMaterials, once, after
    // editing commits.
    internal sealed class NewBomPartRow
    {
        public string Description { get; set; } = string.Empty;
        public string PartNumber { get; set; } = string.Empty;
        public int Qty { get; set; }
        public float Length { get; set; }
        public float Width { get; set; }
    }
}
