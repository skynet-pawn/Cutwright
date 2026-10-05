using ClosedXML.Excel;

namespace Cutwright
{
    // The public face of the engine, for programs that feed Cutwright a job instead of a person
    // typing one in. Everything else in the library is internal; this is deliberately
    // small and made of plain data, so callers never touch Part, PNest or the workbook layout.
    //
    // The flow is the same one the app runs, file to file: write a Cutwright bill of materials
    // (an estimator can edit it by hand), read it back, nest it, write the buy sheet and the nested
    // sheet DXFs. Lengths are inches, as everywhere in Cutwright.

    // One line of a bill of materials. Width > 0 makes it a sheet part (width x length, nested on a
    // sheet); width 0 is a stick cut to length. The description decides the material and thickness
    // ("Sheet 14GA HR"), exactly as when an estimator types it.
    // A stick with a mitered end says so (it nests as a 45-degree cut and is recorded on the End
    // Features sheet).
    public sealed record BomLineInput(
        string PartNumber, string Description, int QuantityPerUnit, double LengthInches, double WidthInches,
        bool MiterStart = false, bool MiterEnd = false);

    public sealed class BomJobInfo
    {
        public string Customer { get; init; } = "";
        public string Description { get; init; } = "";
        public string DrawingNumber { get; init; } = "";
        public string Revision { get; init; } = "";
        public string CheckedBy { get; init; } = "";
        public string JobEstimate { get; init; } = "";
        public string Date { get; init; } = "";
        public string PreparedBy { get; init; } = "";
        public int Units { get; init; } = 1;
    }

    public sealed class NestJobOptions
    {
        public double SheetWidthInches { get; init; } = 48;
        public double SheetLengthInches { get; init; } = 96;

        // Size each sheet group to the smallest single sheet that holds it (as the app's Smallest
        // Drop does) instead of the fixed size above.
        public bool SmallestDrop { get; init; }

        public double PartSpacingInches { get; init; } = SpacingInput.Default;
        public double SheetEdgeInches { get; init; } = SpacingInput.Default;

        // Settings of one group where it differs from the above, keyed by the group's description
        // (what the BOM line says, e.g. "Sheet 14GA HR" or "SQ Tube 2 x 2 x 1/4").
        public IReadOnlyDictionary<string, SheetNestSetup> SheetGroups { get; init; } = new Dictionary<string, SheetNestSetup>();
        public IReadOnlyDictionary<string, StickNestSetup> StickGroups { get; init; } = new Dictionary<string, StickNestSetup>();

        // What every stick group without an entry above is nested with.
        public StickNestSetup Sticks { get; init; } = new StickNestSetup();
    }

    public sealed record StickGroupResult(
        string Description, double StickLengthInches, int StickCount, double UtilizationPercent,
        int PartCount, int UnnestedPartCount);

    public sealed record SheetGroupResult(
        string Description, double SheetWidthInches, double SheetLengthInches, int SheetCount,
        double UtilizationPercent, int PartCount, int UnnestedPartCount);

    // One line of the Purchase sheet: what to buy.
    public sealed record PurchaseLineResult(string Line, double Quantity, string Unit, string Description);

    public sealed class NestJobResult
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<SheetGroupResult> SheetGroups { get; } = new();
        public List<StickGroupResult> StickGroups { get; } = new();
        public int StickGroupCount { get; set; }
        public List<PurchaseLineResult> Purchase { get; } = new();

        // Files written: the buy-sheet workbook and the nested sheet DXFs.
        public List<string> Files { get; } = new();

        public bool Succeeded => Errors.Count == 0;
    }

    public static class NestJob
    {
        // Writes a Cutwright bill of materials workbook. Returns why it failed, or nothing.
        public static IReadOnlyList<string> WriteBom(string path, BomJobInfo info, IEnumerable<BomLineInput> lines)
        {
            var bom = new BillOfMaterials
            {
                CustomerName = info.Customer,
                Description = info.Description,
                DrawingNo = info.DrawingNumber,
                RevisionNo = info.Revision,
                CheckedBy = info.CheckedBy,
                JobEst = info.JobEstimate,
                Date = info.Date,
                EngName = info.PreparedBy,
                Units = info.Units,
            };

            int line = 0;
            foreach (BomLineInput input in lines)
            {
                var part = new Part(++line, input.QuantityPerUnit, input.Description, input.PartNumber,
                    (float)input.WidthInches, (float)input.LengthInches, 0f);
                if (input.MiterStart || input.MiterEnd)
                {
                    part.SetEnds(input.MiterStart ? TubeEndFeature.Miter : TubeEndFeature.Unreviewed,
                        input.MiterEnd ? TubeEndFeature.Miter : TubeEndFeature.Unreviewed);
                }
                bom.parts.Add(part);
            }

            bom.WriteToFile(path);
            return bom.Errors;
        }

        // Reads a Cutwright bill of materials (DXF outlines are matched by part number in the same
        // folder or a DXF folder beneath it), nests it, and writes
        //   <buySheetPath>            the bill of materials with the Purchase sheet added
        //   <dxfFolder>\<name>_*.dxf  one program per distinct sheet layout
        // The bill of materials itself is left untouched.
        public static NestJobResult Run(string bomPath, string buySheetPath, string dxfFolder, NestJobOptions options)
        {
            var result = new NestJobResult();

            var parser = new FileParser();
            parser.Parse(bomPath);
            result.Errors.AddRange(parser.Errors);
            result.Warnings.AddRange(parser.Warnings);
            if (result.Errors.Count > 0)
                return result;

            foreach (PNest group in parser.PNestList)
            {
                var setup = options.SheetGroups.TryGetValue(group.Description ?? "", out SheetNestSetup? own)
                    ? own
                    : new SheetNestSetup
                    {
                        SheetWidthInches = options.SheetWidthInches,
                        SheetLengthInches = options.SheetLengthInches,
                        SmallestDrop = options.SmallestDrop,
                        PartSpacingInches = options.PartSpacingInches,
                        SheetEdgeInches = options.SheetEdgeInches,
                    };
                group.SheetWidth = (float)setup.SheetWidthInches;
                group.SheetLength = (float)setup.SheetLengthInches;
                group.SizeToSmallestDrop = setup.SmallestDrop;
                group.PartSpacing = (float)setup.PartSpacingInches;
                group.SheetSpacing = (float)setup.SheetEdgeInches;
            }

            foreach (TNest group in parser.TNestList)
            {
                var setup = options.StickGroups.TryGetValue(group.Description ?? "", out StickNestSetup? own)
                    ? own
                    : options.Sticks;
                group.StickLength = (float)setup.StickLengthInches;
                group.Kerf = (float)setup.KerfInches;
                group.MinClampLength = (float)setup.MinClampInches;
                group.CutOnSaw = setup.CutOnSaw;
                group.SizeToSmallestDrop = setup.SmallestDrop;
            }

            try
            {
                foreach (TNest group in parser.TNestList)
                    group.Nest();
                foreach (PNest group in parser.PNestList)
                    group.Nest();
            }
            catch (Exception e)
            {
                result.Errors.Add($"Nesting failed: {e.Message}");
                return result;
            }

            result.StickGroupCount = parser.TNestList.Count;
            foreach (TNest group in parser.TNestList)
            {
                result.StickGroups.Add(new StickGroupResult(group.Description ?? "", group.StickLength, group.StickCount,
                    group.Efficiency, group.Parts.Sum(p => p.quantity), group.UnnestedList.Count));
            }
            foreach (PNest group in parser.PNestList)
            {
                double.TryParse(group.Efficiency, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double utilization);
                result.SheetGroups.Add(new SheetGroupResult(group.Description ?? "", group.SheetWidth,
                    group.SheetLength, group.SheetCount, utilization, group.Parts.Sum(p => p.quantity),
                    group.UnnestedPartCount));
                result.Warnings.AddRange(group.Warnings);
            }

            var writer = new FileWriter();
            writer.SendToFile(buySheetPath, parser.PNestList, parser.TNestList, parser.SourcePath);
            result.Errors.AddRange(writer.Errors);
            if (result.Errors.Count > 0)
                return result;
            result.Files.Add(buySheetPath);

            ReadPurchase(buySheetPath, result);

            try
            {
                Directory.CreateDirectory(dxfFolder);
                string baseName = Path.GetFileNameWithoutExtension(bomPath);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var dxf = new DXFWriter();

                foreach (PNest group in parser.PNestList)
                {
                    string safe = NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart(group.Description), used);
                    List<SheetLayout> layouts = SheetLayouts.Group(group.Sheets);
                    for (int i = 0; i < layouts.Count; i++)
                    {
                        string file = Path.Combine(dxfFolder, $"{baseName}_{safe}_{i + 1}_x{layouts[i].Count}.dxf");
                        dxf.WriteDXF(layouts[i].Representative, file);
                        result.Files.Add(file);
                    }
                }
            }
            catch (Exception e)
            {
                result.Errors.Add($"Could not write the nested sheet DXFs: {e.Message}");
            }

            return result;
        }

        private static void ReadPurchase(string path, NestJobResult result)
        {
            try
            {
                using var wb = new XLWorkbook(path);
                if (!BomWorkbook.TryGetTable(wb, BomWorkbook.PurchaseTable, out IXLTable table) || table.DataRange is null)
                    return;

                var columns = BomWorkbook.Columns(table);
                if (!columns.TryGetValue(BomWorkbook.PurDescription, out int description) ||
                    !columns.TryGetValue(BomWorkbook.PurQty, out int quantity))
                    return;

                columns.TryGetValue(BomWorkbook.PurLine, out int line);
                columns.TryGetValue(BomWorkbook.PurUnit, out int unit);

                foreach (IXLRangeRow row in table.DataRange.Rows())
                {
                    int r = row.RowNumber();
                    string text = BomWorkbook.Text(table.Worksheet.Cell(r, description));
                    if (text.Length == 0)
                        continue;

                    BomWorkbook.TryNumber(table.Worksheet.Cell(r, quantity), out double qty);
                    result.Purchase.Add(new PurchaseLineResult(
                        line > 0 ? BomWorkbook.Text(table.Worksheet.Cell(r, line)) : "",
                        qty,
                        unit > 0 ? BomWorkbook.Text(table.Worksheet.Cell(r, unit)) : "",
                        text));
                }
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Could not read the Purchase sheet back: {e.Message}");
            }
        }
    }
}

namespace Cutwright
{
    // How Cutwright reads a stock description ("SQ Tube 2 x 2 x 1/4"): for callers that build
    // descriptions and want to check they are recognised as the stock they mean.
    public static class StockCallouts
    {
        // The stock form Cutwright reads from a description ("SquareTube", "Angle", ...), or "Unknown".
        public static string FormOf(string description) =>
            CalloutTranslator.Read(description ?? string.Empty, null)?.Form.ToString() ?? "Unknown";

        // Whether the description reads as stock that is cut by length (tube, angle, bar, pipe) rather than
        // from a sheet or plate. False for stock it doesn't recognise.
        public static bool IsStick(string description) =>
            CalloutTranslator.Read(description ?? string.Empty, null) is { } spec && StockForms.IsStick(spec.Form);

        // The cross-section numbers it reads (inches): the sides and wall of a tube, for instance.
        public static IReadOnlyList<double> SectionOf(string description) =>
            CalloutTranslator.Read(description ?? string.Empty, null)?.Section.Select(v => (double)v).ToList()
            ?? (IReadOnlyList<double>)Array.Empty<double>();
    }
}
