namespace Cutwright
{
    // Tube parts only. What's recorded in the BOM's Column M cell for a tube/angle/bar part: does
    // either end need a 45-degree miter, and has the remaining square end been reviewed and
    // confirmed clear of holes or other features near it.
    //
    // A miter cut needs real support right at the cut, unlike a plain square parting cut, so a
    // miter end can never sit inside the tube laser's clamp zone (see TNest.MinClampLength) no
    // matter what the rest of the state says - only Clean and SingleMiterClean, whose square end
    // is the one that would land there, ever allow it.
    internal enum TubeEndState
    {
        // Blank. Square both ends, nobody has reviewed it - the safe default for anything nobody
        // has looked at.
        Unreviewed = 0,

        // "TRUE". Square both ends, reviewed - no features near either end.
        Clean = 1,

        // "45". One end mitered 45 degrees, square end unreviewed.
        SingleMiter = 2,

        // "45T". One end mitered 45 degrees, square end reviewed clean.
        SingleMiterClean = 3,

        // "45/45". Both ends mitered 45 degrees, facing - the piece tapers to a point.
        DoubleMiterFacing = 4,

        // "45x45". Both ends mitered 45 degrees, on opposite sides - an offset/parallelogram cut,
        // not a taper. Rare compared to DoubleMiterFacing.
        DoubleMiterOpposite = 5,
    }

    internal static class TubeEndStateText
    {
        // The exact values the column accepts, in the order the dropdown offers them. "1" is also
        // accepted for Clean on read, for the same reason the old NoFeatures column took it.
        public static readonly string[] DropdownValues = { "TRUE", "45", "45T", "45/45", "45x45" };

        public static TubeEndState Parse(string? text)
        {
            switch ((text ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "TRUE":
                case "1":
                    return TubeEndState.Clean;
                case "45":
                    return TubeEndState.SingleMiter;
                case "45T":
                    return TubeEndState.SingleMiterClean;
                case "45/45":
                    return TubeEndState.DoubleMiterFacing;
                case "45X45":
                    return TubeEndState.DoubleMiterOpposite;
                default:
                    // Blank, or anything unrecognized - the safe "unreviewed" default.
                    return TubeEndState.Unreviewed;
            }
        }

        // Blank for Unreviewed, matching the old NoFeatures column's "blank rather than FALSE"
        // convention - the cell should read as "nobody has looked" rather than a claim nobody made.
        public static string? Format(TubeEndState state) => state switch
        {
            TubeEndState.Clean => "TRUE",
            TubeEndState.SingleMiter => "45",
            TubeEndState.SingleMiterClean => "45T",
            TubeEndState.DoubleMiterFacing => "45/45",
            TubeEndState.DoubleMiterOpposite => "45x45",
            _ => null,
        };

        // How many of the part's two ends need a 45-degree miter cut - 0, 1 or 2.
        public static int MiteredEndCount(TubeEndState state) => state switch
        {
            TubeEndState.SingleMiter or TubeEndState.SingleMiterClean => 1,
            TubeEndState.DoubleMiterFacing or TubeEndState.DoubleMiterOpposite => 2,
            _ => 0,
        };

        // The two ends as the nesting engine sees them.
        //
        // Which end is which does not matter to a nest - a part can be cut either way round - so
        // only the counts survive: how many ends are mitered, and whether a square end is confirmed
        // clear. Two mitered ends are opposed (a parallelogram) when either is marked so, otherwise
        // facing (a taper); nothing in the engine reads the difference today, but it is kept.
        //
        // One clear square end is enough for the clamp zone even when the other end has features:
        // the part goes on the stick with its clear end toward the clamp, and the other end is then
        // beside the next part, not in the clamp.
        public static TubeEndState FromEnds(TubeEndFeature a, TubeEndFeature b)
        {
            int miters = (TubeEndFeatureText.IsMiter(a) ? 1 : 0) + (TubeEndFeatureText.IsMiter(b) ? 1 : 0);
            bool clearSquareEnd = a == TubeEndFeature.Clear || b == TubeEndFeature.Clear;

            if (miters == 2)
                return a == TubeEndFeature.MiterOpposed || b == TubeEndFeature.MiterOpposed
                    ? TubeEndState.DoubleMiterOpposite
                    : TubeEndState.DoubleMiterFacing;

            if (miters == 1)
                return clearSquareEnd ? TubeEndState.SingleMiterClean : TubeEndState.SingleMiter;

            return clearSquareEnd ? TubeEndState.Clean : TubeEndState.Unreviewed;
        }

        // The ends a legacy column M code says, for a BOM with no End Features sheet. The miter is
        // put on end A because the code does not say which end - see FromEnds.
        public static (TubeEndFeature A, TubeEndFeature B) ToEnds(TubeEndState state) => state switch
        {
            TubeEndState.Clean => (TubeEndFeature.Clear, TubeEndFeature.Clear),
            TubeEndState.SingleMiter => (TubeEndFeature.Miter, TubeEndFeature.Unreviewed),
            TubeEndState.SingleMiterClean => (TubeEndFeature.Miter, TubeEndFeature.Clear),
            TubeEndState.DoubleMiterFacing => (TubeEndFeature.Miter, TubeEndFeature.Miter),
            TubeEndState.DoubleMiterOpposite => (TubeEndFeature.Miter, TubeEndFeature.MiterOpposed),
            _ => (TubeEndFeature.Unreviewed, TubeEndFeature.Unreviewed),
        };

        // A plain sentence for what a part's ends do to its nesting, shown beside the dropdowns so
        // nobody has to remember what each combination means.
        public static string Effect(TubeEndFeature a, TubeEndFeature b)
        {
            int miters = (TubeEndFeatureText.IsMiter(a) ? 1 : 0) + (TubeEndFeatureText.IsMiter(b) ? 1 : 0);
            bool clear = a == TubeEndFeature.Clear || b == TubeEndFeature.Clear;

            if (miters == 2)
                return a == TubeEndFeature.MiterOpposed || b == TubeEndFeature.MiterOpposed
                    ? "Two opposed miters (parallelogram) - shares cuts with neighbours, never in the clamp zone"
                    : "Two facing miters (taper) - shares cuts with neighbours, never in the clamp zone";

            if (miters == 1)
                return clear
                    ? "Miter + clear end - shares a cut, clear end may use the clamp zone"
                    : "Miter - shares a cut, kept out of the clamp zone";

            if (clear)
                return "Clear end - may use the clamp zone";

            return a == TubeEndFeature.HasFeatures || b == TubeEndFeature.HasFeatures
                ? "Features near an end - kept out of the clamp zone"
                : "Unreviewed - kept out of the clamp zone";
        }

        // Whether this part has a square end confirmed clear of features, eligible for the tube
        // laser's clamp zone the same way the old NoFeatures=true did.
        public static bool ClampZoneEligible(TubeEndState state) =>
            state == TubeEndState.Clean || state == TubeEndState.SingleMiterClean;
    }

    // What is known about ONE end of a stick part - the finer-grained view the End Features tab
    // edits. Nesting only ever needs two facts about a part (how many ends are mitered, and whether
    // some square end is confirmed clear), which is what TubeEndState carries; TubeEndStateText
    // maps between the two so the engine and the old column M codes are unchanged.
    public enum TubeEndFeature
    {
        // Square, and nobody has looked - the safe default. Kept out of the clamp zone.
        Unreviewed = 0,

        // Square and reviewed: nothing within the clamp length of this end. The only kind of end
        // that may sit in the tube laser's clamp zone.
        Clear = 1,

        // A 45 degree miter. Needs real support right at the cut, so it is never in the clamp zone.
        Miter = 2,

        // Reviewed, and there IS something within the clamp length of this end (a hole, a slot).
        // Nests exactly like Unreviewed - both are kept out of the clamp zone - but says the part
        // has been looked at, which Unreviewed does not.
        HasFeatures = 3,

        // A 45 degree miter that runs the opposite way to the miter on the part's other end, so the
        // piece is a parallelogram rather than tapering to a point ("45x45"). Only means anything
        // beside another miter; with none it is just a miter. Nests exactly like Miter today.
        MiterOpposed = 4,
    }

    internal static class TubeEndFeatureText
    {
        // In the order the dropdowns offer them. Also what the End Features sheet stores.
        public static readonly string[] Options =
            { "Unreviewed", "Clear", "Miter 45", "Miter 45 opposed", "Has features" };

        public static string Format(TubeEndFeature feature) => feature switch
        {
            TubeEndFeature.Clear => Options[1],
            TubeEndFeature.Miter => Options[2],
            TubeEndFeature.MiterOpposed => Options[3],
            TubeEndFeature.HasFeatures => Options[4],
            _ => Options[0],
        };

        // Either kind of miter: what counts toward how many ends need a diagonal cut.
        public static bool IsMiter(TubeEndFeature feature) =>
            feature == TubeEndFeature.Miter || feature == TubeEndFeature.MiterOpposed;

        // Tolerant of how someone might type it into the sheet in Excel. Anything unrecognized,
        // blank included, is the safe Unreviewed - callers that want to say so use Recognized.
        public static TubeEndFeature Parse(string? text) => Parse(text, out _);

        public static TubeEndFeature Parse(string? text, out bool recognized)
        {
            string t = (text ?? string.Empty).Trim().ToUpperInvariant();
            recognized = true;

            if (t.Length == 0 || t == "UNREVIEWED")
                return TubeEndFeature.Unreviewed;

            if (t == "CLEAR" || t == "TRUE" || t == "1" || t == "REVIEWED" || t == "CLEAN")
                return TubeEndFeature.Clear;

            // Before the plain miter test, which "miter 45 opposed" would also satisfy. "45x45" is
            // the column M code for the same thing.
            if (t.Contains("OPPOS") || t == "45X45")
                return TubeEndFeature.MiterOpposed;

            if (t.Contains("MITER") || t == "45")
                return TubeEndFeature.Miter;

            if (t.Contains("FEATURE"))
                return TubeEndFeature.HasFeatures;

            recognized = false;
            return TubeEndFeature.Unreviewed;
        }
    }
}
