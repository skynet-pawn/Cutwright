namespace Cutwright
{
    // Where each part of a stick sits in the 1D drawing, and the outline it is drawn with.
    //
    // Kept out of the window so the claim that matters can be tested: the parts are drawn over
    // exactly the length the nest charged for them, shared miter cuts included.
    internal static class StickLayout
    {
        // The miter width drawn when the stock's description gives no size at all.
        public const float FallbackMiterWidth = 1f;

        // Where each part's long point starts, measured from the start of the stick. A part whose
        // leading miter shares the cut before it starts the miter credit sooner, so its diagonal
        // runs alongside its neighbour's - the same length Stick.AddPart charged for it.
        public static List<float> PartOffsets(Stick stick)
        {
            var offsets = new List<float>(stick.NestedParts.Count);
            float x = 0f;

            for (int i = 0; i < stick.NestedParts.Count; i++)
            {
                if (stick.PlacementAt(i).SharedCut)
                    x -= stick.MiterCreditWidth;

                offsets.Add(x);
                x += stick.NestedParts[i].length + stick.Kerf;
            }

            return offsets;
        }

        // The part's outline, clockwise from the top left: x along the stick from the part's start,
        // y down from its top edge. Each mitered end pulls one edge in by the miter width - clamped
        // so a part shorter than its miters still draws as a shape instead of crossing itself.
        public static (double X, double Y)[] Outline(StickPlacement placement, double length, double height,
            double miterWidth)
        {
            int slants = (placement.Leading != MiterSlant.None ? 1 : 0) +
                         (placement.Trailing != MiterSlant.None ? 1 : 0);
            double w = slants == 0 ? 0.0 : Math.Min(miterWidth, length / slants);

            double topLeft = placement.Leading == MiterSlant.LongBottom ? w : 0.0;
            double bottomLeft = placement.Leading == MiterSlant.LongTop ? w : 0.0;
            double topRight = length - (placement.Trailing == MiterSlant.LongBottom ? w : 0.0);
            double bottomRight = length - (placement.Trailing == MiterSlant.LongTop ? w : 0.0);

            return new[] { (topLeft, 0.0), (topRight, 0.0), (bottomRight, height), (bottomLeft, height) };
        }
    }
}
