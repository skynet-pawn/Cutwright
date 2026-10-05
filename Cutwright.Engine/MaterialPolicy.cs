namespace Cutwright
{
    // How a material's sheets get cut, which drives the nesting rules for it.
    internal enum CutMethod
    {
        // Laser/plasma/router: parts can sit anywhere on the sheet, but every cut consumes
        // kerf, so parts need spacing between them.
        FreePlacement = 0,

        // Sheared: every cut must run the full width or height of the piece being cut
        // (guillotine), but the blade consumes no material so parts can butt together.
        Shear = 1
    }

    // Per-material nesting rules. Sheet metal, expanded metal, wire mesh, and foam all nest
    // differently, and getting these wrong produces layouts that can't actually be cut (or,
    // worse, that look fine and quietly mis-estimate material).
    internal class MaterialPolicy
    {
        // Expanded metal's diamond pattern is directional: on the BOM the long-way-of-diamond
        // dimension is ALWAYS the Length column, even when it's the smaller number. So Width and
        // Length encode orientation, not magnitude - the part cannot be rotated, and its Length
        // axis must stay aligned with the sheet's Length axis.
        public bool AllowRotation { get; init; } = true;

        public CutMethod CutMethod { get; init; } = CutMethod.FreePlacement;

        // Minimum gap between parts. For free placement this is kerf/heat-affected-zone (or, on
        // the router, the cutter diameter). Shearing consumes no material, so it's zero.
        public double MinimumPartSpacing { get; init; }

        // Material trimmed off the stock sheet before parts can be laid out. Sheared material
        // gets squared up first, which costs one long edge and one short edge.
        public double EdgeAllowanceWidth { get; init; }
        public double EdgeAllowanceLength { get; init; }

        // Whether concave parts may be interleaved into each other's pockets rather than each
        // taking its own bounding box. Guillotine materials rule it out by construction - an
        // interleaved cluster cannot be separated by cuts that span the full piece - but this is
        // a flag rather than a test on CutMethod so a free-placement material can still be
        // excluded on its own merits.
        public bool AllowInterleaving { get; init; }

        public string Name { get; init; } = "Sheet";

        public static readonly MaterialPolicy SheetMetal = new()
        {
            Name = "Sheet / Plate",
            AllowRotation = true,
            CutMethod = CutMethod.FreePlacement,
            MinimumPartSpacing = 0.0,
            AllowInterleaving = true
        };

        public static readonly MaterialPolicy ExpandedMetal = new()
        {
            Name = "Expanded Metal",
            AllowRotation = false,
            CutMethod = CutMethod.Shear,
            MinimumPartSpacing = 0.0,
            EdgeAllowanceWidth = 0.5,
            EdgeAllowanceLength = 0.5
        };

        public static readonly MaterialPolicy WireMesh = new()
        {
            Name = "Wire Mesh",
            AllowRotation = true,
            CutMethod = CutMethod.Shear,
            MinimumPartSpacing = 0.0,
            EdgeAllowanceWidth = 0.5,
            EdgeAllowanceLength = 0.5
        };

        public static readonly MaterialPolicy Foam = new()
        {
            Name = "Foam",
            AllowRotation = true,
            CutMethod = CutMethod.FreePlacement,
            // Foam is routed with the outline cutter, so parts can never be closer than the
            // bit diameter or the cut would run into the neighbouring part.
            MinimumPartSpacing = 0.375,
            AllowInterleaving = true
        };

        public static readonly MaterialPolicy[] All = { SheetMetal, ExpandedMetal, WireMesh, Foam };

        // Guesses the material from the BOM description so the common case needs no input. Only
        // ever a guess: descriptions are free text, and a keyword match cannot know how a material
        // is really cut - "HDPE 2\" Sheet Black" reads as plain sheet here but is routed, so it
        // falls through to SheetMetal and gets laser spacing.
        //
        // A wrong guess changes the rules a nest is built under, and so the sheet count, so the
        // choice is offered per group in the Nested Parts List for the estimator to correct.
        // Nothing here is trusted outright.
        public static MaterialPolicy FromDescription(string? description)
        {
            string text = (description ?? string.Empty).ToLowerInvariant();

            if (text.Contains("exp. metal") || text.Contains("exp metal") || text.Contains("expanded"))
                return ExpandedMetal;

            if (text.Contains("wire mesh") || text.Contains("mesh"))
                return WireMesh;

            if (text.Contains("foam"))
                return Foam;

            return SheetMetal;
        }
    }
}
