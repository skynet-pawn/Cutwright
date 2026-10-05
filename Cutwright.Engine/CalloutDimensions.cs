using System.Globalization;
using System.Text.RegularExpressions;

namespace Cutwright
{
    // What could be read out of a material callout.
    internal readonly struct CalloutReading
    {
        private CalloutReading(bool found, float width, float length, string reason)
        {
            Found = found;
            Width = width;
            Length = length;
            Reason = reason;
        }

        public bool Found { get; }
        public float Width { get; }
        public float Length { get; }

        // Why nothing was read, for the estimator. Empty on success.
        public string Reason { get; }

        public static CalloutReading Read(float width, float length) =>
            new(true, width, length, string.Empty);

        public static CalloutReading None(string reason) => new(false, 0f, 0f, reason);
    }

    // Reads the face dimensions out of a material callout, for the two customers in three whose
    // drawings carry no dimension columns at all - the sizes are inside the description text, and
    // without this their parts import with no size and cannot be nested.
    //
    // Every rule here was derived from the four real Tabula exports in this repository rather than
    // from what a callout ought to look like, and each exists because something in them broke a
    // simpler rule:
    //
    //  * Numbers must be joined by x or X. Without that, "8815N11_STACKING_CAP" reads as 8815 by
    //    11 and "CB_PLATE_4_LINES_31" as 4 by 31 - part names, not sizes.
    //  * The first number must not be glued to a letter, digit or period. "FS6.00X0.19" is a
    //    material code and "SG1709 x 43 1/4" is a profile designation.
    //  * A thickness has to come from somewhere - a third number, or a gauge. A bare two-number
    //    group is not a plate callout: "Cover 2x1" is a note, and "Plastic Tube Plug 2 1/2" x 1""
    //    is a purchased part.
    //  * Four or more numbers is a formed part or linear stock - a channel, an angle, a bend
    //    development. The flat size cannot be recovered from folded dimensions, so it declines
    //    rather than guessing.
    //  * 1/4-20 is a thread, not the mixed number 1/4 minus 20. Across all four samples, a
    //    fraction followed by a hyphen and a whole number appears only in fasteners.
    //
    // What it will not do is decide which of three dimensions is the thickness in a case where the
    // callout genuinely does not say. It takes the two largest, which is right for plate and sheet,
    // and can be wrong for thick material like foam where the stock thickness is the deciding fact
    // and lives in the group description rather than the callout. Readings are reported for that
    // reason: the estimator sees what was taken.
    internal static class CalloutDimensions
    {
        // Purchased hardware, which has threads and lengths but no face to nest.
        private static readonly Regex Hardware = new(
            @"\b(HHCS|HCS|H\.H\.C\.S|F\.S\.H\.C\.S|NUT|WASHER|RIVET|BOLT|SCREW|NYLOCK|UNC|UNF|TPI|PLUG|STRAP|GRADE\s*\d|Gr\.\s*\d)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A thread: a fraction, a hyphen, then a whole number - 1/4-20, 1/2-13. Distinguished from
        // a mixed number, which is a whole number then a fraction - 36-1/8, 1-1/4.
        private static readonly Regex Thread = new(
            @"\d+\s*/\s*\d+\s*-\s*\d+(?!\s*/)", RegexOptions.Compiled);

        private static readonly Regex Parenthetical = new(@"\([^)]*\)", RegexOptions.Compiled);

        // A sheet gauge. Evidence that a thickness was given, which is all that is needed here -
        // the number itself is not converted, because gauge tables differ by material and a wrong
        // thickness would be worse than none.
        private static readonly Regex Gauge = new(@"\d+\s*(?:ga\b|gauge\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A number: mixed (36-1/8 or 47 7/8), fraction (3/16), decimal (.188, 2.000) or whole.
        private const string Number = @"(?:\d+[- ]\d+/\d+|\d+/\d+|\d*\.\d+|\d+)";

        private static readonly Regex Group = new(
            @"(?<![A-Za-z0-9.])" + Number + @"(?:\s*[xX×]\s*" + Number + @")+",
            RegexOptions.Compiled);

        private static readonly Regex SingleNumber = new(Number, RegexOptions.Compiled);

        public static CalloutReading Read(string? callout)
        {
            string text = callout ?? string.Empty;

            if (text.Trim().Length == 0)
                return CalloutReading.None("no description to read");

            if (Hardware.IsMatch(text) || Thread.IsMatch(text))
                return CalloutReading.None("reads as purchased hardware");

            // Counts and notes live in brackets - "(2) Bends", "(4) Cutouts", "(WT:3/16)" - and are
            // not dimensions. Inch marks are noise once the numbers are out.
            string cleaned = Parenthetical.Replace(text, " ").Replace("\"", " ");

            bool gauged = Gauge.IsMatch(cleaned);
            cleaned = Gauge.Replace(cleaned, " ");

            // The longest run of x-joined numbers. Longest rather than first because a callout is
            // usually the most specific thing in the string.
            string best = string.Empty;
            foreach (Match match in Group.Matches(cleaned))
            {
                if (match.Value.Length > best.Length)
                    best = match.Value;
            }

            if (best.Length == 0)
                return CalloutReading.None("no dimensions found in the description");

            var numbers = new List<float>();
            foreach (Match match in SingleNumber.Matches(best))
            {
                if (TryValue(match.Value, out float value) && value > 0)
                    numbers.Add(value);
            }

            if (numbers.Count >= 4)
                return CalloutReading.None($"{numbers.Count} dimensions - a formed part or linear stock, not a flat size");

            if (numbers.Count == 3)
            {
                // The thickness is the smallest of the three; the face is the other two. Which
                // position the thickness sits in varies by customer - "3/4 x 2 x 3-1/8" leads with
                // it and "2.000 x 2.000 x 0.125" ends with it - so it is found by size, not place.
                numbers.Sort();
                return CalloutReading.Read(numbers[1], numbers[2]);
            }

            if (numbers.Count == 2)
            {
                return gauged
                    ? CalloutReading.Read(Math.Min(numbers[0], numbers[1]), Math.Max(numbers[0], numbers[1]))
                    : CalloutReading.None("two dimensions and no thickness or gauge, so not a flat size");
            }

            return CalloutReading.None("only one dimension found");
        }

        private static bool TryValue(string text, out float value)
        {
            value = 0f;
            string trimmed = text.Trim();

            // Mixed: a whole number then a fraction, separated by a space or a hyphen.
            var mixed = Regex.Match(trimmed, @"^(\d+)[- ](\d+)/(\d+)$");
            if (mixed.Success)
            {
                if (!int.TryParse(mixed.Groups[1].Value, out int whole) ||
                    !int.TryParse(mixed.Groups[2].Value, out int numerator) ||
                    !int.TryParse(mixed.Groups[3].Value, out int denominator) || denominator == 0)
                {
                    return false;
                }

                value = whole + (float)numerator / denominator;
                return true;
            }

            var fraction = Regex.Match(trimmed, @"^(\d+)/(\d+)$");
            if (fraction.Success)
            {
                if (!int.TryParse(fraction.Groups[1].Value, out int numerator) ||
                    !int.TryParse(fraction.Groups[2].Value, out int denominator) || denominator == 0)
                {
                    return false;
                }

                value = (float)numerator / denominator;
                return true;
            }

            return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
