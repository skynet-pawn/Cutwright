using ClosedXML.Excel;
using System.Globalization;

namespace Cutwright
{
    // The Cutwright bill of materials workbook, format 2: what is on each sheet, what it is called, and
    // how to find it again.
    //
    // Everything is found by name, never by cell address. The job block is a set of named cells and
    // the parts, finishes and purchase lines are Excel tables, so a row inserted by hand, a sorted
    // table, an extra column or a moved block does not stop the file being read. There are no merged
    // cells anywhere.
    //
    // Cutwright reads only this format. Cutwright_BomFormat is the marker: a workbook without it is not a Cutwright
    // bill of materials, and the reader says so rather than guessing at some other layout. See
    // docs/bom-format-v2.md.
    internal static class BomWorkbook
    {
        public const string FormatName = "Cutwright_BomFormat";
        public const int FormatVersion = 2;

        public const string BomSheet = "BOM";
        public const string FinishesSheet = "Finishes";
        public const string PurchaseSheet = "Purchase";

        public const string PartsTable = "BomParts";
        public const string FinishTable = "FinishLines";
        public const string PurchaseTable = "PurchaseLines";

        public const string JobCustomer = "Job_Customer";
        public const string JobEst = "Job_Est";
        public const string JobPrevEst = "Job_PrevEst";
        public const string JobUnits = "Job_Units";
        public const string JobBy = "Job_By";
        public const string JobDrawing = "Job_Drawing";
        public const string JobCheckedBy = "Job_CheckedBy";
        public const string JobDescription = "Job_Description";
        public const string JobDate = "Job_Date";
        public const string JobRevision = "Job_Revision";

        // Parts table columns, matched by header text.
        public const string ColItem = "Item";
        public const string ColPerUnit = "Per unit";
        public const string ColUnits = "Units";
        public const string ColTotal = "Total";
        public const string ColDescription = "Description";
        public const string ColLength = "Length";
        public const string ColWidth = "Width";
        public const string ColPartNumber = "Part number";

        // Finishes table columns.
        public const string FinItem = "Item";
        public const string FinSpecification = "Specification";
        public const string FinBasis = "Basis";
        public const string FinArea = "Area per unit (sq ft)";
        public const string FinCoverage = "Coverage (sq ft per unit)";
        public const string FinWaste = "Waste %";
        public const string FinEach = "Each per unit";
        public const string FinQtyPerUnit = "Qty per unit";
        public const string FinUnit = "Unit";
        public const string FinTotalQty = "Total qty";
        public const string FinUnitCost = "Unit cost";
        public const string FinExtended = "Extended cost";
        public const string FinNotes = "Notes";

        // Purchase table columns.
        public const string PurLine = "Line";
        public const string PurQty = "Qty";
        public const string PurUnit = "Unit";
        public const string PurDescription = "Description";
        public const string PurUnitCost = "Unit cost";
        public const string PurExtended = "Extended cost";

        public const string BasisCoverage = "Coverage";
        public const string BasisPerArea = "Per area";
        public const string BasisEach = "Each";

        private const int PartsHeaderRow = 9;

        // ------------------------------------------------------------------ finding things

        // 0 for a workbook that is not a Cutwright bill of materials.
        public static int FormatOf(XLWorkbook workbook)
        {
            if (!workbook.DefinedNames.TryGetValue(FormatName, out IXLDefinedName? name))
                return 0;

            return int.TryParse(name.RefersTo.Trim().TrimStart('='), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int version) ? version : 0;
        }

        // Null when the file is fine to read; otherwise the sentence to show.
        public static string? Refusal(XLWorkbook workbook, string path)
        {
            string file = System.IO.Path.GetFileName(path);
            int version = FormatOf(workbook);

            if (version == 0)
                return $"'{file}' is not a Cutwright bill of materials. Use New BOM > Blank Template to make one " +
                       "to fill in, or import a CSV, PDF or SolidWorks parts list.";

            if (version > FormatVersion)
                return $"'{file}' was made by a newer version of Cutwright (BOM format {version}); this one reads " +
                       $"format {FormatVersion}.";

            if (!TryGetTable(workbook, PartsTable, out _))
                return $"'{file}' has no {PartsTable} table, so there is no part list to read.";

            return null;
        }

        public static IXLCell? NamedCell(XLWorkbook workbook, string name)
        {
            if (!workbook.DefinedNames.TryGetValue(name, out IXLDefinedName? defined))
                return null;

            return defined.Ranges.FirstOrDefault()?.FirstCell();
        }

        public static bool TryGetTable(XLWorkbook workbook, string name, out IXLTable table)
        {
            foreach (IXLWorksheet sheet in workbook.Worksheets)
            {
                if (sheet.Tables.TryGetTable(name, out IXLTable? found))
                {
                    table = found;
                    return true;
                }
            }

            table = null!;
            return false;
        }

        // Header text to absolute column number, case-insensitive.
        public static Dictionary<string, int> Columns(IXLTable table)
        {
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (IXLCell header in table.HeadersRow().Cells())
                columns.TryAdd(header.GetFormattedString().Trim(), header.Address.ColumnNumber);

            return columns;
        }

        public static string Text(IXLCell cell) => cell.GetFormattedString().Trim();

        public static bool TryNumber(IXLCell cell, out double value)
        {
            if (cell.TryGetValue(out value))
                return true;

            return double.TryParse(cell.GetFormattedString().Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        // ------------------------------------------------------------------ reading

        public sealed class PartRow
        {
            public int Row { get; init; }
            public int Item { get; init; }
            public double PerUnit { get; init; }
            public double Total { get; init; }
            public string Description { get; init; } = "";
            public double Length { get; init; }
            public double Width { get; init; }
            public string PartNumber { get; init; } = "";
        }

        // Writes the job's unit count into the Job_Units cell, which every part row's Units column
        // and the finish lines read, so the totals follow it. False when the workbook has no such
        // cell (not laid out around one, so none is invented).
        public static bool WriteUnits(XLWorkbook workbook, int units)
        {
            IXLCell? cell = NamedCell(workbook, JobUnits);
            if (cell is null)
                return false;

            cell.Value = units;
            return true;
        }

        public static int ReadUnits(XLWorkbook workbook)
        {
            IXLCell? cell = NamedCell(workbook, JobUnits);
            if (cell is not null && TryNumber(cell, out double units) && units >= 1)
                return (int)units;

            return 1;
        }

        public static string ReadJob(XLWorkbook workbook, string name)
        {
            IXLCell? cell = NamedCell(workbook, name);
            return cell is null ? "" : Text(cell);
        }

        // The rows of the parts table that describe a part - a row with no description and no length
        // is a blank one, and a row with no length is a purchased item (hardware), which the nesting
        // does not read but the purchase list does. Total is Per unit x the job's units, worked out
        // here rather than read back from the formula cell, so it does not depend on the file having
        // been recalculated by Excel.
        public static List<PartRow> ReadParts(XLWorkbook workbook, List<string> errors)
        {
            var rows = new List<PartRow>();

            if (!TryGetTable(workbook, PartsTable, out IXLTable table))
            {
                errors.Add($"No {PartsTable} table found.");
                return rows;
            }

            Dictionary<string, int> columns = Columns(table);

            foreach (string required in new[] { ColDescription, ColPerUnit, ColLength })
            {
                if (!columns.ContainsKey(required))
                {
                    errors.Add($"The {PartsTable} table has no '{required}' column.");
                    return rows;
                }
            }

            int units = ReadUnits(workbook);
            IXLRange? data = table.DataRange;
            if (data is null)
                return rows;

            IXLWorksheet sheet = table.Worksheet;
            int ordinal = 0;

            foreach (IXLRangeRow dataRow in data.Rows())
            {
                int r = dataRow.RowNumber();

                string description = Text(sheet.Cell(r, columns[ColDescription]));
                bool hasLength = TryNumber(sheet.Cell(r, columns[ColLength]), out double length) && length > 0;
                bool hasPerUnit = TryNumber(sheet.Cell(r, columns[ColPerUnit]), out double perUnit) && perUnit > 0;

                if (description.Length == 0 && !hasLength)
                    continue;

                ordinal++;

                int item = ordinal;
                if (columns.TryGetValue(ColItem, out int itemColumn) &&
                    TryNumber(sheet.Cell(r, itemColumn), out double itemValue) && itemValue >= 1)
                    item = (int)itemValue;

                double width = 0;
                if (columns.TryGetValue(ColWidth, out int widthColumn))
                    TryNumber(sheet.Cell(r, widthColumn), out width);

                string partNumber = columns.TryGetValue(ColPartNumber, out int partColumn)
                    ? Text(sheet.Cell(r, partColumn))
                    : "";

                rows.Add(new PartRow
                {
                    Row = r,
                    Item = item,
                    PerUnit = hasPerUnit ? perUnit : 0,
                    Total = hasPerUnit ? perUnit * units : 0,
                    Description = description,
                    Length = hasLength ? length : 0,
                    Width = width > 0 ? width : 0,
                    PartNumber = partNumber,
                });
            }

            return rows;
        }

        // ------------------------------------------------------------------ writing

        // Adds the BOM and Finishes sheets to an empty workbook. The Purchase sheet is not here: it
        // depends on nesting results, which do not exist until the first Save (FileWriter builds it).
        // Parts must already be in their final order and without zero-quantity lines.
        public static void WriteTemplate(XLWorkbook workbook, BillOfMaterials job, IReadOnlyList<Part> parts,
            Func<string, string>? setting = null)
        {
            WriteBomSheet(workbook, job, parts);
            WriteFinishesSheet(workbook, setting ?? CutwrightSettings.Get);

            workbook.DefinedNames.Add(FormatName, FormatVersion.ToString(CultureInfo.InvariantCulture));
            workbook.Worksheet(BomSheet).SetTabActive();
        }

        // Header text wraps, so a long heading grows its row instead of being cut off by a narrow column.
        private static void WrapHeader(IXLWorksheet ws, int row, int columns, double height)
        {
            IXLRange header = ws.Range(row, 1, row, columns);
            header.Style.Alignment.WrapText = true;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(row).Height = height;
        }

        private static readonly XLColor LabelColour = XLColor.FromHtml("#4A5361");
        private static readonly XLColor ValueFill = XLColor.FromHtml("#F4F6F8");

        private static void WriteBomSheet(XLWorkbook workbook, BillOfMaterials job, IReadOnlyList<Part> parts)
        {
            IXLWorksheet ws = workbook.AddWorksheet(BomSheet);

            ws.Cell("A1").Value = "BILL OF MATERIALS";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 20;

            // Label, value cell, defined name, initial value, and how many columns the value's shading
            // spans. The left group's values sit over the empty cells to their right, so a long
            // customer or description has room without merging anything; the right group is short
            // values (job number, date, units).
            var block = new (string Label, string Value, string Name, object Content, int Span)[]
            {
                ("A3", "B3", JobCustomer, job.CustomerName, 4),
                ("A4", "B4", JobDescription, job.Description, 4),
                ("A5", "B5", JobDrawing, job.DrawingNo, 4),
                ("A6", "B6", JobRevision, job.RevisionNo, 4),
                ("A7", "B7", JobCheckedBy, job.CheckedBy, 4),
                ("F3", "G3", JobEst, job.JobEst, 2),
                ("F4", "G4", JobPrevEst, job.PrevJobEst, 2),
                ("F5", "G5", JobDate, job.Date, 2),
                ("F6", "G6", JobUnits, job.Units > 0 ? job.Units : 1, 2),
                ("F7", "G7", JobBy, job.EngName, 2),
            };

            string[] labels =
            {
                "Customer", "Description", "Drawing number", "Revision", "Checked by",
                "Job / Est", "Previous job / Est", "Date", "Units", "By",
            };

            for (int i = 0; i < block.Length; i++)
            {
                var (label, value, name, content, span) = block[i];

                ws.Cell(label).Value = labels[i];
                ws.Cell(label).Style.Font.Bold = true;
                ws.Cell(label).Style.Font.FontColor = LabelColour;

                IXLCell cell = ws.Cell(value);
                if (content is int n)
                    cell.Value = n;
                else
                    cell.Value = content?.ToString() ?? "";

                IXLRange shade = ws.Range(cell.Address.RowNumber, cell.Address.ColumnNumber,
                    cell.Address.RowNumber, cell.Address.ColumnNumber + span - 1);
                shade.Style.Fill.BackgroundColor = ValueFill;
                shade.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

                workbook.DefinedNames.Add(name, cell.AsRange());
            }

            string[] headers = { ColItem, ColPerUnit, ColUnits, ColTotal, ColDescription, ColLength, ColWidth, ColPartNumber };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(PartsHeaderRow, i + 1).Value = headers[i];

            int row = PartsHeaderRow;
            foreach (Part part in parts)
            {
                row++;
                ws.Cell(row, 1).Value = row - PartsHeaderRow;
                ws.Cell(row, 2).Value = part.quantity;
                ws.Cell(row, 3).FormulaA1 = "=" + JobUnits;
                ws.Cell(row, 4).FormulaA1 = $"=B{row}*C{row}";
                ws.Cell(row, 5).Value = part.Description ?? "";
                if (part.length > 0)
                    ws.Cell(row, 6).Value = part.length;
                if (part.width > 0)
                    ws.Cell(row, 7).Value = part.width;
                ws.Cell(row, 8).Value = part.PartNumber ?? "";
            }

            // An Excel table needs at least one data row, so an empty template has one blank one.
            if (parts.Count == 0)
            {
                row++;
                ws.Cell(row, 3).FormulaA1 = "=" + JobUnits;
                ws.Cell(row, 4).FormulaA1 = $"=B{row}*C{row}";
            }

            IXLTable table = ws.Range(PartsHeaderRow, 1, row, headers.Length).CreateTable(PartsTable);
            table.Theme = XLTableTheme.TableStyleMedium2;

            ws.Range(PartsHeaderRow + 1, 6, row, 7).Style.NumberFormat.Format = "0.###";

            double[] widths = { 16, 10, 9, 10, 44, 18, 12, 18 };
            for (int i = 0; i < widths.Length; i++)
                ws.Column(i + 1).Width = widths[i];

            WrapHeader(ws, PartsHeaderRow, headers.Length, 20);
            ws.SheetView.FreezeRows(PartsHeaderRow);
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.FitToPages(1, 0);
            ws.PageSetup.SetRowsToRepeatAtTop(PartsHeaderRow, PartsHeaderRow);
        }

        private static void WriteFinishesSheet(XLWorkbook workbook, Func<string, string> setting)
        {
            IXLWorksheet ws = workbook.AddWorksheet(FinishesSheet);

            ws.Cell("A1").Value = "FINISHES, TREATMENTS, STENCIL AND STICKERS";
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontSize = 14;

            ws.Cell("A2").Value = "One row per treatment. Basis: Coverage = area x (1 + waste) / coverage (powder in lb, " +
                                  "paint in gal); Per area = area x (1 + waste); Each = the each-per-unit figure. " +
                                  "Coverage and waste are yours to set: Cutwright starts them from settings.json.";
            ws.Cell("A2").Style.Font.Italic = true;

            const int header = 4;
            string[] headers =
            {
                FinItem, FinSpecification, FinBasis, FinArea, FinCoverage, FinWaste, FinEach,
                FinQtyPerUnit, FinUnit, FinTotalQty, FinUnitCost, FinExtended, FinNotes,
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(header, i + 1).Value = headers[i];

            // Starter rows: no quantities, no prices. Coverage and waste come from the settings file
            // when the shop has set them, and stay blank (highlighted) when not.
            double? waste = Percent(setting(CutwrightSettings.FinishWastePercent));
            var starters = new (string Item, string Basis, string Unit, double? Coverage)[]
            {
                ("Powder coat", BasisCoverage, "lb", Number(setting(CutwrightSettings.PowderCoverageSqFtPerLb))),
                ("Stencil", BasisEach, "can", null),
                ("Stickers", BasisEach, "each", null),
            };

            int row = header;
            foreach (var starter in starters)
            {
                row++;
                ws.Cell(row, 1).Value = starter.Item;
                ws.Cell(row, 3).Value = starter.Basis;
                if (starter.Coverage is double coverage && starter.Basis == BasisCoverage)
                    ws.Cell(row, 5).Value = coverage;
                if (waste is double w && starter.Basis != BasisEach)
                    ws.Cell(row, 6).Value = w;
                ws.Cell(row, 9).Value = starter.Unit;
                WriteFinishFormulas(ws, row);
            }

            IXLTable table = ws.Range(header, 1, row, headers.Length).CreateTable(FinishTable);
            table.Theme = XLTableTheme.TableStyleMedium2;

            // Dropdowns and formats reach past the starter rows so a row added below inherits them.
            const int reach = 200;
            ws.Range(header + 1, 3, header + reach, 3).CreateDataValidation()
                .List($"\"{BasisCoverage},{BasisPerArea},{BasisEach}\"", true);
            ws.Range(header + 1, 9, header + reach, 9).CreateDataValidation()
                .List("\"lb,gal,sq ft,each,can\"", true);

            ws.Range(header + 1, 6, header + reach, 6).Style.NumberFormat.Format = "0%";
            ws.Range(header + 1, 8, header + reach, 8).Style.NumberFormat.Format = "0.00";
            ws.Range(header + 1, 10, header + reach, 10).Style.NumberFormat.Format = "0.0";
            ws.Range(header + 1, 11, header + reach, 12).Style.NumberFormat.Format = "$#,##0.00";

            // A Coverage row with no coverage figure cannot work out a quantity.
            ws.Range(header + 1, 5, header + reach, 5).AddConditionalFormat()
                .WhenIsTrue($"=AND($C{header + 1}=\"{BasisCoverage}\",$E{header + 1}=\"\")")
                .Fill.SetBackgroundColor(XLColor.Yellow);

            double[] widths = { 22, 28, 12, 12, 14, 9, 11, 11, 8, 10, 11, 13, 36 };
            for (int i = 0; i < widths.Length; i++)
                ws.Column(i + 1).Width = widths[i];

            WrapHeader(ws, header, headers.Length, 32);
            ws.SheetView.FreezeRows(header);
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.FitToPages(1, 0);
        }

        // Columns are fixed by this writer (A Item ... M Notes); readers look them up by header.
        private static void WriteFinishFormulas(IXLWorksheet ws, int row)
        {
            ws.Cell(row, 8).FormulaA1 =
                $"=IF(C{row}=\"{BasisCoverage}\",IF(E{row}=0,0,D{row}*(1+F{row})/E{row})," +
                $"IF(C{row}=\"{BasisPerArea}\",D{row}*(1+F{row}),G{row}))";
            ws.Cell(row, 10).FormulaA1 = $"=H{row}*{JobUnits}";
            ws.Cell(row, 12).FormulaA1 = $"=J{row}*K{row}";
        }

        private static double? Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;

        // "15" and "15%" both mean fifteen percent; the cell holds 0.15.
        private static double? Percent(string text)
        {
            string trimmed = text.Trim().TrimEnd('%');
            return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0
                ? v / 100.0
                : null;
        }
    }
}
