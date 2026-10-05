namespace Cutwright
{
    // Interactive sheet nesting for other programs: give it the parts and the settings, get back
    // the layouts to draw. Stateless and fast (the engine is deterministic and sub-second on a full
    // BOM), so a caller simply calls it again whenever a setting changes. Lengths are inches.

    // One part to nest, already multiplied out to the quantity the job needs. The outline comes from
    // the DXF (units honoured); without one the part nests as its width x length rectangle.
    public sealed record SheetPartInput(
        string PartNumber, string Description, int TotalQuantity, double WidthInches, double LengthInches,
        string? DxfPath);

    public sealed class SheetNestSetup
    {
        public double SheetWidthInches { get; init; } = 48;
        public double SheetLengthInches { get; init; } = 96;
        public bool SmallestDrop { get; init; }
        public double PartSpacingInches { get; init; } = SpacingInput.Default;
        public double SheetEdgeInches { get; init; } = SpacingInput.Default;
    }

    // One placed part: outer outline and holes in sheet coordinates, x across the sheet's width and
    // y along its length.
    public sealed record PlacedOutline(
        string PartNumber,
        IReadOnlyList<(double X, double Y)> Outer,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> Holes);

    // One distinct sheet layout and how many sheets are cut that way.
    public sealed record SheetLayoutView(int Count, IReadOnlyList<PlacedOutline> Parts);

    public sealed class SheetNestPreviewResult
    {
        // The size nested on - differs from the setup's when SmallestDrop sized it.
        public double SheetWidthInches { get; init; }
        public double SheetLengthInches { get; init; }
        public int SheetCount { get; init; }
        public double UtilizationPercent { get; init; }
        public IReadOnlyList<SheetLayoutView> Layouts { get; init; } = Array.Empty<SheetLayoutView>();
        public IReadOnlyList<string> UnnestedPartNumbers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    public static class SheetNestPreview
    {
        // All parts are nested as one group on one stock size, so they should share a material and
        // thickness; the cutting rules (laser spacing, shear, rotation) come from the first part's
        // description, as when an estimator types it.
        public static SheetNestPreviewResult Nest(IReadOnlyList<SheetPartInput> parts, SheetNestSetup setup)
        {
            var group = new PNest((float)setup.SheetWidthInches, (float)setup.SheetLengthInches,
                (float)setup.SheetEdgeInches, (float)setup.PartSpacingInches)
            {
                SizeToSmallestDrop = setup.SmallestDrop,
                Policy = MaterialPolicy.FromDescription(parts.Count > 0 ? parts[0].Description : null),
            };

            int line = 0;
            foreach (SheetPartInput input in parts)
            {
                var part = new Part(++line, input.TotalQuantity, input.Description, input.PartNumber,
                    (float)input.WidthInches, (float)input.LengthInches, 0f);
                if (!string.IsNullOrEmpty(input.DxfPath))
                {
                    part.hasDXF = true;
                    part.DXFPath = input.DxfPath;
                }
                group.Parts.Add(part);
            }

            group.Nest();

            double.TryParse(group.Efficiency, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double utilization);

            var layouts = new List<SheetLayoutView>();
            foreach (SheetLayout layout in SheetLayouts.Group(group.Sheets))
            {
                var placed = new List<PlacedOutline>();
                foreach (PlacedPart p in layout.Representative.NestedParts)
                {
                    var (outer, holes) = p.ToWorld();
                    placed.Add(new PlacedOutline(p.SourcePart.PartNumber ?? "", outer,
                        holes.Select(h => (IReadOnlyList<(double X, double Y)>)h).ToList()));
                }
                layouts.Add(new SheetLayoutView(layout.Count, placed));
            }

            return new SheetNestPreviewResult
            {
                SheetWidthInches = group.SheetWidth,
                SheetLengthInches = group.SheetLength,
                SheetCount = group.SheetCount,
                UtilizationPercent = utilization,
                Layouts = layouts,
                UnnestedPartNumbers = group.UnnestedList.Select(p => p.PartNumber ?? "").Distinct().ToList(),
                Warnings = group.Warnings,
            };
        }
    }
}
