using System.Globalization;

namespace Cutwright
{
    // Builds a bill of materials from a CSV whose columns the estimator has described.
    //
    // This is what the "Import BOM from CSV" command does, and the whole of it. It was called
    // PDFTableParser, which was misleading twice over: it never touched a PDF - the drawing is put
    // through Tabula first, and this reads Tabula's CSV - and it did not only parse, it wrote the
    // workbook out as a side effect of building the list.
    //
    // Building and writing are separate now. This returns a BillOfMaterials and nothing else; the
    // caller decides whether to write it.
    internal sealed class BomFromCsv
    {
        // Anything the estimator should know before quoting from the result.
        public List<string> Warnings { get; } = new();

        public BillOfMaterials Build(CsvTable table, IReadOnlyList<CsvColumnRole> roles)
        {
            var bom = new BillOfMaterials();

            // BillOfMaterials.WriteToFile's parts table takes the imported quantity as Per unit -
            // how many of this part go into ONE unit - and multiplies it by Units for the row's
            // Total. A CSV's own QTY column has no way to tell this reader whether it is already
            // per-unit or a total for the whole job, so the only safe default is 1: an untouched
            // Units box then reproduces exactly what was imported, with nothing multiplied on top
            // of it. Left at the BillOfMaterials() default of 0, BomDetailsWindow's Units box
            // starts blank and forces the estimator to type something before they have any reason
            // to know it is a multiplier - and typing the job's real unit count over quantities
            // that were already job totals silently inflates every Total by that count again.
            bom.Units = 1;

            // Which columns feed which field. A role may be given to more than one column - two
            // halves of a split description is the common case - and they are joined in column
            // order.
            var description = ColumnsFor(roles, CsvColumnRole.Description, CsvColumnRole.Profile);
            var materialColumns = ColumnsFor(roles, CsvColumnRole.Material);
            var detail = ColumnsFor(roles, CsvColumnRole.Detail);
            var quantity = ColumnsFor(roles, CsvColumnRole.Quantity);
            var length = ColumnsFor(roles, CsvColumnRole.Length);
            var width = ColumnsFor(roles, CsvColumnRole.Width);
            var thickness = ColumnsFor(roles, CsvColumnRole.Thickness);
            var height = ColumnsFor(roles, CsvColumnRole.Height);
            var partNumber = ColumnsFor(roles, CsvColumnRole.PartNumber);

            if (description.Count == 0)
            {
                Warnings.Add("No column was marked Description, so every part will be nested with " +
                             "no material callout. Nothing can be grouped by material without one.");
            }

            if (quantity.Count == 0)
                Warnings.Add("No column was marked QTY, so every part will import with a quantity of zero and be dropped.");

            // A number cannot be assembled out of two cells, so more than one column on a numeric
            // role produces nothing rather than a wrong figure. Said out loud, because the old code
            // joined them and let the parse fail silently.
            WarnIfSplit(quantity, "QTY");
            WarnIfSplit(length, "Length");
            WarnIfSplit(width, "Width");
            WarnIfSplit(thickness, "Thickness");
            WarnIfSplit(height, "Height");

            bool hasDimensionColumns = thickness.Count > 0 || height.Count > 0;

            int unreadableQuantity = 0;
            int missingDimensions = 0;
            int readFromCallout = 0;
            int translated = 0;
            var unreadable = new List<string>();
            var translations = new List<string>();

            for (int row = 0; row < table.Rows.Count; row++)
            {
                // The customer's own words, kept for the rest of this loop: the size of a part
                // is read out of them, and an stock callout names stock rather than a part, so
                // "Plate 1/4 HR" has no size in it to find.
                string customerText = Join(table, row, description);
                string materialText = Join(table, row, materialColumns);
                string part = Join(table, row, partNumber);

                int.TryParse(Join(table, row, detail), NumberStyles.Integer, CultureInfo.CurrentCulture, out int det);

                string quantityText = Join(table, row, quantity);
                if (!int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int qty)
                    && quantityText.Length > 0)
                {
                    unreadableQuantity++;
                }

                float len = ParseDimension(Join(table, row, length));
                float wid = ParseDimension(Join(table, row, width));
                float thk = ParseDimension(Join(table, row, thickness));
                float hgt = ParseDimension(Join(table, row, height));

                // Only when the columns gave nothing. A customer whose drawing carries real
                // dimension columns is left exactly as it was - the callout is a fallback for the
                // drawings that have no such columns, not a second opinion on the ones that do.
                if (len <= 0 && wid <= 0)
                {
                    var reading = CalloutDimensions.Read(customerText);

                    if (reading.Found)
                    {
                        wid = reading.Width;
                        len = reading.Length;
                        readFromCallout++;
                    }
                    else if (qty > 0 && unreadable.Count < UnreadableExamples)
                    {
                        unreadable.Add($"{Shorten(customerText)} - {reading.Reason}");
                    }
                }

                if (qty > 0 && len <= 0 && wid <= 0)
                    missingDimensions++;

                // Now the description can become a callout. On success it replaces the
                // customer's words outright, since it already carries the grade; on failure the
                // material is joined back on and the estimator corrects it on the sheet.
                string partDescription;

                // A drawing whose own table carries dimensions in columns of their own (rather
                // than embedded in the description text) is tried first - the text-only path
                // below has nothing to find on a description like "SHEET STEEL (LASER)" or
                // "RECT BAR" and would otherwise fail every row of a table shaped that way.
                string? fromColumns = hasDimensionColumns
                    ? CalloutTranslator.TranslateFromColumns(customerText, materialText,
                        new CalloutTranslator.ColumnDimensions(
                            thk > 0 ? thk : null, wid > 0 ? wid : null, hgt > 0 ? hgt : null))
                    : null;

                if (fromColumns is not null)
                {
                    partDescription = fromColumns;
                    translated++;

                    if (translations.Count < TranslationExamples)
                        translations.Add($"{Shorten(customerText)}  ->  {partDescription}");
                }
                else if (CalloutTranslator.CanTranslate(customerText, materialText))
                {
                    partDescription = CalloutTranslator.Translate(customerText, materialText);
                    translated++;

                    if (translations.Count < TranslationExamples)
                        translations.Add($"{Shorten(customerText)}  ->  {partDescription}");
                }
                else
                {
                    partDescription = string.Join(" ",
                        new[] { customerText, materialText }.Where(t => t.Length > 0));
                }

                // A stick form (tube, pipe, angle, bar, rod) nests 1D by length only - its own
                // cross-section dimensions belong in partDescription's callout, never in
                // Part.width, which is FileParser's sole signal for routing a part into 2D sheet
                // nesting instead. This applies regardless of whether a Width-role column was
                // ever meant as a footprint on this particular drawing, since nothing else catches
                // a Width column that turned out to hold a tube's cross-section leg.
                StockForm? form = CalloutTranslator.NamedForm(customerText);
                float partWidth = form is { } f && StockForms.IsStick(f) ? 0f : wid;

                bom.parts.Add(new Part(det, qty, partDescription, part, partWidth, len, 0.0f));
            }

            if (unreadableQuantity > 0)
            {
                // Almost always the header row of a file that has one, which is why this counts
                // rather than complains: one is expected, a dozen means the wrong column.
                Warnings.Add($"{unreadableQuantity} row(s) had a quantity that is not a number and " +
                             "will be dropped. A file with a header row accounts for one of these.");
            }

            if (translated > 0)
            {
                // Named rather than counted, because a wrong callout is a wrong material and the
                // cheapest place to catch it is here. The rest keep the customer's wording, which
                // is visibly not one of ours.
                Warnings.Add($"{translated} of {table.Rows.Count} description(s) were rewritten as " +
                             "stock callouts. The rest kept the customer's wording for you to " +
                             "correct on the sheet." + Environment.NewLine +
                             string.Join(Environment.NewLine, translations.Select(t => "    " + t)));
            }

            if (readFromCallout > 0)
            {
                // Reported rather than done quietly. Which of three dimensions is the thickness is
                // read as the smallest, which is right for plate and sheet and can be wrong for
                // thick stock, so the estimator gets told it happened.
                Warnings.Add($"{readFromCallout} part(s) had no dimension columns, so their size was " +
                             "read out of the description. Check these against the drawing.");
            }

            if (missingDimensions > 0)
            {
                string examples = unreadable.Count == 0
                    ? string.Empty
                    : Environment.NewLine + string.Join(Environment.NewLine,
                        unreadable.Select(u => "    " + u));

                Warnings.Add($"{missingDimensions} part(s) have no length or width and cannot be " +
                             "nested. Purchased hardware is expected here; a cut part is not." +
                             examples);
            }

            return bom;
        }

        // How many unreadable descriptions to name. Enough to see the pattern, not enough to
        // become a wall of text nobody reads.
        private const int UnreadableExamples = 8;

        // How many translations to show. Enough to tell at a glance whether the rules understood
        // this customer's drawing.
        private const int TranslationExamples = 12;

        // A plain float.TryParse cannot read a real drawing's own dimension cells - Tabula hands
        // them back exactly as printed, inch mark and mixed fraction included ("2 15/16\"",
        // "1/4\""), not as a clean decimal. CalloutTranslator.Value already parses exactly that
        // shape (it reads the same fractions out of description text); this only has to strip the
        // inch mark first, since Value's mixed-number regex is anchored end to end.
        private static float ParseDimension(string text)
        {
            string cleaned = text.Replace("\"", string.Empty).Trim();
            return cleaned.Length == 0 ? 0f : CalloutTranslator.Value(cleaned);
        }

        private static string Shorten(string text) =>
            text.Length <= 60 ? text : text[..60] + "...";

        private static List<int> ColumnsFor(IReadOnlyList<CsvColumnRole> roles, params CsvColumnRole[] wanted)
        {
            var columns = new List<int>();

            for (int i = 0; i < roles.Count; i++)
            {
                if (Array.IndexOf(wanted, roles[i]) >= 0)
                    columns.Add(i);
            }

            return columns;
        }

        private void WarnIfSplit(List<int> columns, string role)
        {
            if (columns.Count > 1)
            {
                Warnings.Add($"{columns.Count} columns were marked {role}. A number cannot be read " +
                             $"from more than one cell, so {role} will be empty - mark only one.");
            }
        }

        // Cells joined with a space, in column order, trimmed. Trimmed because a value read for a
        // number has to parse, and because a description should not arrive with a trailing space.
        private static string Join(CsvTable table, int row, List<int> columns)
        {
            if (columns.Count == 0)
                return string.Empty;

            if (columns.Count == 1)
                return table.Cell(row, columns[0]).Trim();

            var parts = new List<string>(columns.Count);

            foreach (int column in columns)
            {
                string cell = table.Cell(row, column).Trim();
                if (cell.Length > 0)
                    parts.Add(cell);
            }

            return string.Join(" ", parts);
        }
    }
}
