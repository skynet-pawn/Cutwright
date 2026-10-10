namespace Cutwright
{
    // Folds sticks that were cut the same way into one row apiece for the 1D drawing.
    //
    // Separate from the drawing code because the multipliers it produces have to sum to the stick
    // count the estimate is built on - that is a claim worth testing on its own, without a window
    // in the way.
    internal static class StickPatterns
    {
    //Collapses sticks cut the same way down to one row apiece, with how many were cut that way.
    //A twenty-stick group is usually two or three distinct patterns, and twenty near-identical
    //bars tell the estimator far less than three bars and their counts.
    //
    //Patterns are compared on the sequence of cut lengths and miters, which is exactly what the row draws.
    //Ordering is not a separate concern in practice: the nester places pieces longest first, so
    //a stick's contents always come out in descending length.
    public static List<(Stick Representative, int Count)> GroupIdenticalSticks(List<Stick> sticks)
    {
        var patterns = new List<(Stick Representative, int Count)>();

        foreach (var stick in sticks)
        {
            int found = -1;

            for (int i = 0; i < patterns.Count; i++)
            {
                if (SameArrangement(patterns[i].Representative, stick))
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
                patterns[found] = (patterns[found].Representative, patterns[found].Count + 1);
            else
                patterns.Add((stick, 1));
        }

        return patterns;
    }

    public static bool SameArrangement(Stick a, Stick b)
    {
        if (a.NestedParts.Count != b.NestedParts.Count || a.StickLength != b.StickLength)
            return false;

        for (int i = 0; i < a.NestedParts.Count; i++)
        {
            //Exact comparison is right here: these lengths are copies of the same BOM values,
            //not the result of arithmetic, so two equal cuts really are bit-identical.
            if (a.NestedParts[i].length != b.NestedParts[i].length)
                return false;

            //The row draws the miters too, so the same lengths cut with different miters (or a
            //shared cut on one stick and not the other) are different rows.
            if (a.PlacementAt(i) != b.PlacementAt(i))
                return false;
        }

        return true;
    }
    }
}
