namespace Cutwright
{
    // Interactive stick (linear stock) nesting for other programs, the counterpart of SheetNestPreview:
    // the pieces and the settings in, the distinct stick layouts to draw out. Stateless, so a caller
    // calls it again whenever a setting changes. Lengths are inches, angles degrees from square.

    // One piece to cut, already multiplied out to the quantity the job needs. A non-square end is
    // nested as a 45-degree miter (the only angled cut the engine knows): it needs support at the
    // cut, stays out of the laser's clamp zone, and can share a diagonal cut with a facing miter.
    public sealed record StickPartInput(
        string PartNumber, string Description, int TotalQuantity, double LengthInches,
        bool MiterStart = false, bool MiterEnd = false);

    public sealed class StickNestSetup
    {
        public double StickLengthInches { get; init; } = 240;
        public double KerfInches { get; init; } = TNest.DefaultKerf;
        public double MinClampInches { get; init; } = TNest.DefaultMinClampLength;

        // The saw has no clamp zone; the tube laser needs MinClampInches kept clear of features.
        public bool CutOnSaw { get; init; } = true;

        // Size the stick to the shortest single one that holds every piece.
        public bool SmallestDrop { get; init; }
    }

    // One cut piece on a stick.
    public sealed record PlacedCut(string PartNumber, double LengthInches, bool MiterStart, bool MiterEnd);

    // One distinct stick layout and how many sticks are cut that way.
    public sealed record StickLayoutView(int Count, IReadOnlyList<PlacedCut> Cuts, double RemainingInches);

    public sealed class StickNestPreviewResult
    {
        // The length nested on - differs from the setup's when SmallestDrop sized it.
        public double StickLengthInches { get; init; }
        public int StickCount { get; init; }
        public double UtilizationPercent { get; init; }
        public IReadOnlyList<StickLayoutView> Layouts { get; init; } = Array.Empty<StickLayoutView>();
        public int UnnestedPieceCount { get; init; }
        public IReadOnlyList<string> UnnestedPartNumbers { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    public static class StickNestPreview
    {
        // All pieces are nested as one group on one stock length, so they should be the same
        // profile and material; the group's description (from the first piece) is also how the
        // engine reads the tube's width for sharing miter cuts.
        public static StickNestPreviewResult Nest(IReadOnlyList<StickPartInput> parts, StickNestSetup setup)
        {
            var group = new TNest
            {
                Description = parts.Count > 0 ? parts[0].Description : "",
                StickLength = (float)setup.StickLengthInches,
                Kerf = (float)setup.KerfInches,
                MinClampLength = (float)setup.MinClampInches,
                CutOnSaw = setup.CutOnSaw,
                SizeToSmallestDrop = setup.SmallestDrop,
            };

            int line = 0;
            foreach (StickPartInput input in parts)
            {
                var part = new Part(++line, input.TotalQuantity, input.Description, input.PartNumber, 0f,
                    (float)input.LengthInches, 0f);
                part.SetEnds(input.MiterStart ? TubeEndFeature.Miter : TubeEndFeature.Unreviewed,
                    input.MiterEnd ? TubeEndFeature.Miter : TubeEndFeature.Unreviewed);
                group.Parts.Add(part);
            }

            group.Nest();

            var warnings = new List<string>();
            foreach (string number in group.UnnestedList.Select(p => p.PartNumber ?? "").Distinct())
            {
                Part piece = group.UnnestedList.First(p => (p.PartNumber ?? "") == number);
                warnings.Add($"Part {number} ({piece.length:0.###} in) is longer than a {group.StickLength:0.###} in stick can give.");
            }

            // Sticks cut the same way are one layout: same pieces in the same order.
            var layouts = new List<StickLayoutView>();
            var counts = new Dictionary<string, int>();
            foreach (Stick stick in group.Sticks)
            {
                string key = string.Join("|", stick.NestedParts.Select(p => $"{p.PartNumber}:{p.length:0.####}"));
                if (counts.TryGetValue(key, out int index))
                {
                    layouts[index] = layouts[index] with { Count = layouts[index].Count + 1 };
                    continue;
                }

                counts[key] = layouts.Count;
                layouts.Add(new StickLayoutView(1,
                    stick.NestedParts.Select(p => new PlacedCut(p.PartNumber ?? "", p.length,
                        TubeEndFeatureText.IsMiter(p.EndA), TubeEndFeatureText.IsMiter(p.EndB))).ToList(),
                    stick.RemainingLength));
            }

            return new StickNestPreviewResult
            {
                StickLengthInches = group.StickLength,
                StickCount = group.StickCount,
                UtilizationPercent = group.Efficiency,
                Layouts = layouts,
                UnnestedPieceCount = group.UnnestedList.Count,
                UnnestedPartNumbers = group.UnnestedList.Select(p => p.PartNumber ?? "").Distinct().ToList(),
                Warnings = warnings,
            };
        }
    }
}
