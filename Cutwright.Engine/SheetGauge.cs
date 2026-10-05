using System.Text.RegularExpressions;

namespace Cutwright
{
    // Gauge-to-thickness tables for building real parts, and the sheet thickness reader that uses
    // them.
    //
    // Deliberately separate from StockThickness and CalloutTranslator.GaugeInches. Those exist to
    // name something buyable, so they snap onto the two gauges the shop stocks and treat an unknown gauge
    // as 11 GA - fine for a callout, wrong for a model, where 14 GA has to come out 0.0747 thick.
    // A gauge that is not in the table here is reported rather than guessed.
    internal static class SheetGauge
    {
        // Manufacturers' Standard Gauge for sheet steel.
        private static readonly Dictionary<int, double> Steel = new()
        {
            [7] = 0.1793, [8] = 0.1644, [9] = 0.1495, [10] = 0.1345, [11] = 0.1196, [12] = 0.1046,
            [13] = 0.0897, [14] = 0.0747, [16] = 0.0598, [18] = 0.0478, [20] = 0.0359, [22] = 0.0299
        };

        // Stainless has its own gauge standard - 11 GA stainless is 0.1250, not steel's 0.1196.
        private static readonly Dictionary<int, double> Stainless = new()
        {
            [7] = 0.1875, [8] = 0.1719, [9] = 0.1563, [10] = 0.1406, [11] = 0.1250, [12] = 0.1094,
            [13] = 0.0938, [14] = 0.0781, [16] = 0.0625, [18] = 0.0500, [20] = 0.0375, [22] = 0.0313
        };

        // Aluminum is gauged by Brown & Sharpe, which runs well thinner than steel at the same
        // number.
        private static readonly Dictionary<int, double> Aluminum = new()
        {
            [7] = 0.1443, [8] = 0.1285, [9] = 0.1144, [10] = 0.1019, [11] = 0.0907, [12] = 0.0808,
            [13] = 0.0720, [14] = 0.0641, [16] = 0.0508, [18] = 0.0403, [20] = 0.0320, [22] = 0.0253
        };

        private static readonly Regex GaugeToken = new(@"(\d+)\s*(?:ga\b|gauge\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Parenthetical = new(@"\([^)]*\)", RegexOptions.Compiled);

        // Grade and alloy numbers that sit in a description beside the thickness and must not be
        // read as one - "Plate 6061 Alum 1/4", "Sheet 304 SS 16 GA", "Plate A-36 3/8".
        private static readonly Regex GradeNumbers = new(
            @"\b(?:304L?|316L?|6061(?:-T\d+)?|5052(?:-H\d+)?|3003|A-?\s?36)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string Number = @"(?:\d+[- ]\d+/\d+|\d+/\d+|\d*\.\d+|\d+)";

        private static readonly Regex Group = new(
            @"(?<![A-Za-z0-9.])" + Number + @"(?:\s*[xX×]\s*" + Number + @")+", RegexOptions.Compiled);

        private static readonly Regex SingleNumber = new(
            @"(?<![A-Za-z0-9.])" + Number + @"(?![A-Za-z0-9])", RegexOptions.Compiled);

        // Past this it is not a sheet or plate - matches CalloutTranslator's own flat-stock limit,
        // with headroom for thick plate.
        private const double MaxThickness = 4.0;

        // The thickness of a gauge in inches, or null when the table has no such gauge. Plastic is
        // not gauged at all, so it has no table.
        public static double? Inches(int gauge, SwMaterial material)
        {
            var table = material switch
            {
                SwMaterial.Stainless => Stainless,
                SwMaterial.Aluminum => Aluminum,
                SwMaterial.Steel => Steel,
                _ => null
            };

            return table is not null && table.TryGetValue(gauge, out double inches) ? inches : null;
        }

        // Why Inches gave nothing, for the review grid.
        public static string GaugeProblem(int gauge, SwMaterial material) =>
            SwMaterials.IsPlastic(material)
                ? $"{material} is not gauged - call out its thickness instead of {gauge} GA"
                : $"{gauge} GA is not in the {material.ToString().ToLowerInvariant()} gauge table";

        // The gauge a description names, if it names one - "11 GA", "14GA", "16 gauge".
        public static int? GaugeIn(string description)
        {
            var match = GaugeToken.Match(Parenthetical.Replace(description, " "));
            return match.Success && int.TryParse(match.Groups[1].Value, out int gauge) ? gauge : null;
        }

        // The thickness a sheet or plate description calls for, in inches, or null with the reason
        // it could not be read.
        //
        // A gauge wins when there is one. Otherwise the thickness is a fraction or decimal: the
        // smallest of an x-joined run when a customer's "1/4 x 4 x 5" is still in the text, or the
        // only number in an stock callout like "Plate 3/8\" HR".
        public static double? ReadSheetThickness(string description, SwMaterial material, out string? problem)
        {
            problem = null;
            string text = Parenthetical.Replace(description, " ");

            int? gauge = GaugeIn(text);
            if (gauge is int g)
            {
                double? inches = Inches(g, material);
                if (inches is null)
                    problem = GaugeProblem(g, material);
                return inches;
            }

            text = GradeNumbers.Replace(text, " ").Replace("\"", " ");

            double? thickness = null;
            var group = Group.Match(text);
            if (group.Success)
            {
                thickness = SingleNumber.Matches(group.Value)
                    .Select(m => (double)CalloutTranslator.Value(m.Value))
                    .Where(v => v > 0)
                    .DefaultIfEmpty(0)
                    .Min();
            }
            else
            {
                var single = SingleNumber.Match(text);
                if (single.Success)
                    thickness = CalloutTranslator.Value(single.Value);
            }

            if (thickness is not > 0 || thickness > MaxThickness)
            {
                problem = "no thickness found in the description";
                return null;
            }

            return thickness;
        }
    }
}
