using ClosedXML.Excel;

namespace Cutwright
{
    // The End Features sheet of a bill of materials: what is known about each end of each stick
    // part - mitered, clear of features, or not yet looked at.
    //
    // A sheet of its own rather than more columns on the parts table, so the job's printed BOM is
    // untouched and there is room to add more per-end information later without reshaping the part
    // list.
    //
    // Rows are matched to the parts table by item number, and checked against the part's
    // description and length. A BOM edited since the sheet was written has rows that no longer
    // describe the same part, and applying a miter to the wrong part would quietly change a
    // quote - so a row that does not match is ignored and said so, not guessed at.
    internal static class EndFeaturesSheet
    {
        public const string SheetName = "End Features";

        private const int HeaderRow = 1;
        private const int ItemColumn = 1;
        private const int PartNumberColumn = 2;
        private const int DescriptionColumn = 3;
        private const int LengthColumn = 4;
        private const int EndAColumn = 5;
        private const int EndBColumn = 6;
        private const int NoteColumn = 8;

        // Half a thousandth of an inch: the length is a float read back from a cell, and the two
        // copies of it are not written by the same code.
        private const double LengthTolerance = 0.0005;

        // Writes the sheet from the stick parts given, replacing any sheet already there. When no
        // part has anything recorded on either end the sheet is removed instead of written full of
        // "Unreviewed" - a file nobody has reviewed is left as it was.
        //
        // Each part comes with the item number it has in the DET section, since that - not
        // Part.line - is what the sheet is matched back by.
        public static void Write(XLWorkbook workbook, IEnumerable<(int Item, Part Part)> parts)
        {
            if (workbook.TryGetWorksheet(SheetName, out IXLWorksheet? existing))
                existing.Delete();

            var sticks = parts.Where(p => p.Part.width <= 0).OrderBy(p => p.Item).ToList();

            bool anythingRecorded = sticks.Any(p => p.Part.EndA != TubeEndFeature.Unreviewed
                                                     || p.Part.EndB != TubeEndFeature.Unreviewed);
            if (!anythingRecorded)
                return;

            IXLWorksheet ws = workbook.AddWorksheet(SheetName);

            string[] headers = { "ITEM", "PART NUMBER", "DESCRIPTION", "LENGTH", "END A", "END B" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(HeaderRow, i + 1).SetValue(headers[i]);
                ws.Cell(HeaderRow, i + 1).Style.Font.Bold = true;
            }

            ws.Cell(HeaderRow, NoteColumn).SetValue(
                "Ends: " + string.Join(", ", TubeEndFeatureText.Options) +
                ". Written by Cutwright; rows are matched to the BOM by ITEM, DESCRIPTION and LENGTH.");

            int row = HeaderRow;
            foreach (var (item, part) in sticks)
            {
                row++;
                ws.Cell(row, ItemColumn).SetValue(item);
                ws.Cell(row, PartNumberColumn).SetValue(part.PartNumber ?? string.Empty);
                ws.Cell(row, DescriptionColumn).SetValue(part.Description ?? string.Empty);
                ws.Cell(row, LengthColumn).SetValue(part.length);
                ws.Cell(row, EndAColumn).SetValue(TubeEndFeatureText.Format(part.EndA));
                ws.Cell(row, EndBColumn).SetValue(TubeEndFeatureText.Format(part.EndB));
            }

            if (row > HeaderRow)
            {
                var dropdown = ws.Range(HeaderRow + 1, EndAColumn, row, EndBColumn).CreateDataValidation();
                dropdown.List("\"" + string.Join(",", TubeEndFeatureText.Options) + "\"", true);
            }

            ws.SheetView.FreezeRows(HeaderRow);
            ws.Columns(ItemColumn, EndBColumn).AdjustToContents();
        }

        // Applies the sheet's ends to the parts given, matched by item number. Notes go to
        // `warnings` for anything that could not be applied, so the caller can show them.
        // A workbook with no such sheet is not an error - nothing has been reviewed - and leaves the
        // parts as they were.
        public static void Read(XLWorkbook workbook, IEnumerable<Part> stickParts, List<string> warnings)
        {
            if (!workbook.TryGetWorksheet(SheetName, out IXLWorksheet? ws))
                return;

            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;

            var rowsByItem = new Dictionary<int, int>();
            for (int row = HeaderRow + 1; row <= lastRow; row++)
            {
                if (ws.Cell(row, ItemColumn).TryGetValue(out int item))
                    rowsByItem[item] = row;
            }

            var stale = new List<int>();
            var unrecognized = new List<string>();

            foreach (Part part in stickParts)
            {
                if (!rowsByItem.TryGetValue(part.line, out int row))
                    continue;

                string description = ws.Cell(row, DescriptionColumn).GetValue<string>();
                bool sameLength = ws.Cell(row, LengthColumn).TryGetValue(out double length)
                                  && Math.Abs(length - part.length) <= LengthTolerance;
                bool sameDescription = string.Equals(description.Trim(), (part.Description ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase);

                if (!sameLength || !sameDescription)
                {
                    stale.Add(part.line);
                    continue;
                }

                var endA = TubeEndFeatureText.Parse(ws.Cell(row, EndAColumn).GetValue<string>(), out bool aKnown);
                var endB = TubeEndFeatureText.Parse(ws.Cell(row, EndBColumn).GetValue<string>(), out bool bKnown);

                if (!aKnown)
                    unrecognized.Add($"item {part.line} end A '{ws.Cell(row, EndAColumn).GetValue<string>()}'");
                if (!bKnown)
                    unrecognized.Add($"item {part.line} end B '{ws.Cell(row, EndBColumn).GetValue<string>()}'");

                part.SetEnds(endA, endB);
            }

            if (stale.Count > 0)
                warnings.Add($"The {SheetName} sheet does not match the BOM for item(s) {string.Join(", ", stale)} " +
                             "(the description or length has changed since it was written), so those rows were " +
                             "ignored and those parts are shown as unreviewed.");

            if (unrecognized.Count > 0)
                warnings.Add($"The {SheetName} sheet has values Cutwright does not recognize " +
                             $"({string.Join("; ", unrecognized)}), which were read as Unreviewed. " +
                             $"The values are: {string.Join(", ", TubeEndFeatureText.Options)}.");
        }
    }
}
