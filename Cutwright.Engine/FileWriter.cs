using ClosedXML.Excel;


namespace Cutwright
{
    internal class FileWriter
    {
        // Why writing the results failed, for the caller to show. Every save path here used to
        // swallow its exception - two to Console.WriteLine, which a windowed application has no
        // console for, and one to an explicit "DO nothing!" - so a failed export was
        // indistinguishable from a successful one. The likely case is mundane and constant: the
        // estimator still has the workbook open in Excel, the file is locked, and the save throws.
        //
        // Cleared by SendToFile, which is the entry point.
        public List<string> Errors { get; } = new();

        // Results are written into an existing Cutwright bill of materials, so a workbook that cannot be
        // opened, or is not one, is a genuine stop rather than something to paper over with a blank
        // one: the results would go nowhere and the save would then fail silently on top.
        private bool TryOpen(string path, out XLWorkbook workbook)
        {
            try
            {
                workbook = new XLWorkbook(path);
            }
            catch (Exception e)
            {
                Errors.Add($"Could not open '{System.IO.Path.GetFileName(path)}' to write results into: " +
                           $"{e.Message}");
                workbook = null!;
                return false;
            }

            string? refusal = BomWorkbook.Refusal(workbook, path);
            if (refusal is not null)
            {
                Errors.Add(refusal);
                workbook.Dispose();
                workbook = null!;
                return false;
            }

            return true;
        }

        // SaveAs rather than Save: Save throws for a workbook that was not loaded from a file, so
        // writing to a path that did not already exist could never have worked.
        private bool TrySave(XLWorkbook workbook, string path)
        {
            try
            {
                workbook.SaveAs(path);
                return true;
            }
            catch (Exception e)
            {
                Errors.Add($"Could not save '{System.IO.Path.GetFileName(path)}': {e.Message}" +
                           Environment.NewLine +
                           "If the file is open in Excel, close it and export again.");
                return false;
            }
        }

        public FileWriter()
        {
            this.Path = "";
            this.Input = new List<TNest>();
            this.Output = new List<TNest>();

            this.SheetNests = new List<PNest>();

        }

        public FileWriter(string path, List<TNest> input)
        {
            this.Path = path;
            this.Input = input;
            this.Output = new List<TNest>();

            this.SheetNests = new List<PNest>();

        }

        // Writes the whole Purchase sheet - materials, hardware, the lines from the Finishes sheet,
        // and the Total cost / Per unit cost footer - in one open/edit/save pass, rebuilt from
        // scratch every time rather than matched into whatever a previous save left lying around.
        // That is what makes a job with more distinct materials than any fixed template ever had
        // room for safe: there is no fixed capacity to overrun. Unit costs a purchaser typed in are
        // carried over to the rebuilt lines that have the same description.
        //
        // sourcePath is the bill of materials that was actually loaded and nested. Results are
        // written into an existing workbook - there is no DET section or title block to build one
        // from scratch - so saving under a filename that does not exist yet starts by copying
        // sourcePath there first. Left blank (or equal to path, or already the same file), this
        // behaves exactly as a save back into the file that was loaded.
        public void SendToFile(string path, List<PNest> SheetNests, List<TNest> TubeNests, string sourcePath = "",
                               int? units = null)
        {
            Errors.Clear();

            if (!System.IO.File.Exists(path))
            {
                if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath))
                {
                    Errors.Add($"Could not create '{System.IO.Path.GetFileName(path)}': the bill of " +
                               "materials it would be copied from is not available. Load a bill of " +
                               "materials first, then Save.");
                    return;
                }

                try
                {
                    System.IO.File.Copy(sourcePath, path);
                }
                catch (Exception e)
                {
                    Errors.Add($"Could not create '{System.IO.Path.GetFileName(path)}': {e.Message}");
                    return;
                }
            }

            if (!TryOpen(path, out XLWorkbook opened))
                return;

            // A using declaration rather than a block: the workbook has to be disposed, but the
            // body below is long and wrapping it would only reindent it. Disposal at end of scope
            // covers the early return in BuildPurchaseSection too.
            using var wb = opened;

            // A Units change in the app rescales quantities in memory, but every total in the file
            // multiplies by Job_Units, so the new count has to be written here or the saved totals
            // keep the old one. Before the purchase sheet is built, which reads them.
            if (units is int count && count > 0)
                BomWorkbook.WriteUnits(wb, count);

            BuildPurchaseSheet(wb, SheetNests, TubeNests);
            if (Errors.Count > 0)
                return;

            // The ends edited on the End Features tab live on a sheet of their own, keyed by the
            // item numbers the parts were read with.
            EndFeaturesSheet.Write(wb, TubeNests.SelectMany(n => n.Parts).Select(p => (p.line, p)));

            this.Path = path;

            TrySave(wb, this.Path);
        }

        public void AddList(List<TNest> list)
        {
            this.Input = list;
        }

        public void AddSheetNest(List<PNest> list)
        {
            this.SheetNests = list;
        }

        // One purchase row: a literal quantity, or a formula (a finish line, which reads its total
        // live off the Finishes sheet so changing an area or a coverage there changes what is bought).
        private readonly struct PurchaseLine
        {
            public required string Description { get; init; }
            public required string Uom { get; init; }
            public double QuantityValue { get; init; }
            public string? QuantityFormula { get; init; }
            public string? UnitCostFormula { get; init; }
        }

        private readonly record struct HardwareEntry(string Description, double Quantity);

        // Two lines of the Purchase sheet's default 11 pt font, with a little padding.
        private const double TwoLineRowHeight = 32;

        // Everything in the parts table that is not a nested material - hardware, a quantity with
        // no length - which FileParser does not read since it only takes rows that carry a length.
        //
        // Also returns where each description first appears in the parts table, materials included
        // - the top of the BOM is not necessarily alphabetical (a CSV import or a hand-edited job
        // keeps whatever order it arrived in), so this is what lets the purchase list match that
        // order rather than assuming one.
        private void CollectPartExtras(XLWorkbook wb, out List<HardwareEntry> hardware,
            out Dictionary<string, int> descriptionOrder)
        {
            hardware = new List<HardwareEntry>();
            descriptionOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var quantityByDescription = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var hardwareOrder = new List<string>();

            foreach (BomWorkbook.PartRow row in BomWorkbook.ReadParts(wb, Errors))
            {
                string description = row.Description.Trim();
                if (description.Length == 0)
                    continue;

                if (row.Total > 0 && !descriptionOrder.ContainsKey(description))
                    descriptionOrder[description] = row.Row;

                // A real material row is already represented by PNestList/TNestList.
                if (row.Length > 0 || row.Width > 0 || row.Total <= 0)
                    continue;

                if (!quantityByDescription.ContainsKey(description))
                    hardwareOrder.Add(description);

                quantityByDescription[description] = quantityByDescription.GetValueOrDefault(description) + row.Total;
            }

            foreach (string description in hardwareOrder)
                hardware.Add(new HardwareEntry(description, quantityByDescription[description]));
        }

        // Unit costs already typed into the Purchase table, by description.
        private static Dictionary<string, double> ReadUnitCosts(XLWorkbook wb)
        {
            var costs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (!BomWorkbook.TryGetTable(wb, BomWorkbook.PurchaseTable, out IXLTable table) || table.DataRange is null)
                return costs;

            var columns = BomWorkbook.Columns(table);
            if (!columns.TryGetValue(BomWorkbook.PurDescription, out int descriptionColumn) ||
                !columns.TryGetValue(BomWorkbook.PurUnitCost, out int costColumn))
                return costs;

            foreach (IXLRangeRow dataRow in table.DataRange.Rows())
            {
                IXLCell cost = table.Worksheet.Cell(dataRow.RowNumber(), costColumn);

                // A formula cell is a finish line's link back to the Finishes sheet, not something
                // typed in, and it is rebuilt anyway.
                if (cost.HasFormula)
                    continue;

                string description = BomWorkbook.Text(table.Worksheet.Cell(dataRow.RowNumber(), descriptionColumn));
                if (description.Length > 0 && BomWorkbook.TryNumber(cost, out double value))
                    costs.TryAdd(description, value);
            }

            return costs;
        }

        // One line per treatment on the Finishes sheet that has something to buy, each pointing at that
        // sheet's own cells.
        private List<PurchaseLine> CollectFinishLines(XLWorkbook wb)
        {
            var lines = new List<PurchaseLine>();

            if (!BomWorkbook.TryGetTable(wb, BomWorkbook.FinishTable, out IXLTable table) || table.DataRange is null)
                return lines;

            var columns = BomWorkbook.Columns(table);
            foreach (string required in new[] { BomWorkbook.FinItem, BomWorkbook.FinTotalQty, BomWorkbook.FinUnit, BomWorkbook.FinUnitCost })
            {
                if (!columns.ContainsKey(required))
                {
                    Errors.Add($"The {BomWorkbook.FinishTable} table has no '{required}' column.");
                    return lines;
                }
            }

            IXLWorksheet sheet = table.Worksheet;
            string sheetName = "'" + sheet.Name.Replace("'", "''") + "'";
            columns.TryGetValue(BomWorkbook.FinSpecification, out int specificationColumn);

            foreach (IXLRangeRow dataRow in table.DataRange.Rows())
            {
                int r = dataRow.RowNumber();
                string item = BomWorkbook.Text(sheet.Cell(r, columns[BomWorkbook.FinItem]));
                if (item.Length == 0)
                    continue;

                // A starter row nobody has filled in (no area, no each-per-unit) works out to nothing
                // to buy, so it is not a purchase line.
                if (!BomWorkbook.TryNumber(sheet.Cell(r, columns[BomWorkbook.FinTotalQty]), out double totalQty) || totalQty <= 0)
                    continue;

                string specification = specificationColumn > 0 ? BomWorkbook.Text(sheet.Cell(r, specificationColumn)) : "";
                string unit = BomWorkbook.Text(sheet.Cell(r, columns[BomWorkbook.FinUnit]));

                string total = $"{sheetName}!{sheet.Cell(r, columns[BomWorkbook.FinTotalQty]).Address.ToStringFixed(XLReferenceStyle.A1, false)}";
                string cost = $"{sheetName}!{sheet.Cell(r, columns[BomWorkbook.FinUnitCost]).Address.ToStringFixed(XLReferenceStyle.A1, false)}";
                string unitCell = $"{sheetName}!{sheet.Cell(r, columns[BomWorkbook.FinUnit]).Address.ToStringFixed(XLReferenceStyle.A1, false)}";

                // Things bought whole are rounded up; a weight or an area is left as it works out.
                bool whole = unit.Equals("each", StringComparison.OrdinalIgnoreCase) ||
                             unit.Equals("can", StringComparison.OrdinalIgnoreCase);

                lines.Add(new PurchaseLine
                {
                    Description = specification.Length > 0 ? item + " - " + specification : item,
                    Uom = unit,
                    QuantityFormula = whole ? $"=ROUNDUP({total},0)" : $"={total}",
                    UnitCostFormula = $"=IF({cost}=\"\",\"\",{cost})",
                });
            }

            return lines;
        }

        // Builds the whole Purchase sheet from scratch: materials, then hardware, in the same order
        // the parts table is in, with the finish lines last, followed by a footer sized to wherever
        // the last line landed.
        private void BuildPurchaseSheet(XLWorkbook wb, List<PNest> sheetNests, List<TNest> tubeNests)
        {
            CollectPartExtras(wb, out List<HardwareEntry> hardware, out Dictionary<string, int> descriptionOrder);
            List<PurchaseLine> finishLines = CollectFinishLines(wb);
            if (Errors.Count > 0)
                return;

            Dictionary<string, double> typedCosts = ReadUnitCosts(wb);

            // Paired with the raw parts-table description (before a sheet/tube's size suffix is
            // appended) purely so each line can be ranked by where that description first appears -
            // the top of the BOM is not always alphabetical, so that is looked up rather than assumed.
            var lines = new List<(PurchaseLine Line, string RawDescription)>();

            foreach (TNest tube in tubeNests)
            {
                if (tube.Description is null)
                    continue;

                string uom;
                double quantity;

                // Bought by the piece: the parts themselves, each, so the description - not the
                // unit, which is EA either way - says they are not sticks. Same wording as sheets.
                if (tube.Purchase == StickPurchase.Pieces)
                {
                    lines.Add((new PurchaseLine
                    {
                        Description = tube.Description + " (CUT PARTS)",
                        Uom = "EA",
                        QuantityValue = tube.PurchaseQuantity,
                    }, tube.Description));
                    continue;
                }

                UOM partUom = tube.Parts.First().PartUOM;
                if (partUom == UOM.None)
                {
                    uom = "####";
                    quantity = 0;
                }
                else if (partUom == UOM.EA)
                {
                    uom = "EA";
                    quantity = tube.Sticks.Count;
                }
                else
                {
                    uom = "FT";
                    quantity = tube.Parts.Sum(p => p.length * p.quantity) / 12.0;
                }

                // Whatever length this group actually cut from. A catalog length keeps its usual
                // suffix; a "Smallest Drop" group's length was worked out from its own parts, so it
                // goes on its own line under the material as the size to quote.
                lines.Add((new PurchaseLine
                {
                    Description = tube.SizeToSmallestDrop
                        ? tube.Description + $"\nLength Needed = {tube.StickLength:0.##}\""
                        : tube.Description + $"( x{tube.StickLength:0.##})",
                    Uom = uom,
                    QuantityValue = quantity,
                }, tube.Description));
            }

            foreach (PNest sheet in sheetNests)
            {
                if (sheet.Description is null)
                    continue;

                // Both units are per-each - one whole sheet, or one finished piece - so the
                // description, not the unit, is what tells them apart. A catalog size keeps its
                // usual "(LxW)" suffix; a "Smallest Drop" sheet was sized from the group's own
                // parts, so its size goes on its own line under the material as the size to quote.
                string description = sheet.Purchase == SheetPurchase.Pieces
                    ? sheet.Description + " (CUT PARTS)"
                    : sheet.SizeToSmallestDrop
                        ? sheet.Description + $"\nSheet Size Needed = {sheet.SheetLength:0.##}\" x {sheet.SheetWidth:0.##}\""
                        : sheet.Description + " (" + sheet.SheetLength + "x" + sheet.SheetWidth + ")";

                lines.Add((new PurchaseLine
                {
                    Description = description,
                    Uom = "EA",
                    QuantityValue = sheet.PurchaseQuantity,
                }, sheet.Description));
            }

            foreach (HardwareEntry entry in hardware)
            {
                lines.Add((new PurchaseLine
                {
                    Description = entry.Description,
                    Uom = "EA",
                    QuantityValue = entry.Quantity,
                }, entry.Description));
            }

            // Same relative order as the parts table - one line per unique material description. A
            // description this save's materials/hardware brought in but that is not actually in the
            // table (should not happen, but nothing here guarantees it) sorts after everything that
            // is, rather than throwing.
            var orderedLines = lines
                .OrderBy(t => descriptionOrder.TryGetValue(t.RawDescription, out int row) ? row : int.MaxValue)
                .Select(t => t.Line)
                .Concat(finishLines)
                .ToList();

            // Replace the sheet where it was, so the tab order survives a re-save.
            int position = wb.TryGetWorksheet(BomWorkbook.PurchaseSheet, out IXLWorksheet? existing)
                ? existing.Position
                : wb.Worksheets.Count + 1;
            existing?.Delete();

            IXLWorksheet ws = wb.AddWorksheet(BomWorkbook.PurchaseSheet, position);

            ws.Cell("A1").Value = "PURCHASE";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 14;
            ws.Cell("A2").Value = "Rebuilt by Cutwright from the nesting results each time the file is saved. " +
                                  "Unit costs you type in are kept; finish lines follow the Finishes sheet.";
            ws.Cell("A2").Style.Font.Italic = true;

            const int header = 4;
            string[] headers =
            {
                BomWorkbook.PurLine, BomWorkbook.PurQty, BomWorkbook.PurUnit,
                BomWorkbook.PurDescription, BomWorkbook.PurUnitCost, BomWorkbook.PurExtended,
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(header, i + 1).Value = headers[i];

            int row = header;
            int lineNumber = 0;
            foreach (PurchaseLine line in orderedLines)
            {
                row++;
                lineNumber++;

                ws.Cell(row, 1).Value = lineNumber;

                if (line.QuantityFormula is not null)
                    ws.Cell(row, 2).FormulaA1 = line.QuantityFormula;
                else
                    ws.Cell(row, 2).Value = line.QuantityValue;

                ws.Cell(row, 3).Value = line.Uom;
                ws.Cell(row, 4).Value = line.Description;

                // A second line in the cell only shows with wrap on, and the row is made tall enough
                // for both lines here rather than left to Excel's autofit.
                if (line.Description.Contains('\n'))
                {
                    ws.Cell(row, 4).Style.Alignment.WrapText = true;
                    ws.Row(row).Height = TwoLineRowHeight;
                }

                if (line.UnitCostFormula is not null)
                    ws.Cell(row, 5).FormulaA1 = line.UnitCostFormula;
                else if (typedCosts.TryGetValue(line.Description, out double cost))
                    ws.Cell(row, 5).Value = cost;

                // Blank until a purchaser fills in a cost, rather than showing $0 for every line
                // before any quote has come back.
                ws.Cell(row, 6).FormulaA1 = $"=IF(E{row}=\"\",\"\",B{row}*E{row})";
            }

            // A table needs a data row, so a job with nothing to buy gets one blank line.
            if (orderedLines.Count == 0)
            {
                row++;
                ws.Cell(row, 6).FormulaA1 = $"=IF(E{row}=\"\",\"\",B{row}*E{row})";
            }

            IXLTable table = ws.Range(header, 1, row, headers.Length).CreateTable(BomWorkbook.PurchaseTable);
            table.Theme = XLTableTheme.TableStyleMedium2;

            ws.Range(header + 1, 2, row, 2).Style.NumberFormat.Format = "0.##";
            ws.Range(header + 1, 5, row + 3, 6).Style.NumberFormat.Format = "$#,##0.00";

            // Two rows down from the table so it does not grow to swallow the footer.
            int footer = row + 2;
            ws.Cell(footer, 4).Value = "Total cost";
            ws.Cell(footer, 6).FormulaA1 = $"=SUM(F{header + 1}:F{row})";
            ws.Cell(footer + 1, 4).Value = "Per unit cost";
            ws.Cell(footer + 1, 6).FormulaA1 = $"=IF({BomWorkbook.JobUnits}>0,F{footer}/{BomWorkbook.JobUnits},0)";
            ws.Range(footer, 4, footer + 1, 6).Style.Font.Bold = true;

            double[] widths = { 7, 10, 8, 60, 12, 14 };
            for (int i = 0; i < widths.Length; i++)
                ws.Column(i + 1).Width = widths[i];

            ws.Range(header, 1, header, headers.Length).Style.Alignment.WrapText = true;
            ws.SheetView.FreezeRows(header);
            ws.PageSetup.FitToPages(1, 0);
        }

        private string Path;



        private List<TNest> Input;
        private List<TNest> Output;

        private List<PNest> SheetNests;

    }
}
