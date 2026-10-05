using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Cutwright
{
    // Shows a table pulled off a customer drawing - a CSV from the standalone Tabula app, or rows
    // Tabula's own extraction picked out of a PDF directly - asks what each column holds, and
    // builds the bill of materials from the answer.
    //
    // Was TableFormatter, which said nothing about CSVs or bills of materials, and carried eight
    // named ComboBoxes with eight copy-pasted if/else blocks behind them. The selectors and the
    // preview columns are both generated from the table now, so a table wider than eight columns
    // works instead of being silently truncated.
    public partial class CsvImportWindow : Window
    {
        private const double ColumnWidth = 130.0;

        private readonly List<ComboBox> roleSelectors = new();
        private readonly CsvTable table;
        private readonly string path;

        public CsvImportWindow(string path) : this(CsvTable.Read(path), path)
        {
        }

        // sourceName is what gets logged and shown in warnings - a CSV's own path when this came
        // from Import BOM from CSV, or a made-up label (which page(s) of which PDF) when it came
        // from Import BOM from PDF. Neither constructor reads a file itself here; CsvTable.Read
        // already happened, or there was no file to read to begin with.
        internal CsvImportWindow(CsvTable table, string sourceName)
        {
            InitializeComponent();

            this.path = sourceName;
            this.table = table;

            // CsvTable reports rather than showing, so an empty table comes with a reason instead
            // of just looking like a file with nothing in it.
            if (table.Errors.Count > 0)
            {
                foreach (string error in table.Errors)
                    Log.Warn($"Table import: {error}");

                MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, table.Errors),
                    "Could not read the table", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            BuildColumnControls();

            PreviewGrid.ItemsSource = table.Rows;
            RowCountText.Text = $"{table.Rows.Count} row(s), {table.ColumnCount} column(s)";
            BuildButton.IsEnabled = table.Rows.Count > 0;
        }

        // One role selector and one preview column per column in the file.
        private void BuildColumnControls()
        {
            for (int i = 0; i < table.ColumnCount; i++)
            {
                var selector = new ComboBox
                {
                    Width = ColumnWidth,
                    ItemsSource = CsvColumnRoles.Labels,
                    SelectedItem = CsvColumnRoles.LabelOf(CsvColumnRole.Ignore),
                    Margin = new Thickness(0, 0, 1, 0)
                };

                roleSelectors.Add(selector);
                ColumnRolePanel.Children.Add(selector);

                PreviewGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = i + 1,
                    Width = ColumnWidth,

                    // Rows are string arrays, so the cell is the element at this index. Bound this
                    // way rather than through a row type with a fixed set of properties, which is
                    // what forced the eight column limit.
                    Binding = new Binding($"[{i}]")
                });
            }
        }

        private void BuildBillOfMaterials(object sender, RoutedEventArgs e)
        {
            var roles = roleSelectors
                .Select(selector => CsvColumnRoles.FromLabel(selector.SelectedItem as string))
                .ToList();

            var builder = new BomFromCsv();
            BillOfMaterials bom = builder.Build(table, roles);

            Log.Info($"Table import: {System.IO.Path.GetFileName(path)} -> {bom.parts.Count} part(s) " +
                     $"from {table.Rows.Count} row(s).");

            foreach (string warning in builder.Warnings)
                Log.Warn($"Table import: {warning}");

            if (builder.Warnings.Count > 0)
            {
                //Shown before the save dialog, not after, so the estimator can cancel out of writing
                //a bill of materials they can already see is wrong.
                var proceed = MessageBox.Show(
                    string.Join(Environment.NewLine + Environment.NewLine, builder.Warnings) +
                    Environment.NewLine + Environment.NewLine + "Build it anyway?",
                    "Import notes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.OK)
                    return;
            }

            //Writing is the caller's decision. BomFromCsv used to write the workbook itself, as a
            //side effect of building the list, which meant there was no way to build one without
            //also being asked where to save it.
            if (BomDetailsWindow.AskAndWrite(bom, this))
                this.Close();
        }
    }
}
