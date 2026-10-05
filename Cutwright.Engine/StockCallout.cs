using System.Globalization;

namespace Cutwright
{
    // The stock forms the shop buys, each of which names itself a particular way.
    internal enum StockForm
    {
        Unknown = 0,

        // Flat products. Which of the two a thickness belongs to is decided by the thickness
        // itself - see StockThickness.
        Sheet,
        Plate,

        SquareTube,
        RectangularTube,
        RoundTube,

        Angle,
        FlatBar,
        SquareBar,
        Rod,

        Pipe,
        PvcPipe,

        ExpandedMetal,

        // C-Channel, U-Channel, W-Section and S-Section are all named by their AISC designation
        // rather than by dimensions, so they carry the designation through untouched.
        AiscSection
    }

    // Whether a stock form nests 1D by length (TNestList) or keeps a real 2D footprint (PNestList).
    // FileParser's only signal for that split is Part.width > 0 (see FileParser.cs's DET read),
    // so every stick form below must always carry Part.width == 0 regardless of its own
    // cross-section dimensions - those belong in the callout, not the nesting footprint.
    internal static class StockForms
    {
        public static bool IsStick(StockForm form) => form switch
        {
            StockForm.SquareTube or StockForm.RectangularTube or StockForm.RoundTube or StockForm.Angle
                or StockForm.FlatBar or StockForm.SquareBar or StockForm.Rod
                or StockForm.Pipe or StockForm.PvcPipe => true,
            _ => false
        };
    }

    // Thickness, named the way the shop names it.
    //
    // There is a ladder rather than a continuum: sheet is bought as 11 GA or 7 GA, and anything at
    // a quarter inch or over is plate and named as a fraction. A customer's decimal is snapped onto
    // that ladder because the callout has to name something buyable - ".13 still becomes 11 GA and
    // .1875 becomes 7 GA".
    internal static class StockThickness
    {
        // 11 GA is 0.1196in and 7 GA is 0.1793in, so the boundary between them sits at their
        // midpoint. Below it a thickness is 11 GA, above it 7 GA.
        private const float GaugeBoundary = 0.1495f;

        // At or over this a thickness stops being a gauge and becomes plate.
        public const float PlateFloor = 0.25f;

        // The plate thicknesses actually stocked, as numerator over 16 so every one of them prints
        // as a clean fraction.
        private static readonly (float Inches, string Text)[] PlateLadder =
        {
            (0.25f, "1/4"),
            (0.3125f, "5/16"),
            (0.375f, "3/8"),
            (0.4375f, "7/16"),
            (0.5f, "1/2"),
            (0.625f, "5/8"),
            (0.75f, "3/4"),
            (0.875f, "7/8"),
            (1.0f, "1")
        };

        // Whether a thickness is bought as sheet or as plate.
        public static StockForm FormFor(float inches) =>
            inches >= PlateFloor ? StockForm.Plate : StockForm.Sheet;

        // How a thickness is written. A gauge below plate, the nearest stocked fraction above it.
        public static string Text(float inches)
        {
            if (inches < PlateFloor)
                return inches < GaugeBoundary ? "11 GA" : "7 GA";

            (float _, string text) = PlateLadder
                .OrderBy(entry => Math.Abs(entry.Inches - inches))
                .First();

            return text;
        }
    }

    // One stock item, as the shop would buy it.
    internal sealed class MaterialSpec
    {
        public StockForm Form { get; init; }

        // Section or face dimensions, in the order the callout names them. A tube's two section
        // sizes; an angle's two legs; a flat bar's thickness and width.
        public IReadOnlyList<float> Section { get; init; } = Array.Empty<float>();

        // Wall or thickness, where the form has one.
        public float? Thickness { get; init; }

        // Pipe schedule - 40 unless the drawing named 80. Not read by StockCallout.Format, which
        // still always prints "Sch. 40" (a separate, pre-existing gap) - this exists so ordering
        // logic can tell two pipes of the same size apart.
        public int Schedule { get; init; } = 40;

        // HR unless the material is something else. Written on every steel callout, so that a
        // callout never leaves the material to be inferred.
        public string Grade { get; init; } = "HR";

        // For an AISC section, the designation as the drawing gave it - C3 x 4.1 and the like.
        public string Designation { get; init; } = string.Empty;

        // Expanded metal names its mesh rather than a thickness.
        public string Mesh { get; init; } = string.Empty;
    }

    // Writes a stock item out as an stock callout.
    //
    // The grammar was settled by going through every callout in the bills of materials under Test
    // Files and asking about each place two forms of the same thing appeared. It had drifted in ten
    // ways - "Rect Tube" against "Rec Tube", "11 GA" against "11GA", "1 1/2" against "1-1/2", and
    // a flat bar written width-first among a dozen written thickness-first. This class is where
    // that stops: the rules are here, once, and the tests fail if a callout comes out any other
    // way.
    internal static class StockCallout
    {
        public static string Format(MaterialSpec spec)
        {
            string text = spec.Form switch
            {
                StockForm.Sheet => $"Sheet {StockThickness.Text(spec.Thickness ?? 0f)} {spec.Grade}",
                StockForm.Plate => $"Plate {StockThickness.Text(spec.Thickness ?? 0f)} {spec.Grade}",

                StockForm.SquareTube =>
                    $"SQ Tube {Size(spec, 0)} x {Size(spec, 1)} x {Wall(spec)} {spec.Grade}",

                StockForm.RectangularTube =>
                    $"Rect Tube {Size(spec, 0)} x {Size(spec, 1)} x {Wall(spec)} {spec.Grade}",

                // The one form that spells out what its numbers are, because a single diameter and
                // a wall would otherwise read as two section sizes.
                StockForm.RoundTube =>
                    $"Round Tube {Size(spec, 0)}\" OD x {Wall(spec)} wall {spec.Grade}",

                StockForm.Angle =>
                    $"L Angle {Size(spec, 0)} x {Size(spec, 1)} x {Wall(spec)} {spec.Grade}",

                // Thickness first, then width. A dozen of these were written that way and one was
                // written the other way round.
                StockForm.FlatBar => $"FB {Wall(spec)} x {Size(spec, 0)} {spec.Grade}",

                StockForm.SquareBar => $"SQ Bar Stock {Size(spec, 0)} x {Size(spec, 1)} {spec.Grade}",

                StockForm.Rod => $"Rod {Size(spec, 0)}\" {spec.Grade}",

                StockForm.Pipe => $"Pipe {Size(spec, 0)}\" Sch. 40",
                StockForm.PvcPipe => $"PVC Pipe {Size(spec, 0)}\" Sch. 40",

                StockForm.ExpandedMetal => $"Exp. Metal {spec.Mesh}",

                StockForm.AiscSection => spec.Designation,

                _ => string.Empty
            };

            // Runs of whitespace collapse to one. Two spaces where every sibling has one is
            // invisible in a spreadsheet but makes a different string, which means its own nesting
            // group and its own purchase line - "FB  1/4 x 1/2" was exactly that.
            return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // A tube wall or a bar thickness. Walls follow the same ladder as flat products, so a
        // decimal off a drawing lands on something buyable.
        private static string Wall(MaterialSpec spec) =>
            spec.Thickness is null ? string.Empty : StockThickness.Text(spec.Thickness.Value);

        private static string Size(MaterialSpec spec, int index) =>
            index < spec.Section.Count ? Measure(spec.Section[index]) : string.Empty;

        // A dimension, as a whole number or a hyphenated mixed number - 2, 1-1/2, 3/4. Hyphenated
        // rather than space-separated, and without an inch mark: both appeared, and both were
        // settled.
        public static string Measure(float inches)
        {
            int whole = (int)Math.Floor(inches);
            float remainder = inches - whole;

            if (remainder < 0.001f)
                return whole.ToString(CultureInfo.InvariantCulture);

            // Sixteenths cover every fraction these drawings use, reduced so 8/16 prints as 1/2.
            int sixteenths = (int)Math.Round(remainder * 16f);

            if (sixteenths >= 16)
                return (whole + 1).ToString(CultureInfo.InvariantCulture);

            if (sixteenths == 0)
                return whole.ToString(CultureInfo.InvariantCulture);

            int denominator = 16;
            while (sixteenths % 2 == 0 && denominator % 2 == 0)
            {
                sixteenths /= 2;
                denominator /= 2;
            }

            string fraction = $"{sixteenths}/{denominator}";

            return whole == 0 ? fraction : $"{whole}-{fraction}";
        }
    }
}
