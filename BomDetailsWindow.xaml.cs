using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace Cutwright
{
    // The title block of a bill of materials, typed in.
    //
    // There is nothing to read these out of. A customer's drawing does not know which of our
    // estimates this is, what we call the job, or how many units we are quoting - so extracting
    // them was never possible even in principle, and the whole block used to come out blank.
    //
    // Units matters more than the rest: the sheet's QTY column is a formula pointing at it and
    // every Total multiplies by that, so a blank unit count made every total on the sheet zero.
    public partial class BomDetailsWindow : Window
    {
        private readonly BillOfMaterials bom;

        //Internal because BillOfMaterials is internal; the class itself has to be public for the
        //XAML-generated half of it.
        internal BomDetailsWindow(BillOfMaterials bom)
        {
            InitializeComponent();

            this.bom = bom;

            // Prefilled from whatever is already known, so reformatting a bill of materials that
            // came in with a unit count does not ask for it again.
            CustomerBox.Text = bom.CustomerName;
            JobEstBox.Text = bom.JobEst;
            PreviousJobEstBox.Text = bom.PrevJobEst;
            UnitsBox.Text = bom.Units > 0 ? bom.Units.ToString(CultureInfo.CurrentCulture) : string.Empty;
            DescriptionBox.Text = bom.Description;
            DrawingNumberBox.Text = bom.DrawingNo;
            EngineerBox.Text = bom.EngName;
            CheckedByBox.Text = bom.CheckedBy;

            CustomerBox.Focus();
        }

        //Asks for the title block, then for somewhere to put it, then writes. Shared by both ways
        //into the template - building one from a customer's CSV, and reformatting an existing
        //spreadsheet - so the two cannot drift into asking for different things.
        //
        //Returns false when the estimator backs out of either dialog.
        internal static bool AskAndWrite(BillOfMaterials bom, Window owner)
        {
            var details = new BomDetailsWindow(bom) { Owner = owner };

            if (details.ShowDialog() != true)
                return false;

            var dlg = new SaveFileDialog
            {
                Filter = "Microsoft Excel Spreadsheet (*.xlsx)|*.xlsx",
                Title = "Save the bill of materials",
                FileName = string.IsNullOrWhiteSpace(bom.JobEst) ? "Bill of Materials" : bom.JobEst
            };

            if (dlg.ShowDialog() != true)
                return false;

            bom.WriteToFile(dlg.FileName);

            if (bom.Errors.Count > 0)
            {
                foreach (string error in bom.Errors)
                    Log.Warn($"Bill of materials: {error}");

                MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, bom.Errors),
                    "Could not write the bill of materials", MessageBoxButton.OK, MessageBoxImage.Warning);

                return false;
            }

            Log.Info($"Wrote a bill of materials with {bom.parts.Count} part(s) to " +
                     $"{System.IO.Path.GetFileName(bom.Filename)}.");

            return true;
        }

        private void Continue(object sender, RoutedEventArgs e)
        {
            string units = UnitsBox.Text.Trim();

            // The one field that is refused rather than accepted empty, because the arithmetic on
            // the whole sheet depends on it and a zero is not a plausible thing to quote.
            if (!int.TryParse(units, NumberStyles.Integer, CultureInfo.CurrentCulture, out int count) || count <= 0)
            {
                ValidationText.Text = "Units must be a whole number above zero.";
                UnitsBox.Focus();
                UnitsBox.SelectAll();
                return;
            }

            bom.CustomerName = CustomerBox.Text.Trim();
            bom.JobEst = JobEstBox.Text.Trim();
            bom.PrevJobEst = PreviousJobEstBox.Text.Trim();
            bom.Units = count;
            bom.Description = DescriptionBox.Text.Trim();
            bom.DrawingNo = DrawingNumberBox.Text.Trim();
            bom.EngName = EngineerBox.Text.Trim();
            bom.CheckedBy = CheckedByBox.Text.Trim();

            // The date is the day the bill of materials is made, which is not something to type.
            bom.Date = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);

            DialogResult = true;
        }
    }
}
