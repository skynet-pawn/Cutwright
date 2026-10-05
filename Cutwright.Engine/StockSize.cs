using System.Globalization;

namespace Cutwright
{
    // The "W x L" text the stock selector shows for a sheet.
    //
    // Formatting and parsing live together because they have to agree. The grid offers a nest's
    // current size by formatting it, and applying the user's pick reads it back - so if the two
    // ever disagree, picking a size does nothing and the grid shows stock the nest is not using.
    // That happened: applying a pick used to test the text against a fixed list of known options,
    // which no formatted size outside that list could ever match.
    internal static class StockSize
    {
        public static string Format(double width, double length) =>
            string.Create(CultureInfo.CurrentCulture, $"{width:0.##} x {length:0.##}");

        public static bool TryParse(string? choice, out float width, out float length)
        {
            width = 0.0f;
            length = 0.0f;

            if (string.IsNullOrWhiteSpace(choice))
                return false;

            string[] parts = choice.Split('x', StringSplitOptions.TrimEntries);

            // Parsed in the same culture it was formatted in, so a decimal comma round-trips.
            return parts.Length == 2
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.CurrentCulture, out width)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.CurrentCulture, out length)
                && width > 0.0f
                && length > 0.0f;
        }
    }
}
