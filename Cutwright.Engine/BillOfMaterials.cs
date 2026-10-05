using ClosedXML.Excel;
//using SolidWorks.Interop.sldworks;
//using SolidWorks.Interop.swconst;



namespace Cutwright
{

    internal class BillOfMaterials
    {
        public List<string> Errors { get; } = new();

        public BillOfMaterials()
        {
            CustomerName = "";
            JobEst = "";
            Units = 0;
            Description = "";
            PrevJobEst = "";
            EngName = "";
            DrawingNo = "";
            Date = "";
            CheckedBy = "";
            RevisionNo = "";
            Filename = "";

            this.parts = new List<Part>();


        }

        public void ReadFile(string FileName)
        {


            this.Units = 0;

            const int RowCount = 200;


            int PartNumberColumnIndex = 1;
            int DescColumnIndex = 2;
            int QuantityColumnIndex = 3;
            int JobQuantityColumnIndex = 4;
            int LengthColumnIndex = 5;
            int WidthColumnIndex = 6;


            try
            {
                using var wb = new XLWorkbook(FileName);
                IXLWorksheet ws = wb.Worksheet(1);



                for (int i = 2; i < RowCount + 3; i++)
                {
                    Part pt = new Part();

                    string tmp = ws.Cell(i, PartNumberColumnIndex).GetValue<string>();

                    if (!ws.Cell(i, PartNumberColumnIndex).IsEmpty())
                        pt.PartNumber = ws.Cell(i, PartNumberColumnIndex).GetValue<string>();
                    if (!ws.Cell(i, DescColumnIndex).IsEmpty())
                        pt.Description = ws.Cell(i, DescColumnIndex).GetValue<string>();
                    if (!ws.Cell(i, WidthColumnIndex).IsEmpty())
                        pt.width = ws.Cell(i, WidthColumnIndex).GetValue<float>();
                    if (!ws.Cell(i, LengthColumnIndex).IsEmpty())
                        pt.length = ws.Cell(i, LengthColumnIndex).GetValue<float>();
                    if (!ws.Cell(i, QuantityColumnIndex).IsEmpty())
                        pt.quantity = ws.Cell(i, QuantityColumnIndex).GetValue<int>();
                    if (!ws.Cell(i, JobQuantityColumnIndex).IsEmpty())
                        pt.JobQuantity = ws.Cell(i, JobQuantityColumnIndex).GetValue<int>();



                    this.parts.Add(pt);
                }

                
                Part? first = this.parts.FirstOrDefault(p => p.quantity > 0 && p.JobQuantity > 0);

                if (first is not null)
                    this.Units = first.JobQuantity / first.quantity;
            }
            catch (Exception ex)
            {
                Errors.Add($"Could not read '{System.IO.Path.GetFileName(FileName)}': {ex.Message}");
                return;
            }



        }

        // Writes the bill of materials as a Cutwright format 2 workbook (see BomWorkbook and
        // docs/bom-format-v2.md) to a path the caller has already chosen. This used to open its own
        // SaveFileDialog, which made a model class the last thing in the codebase raising UI and
        // meant a bill of materials could not be built without also being asked where to put it.
        //
        // With no parts it writes the blank template: the job block, an empty parts table and the
        // Finishes sheet, ready to fill in.
        public void WriteToFile(string path)
        {
            // The way an estimator actually lays a BOM out on the page - Tubes, Pipe, Other
            // Sticks, Plate Steel, Sheet Steel, Other Sheet Goods, then Purchased - not
            // alphabetically. See BomLineOrder for the full ruleset.
            this.parts = this.parts.OrderBy(p => p, BomLineOrder.ByRule).ToList();

            // Lines with no quantity are not part of the job.
            this.parts.RemoveAll(p => p.quantity == 0);

            try
            {
                using XLWorkbook wb = new XLWorkbook();
                BomWorkbook.WriteTemplate(wb, this, this.parts);

                // DET items are numbered 1..n down the sheet, whatever Part.line holds.
                EndFeaturesSheet.Write(wb, this.parts.Select((p, index) => (index + 1, p)));

                string target = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : path + ".xlsx";

                wb.SaveAs(target);
                this.Filename = target;
            }
            catch (Exception e)
            {
                Errors.Add($"Could not write the bill of materials: {e.Message}");
            }
        }

        public string CustomerName { get; set; }
        public string JobEst { get; set; }
        public int Units { get; set; }
        public string Description { get; set; }
        public string PrevJobEst { get; set; }
        public string EngName { get; set; }
        public string DrawingNo { get; set; }
        public string Date { get; set; }
        public string CheckedBy { get; set; }
        public string RevisionNo { get; set; }

        public List<Part> parts { get; set; }

        public string Filename { get; set; }



    }
}
