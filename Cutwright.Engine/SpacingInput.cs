namespace Cutwright
{
    // Reads a spacing figure the estimator typed, and says why if it could not be used.
    //
    // Separate from the window, and taking the text rather than the TextBox, for the same reason
    // the parser and the writer stopped raising their own dialogs: deciding what a typed value
    // means is not a UI concern, and it is worth being able to test the answer without one.
    //
    // The failure this replaces was silence. Both spacing boxes ran their input through three
    // nested ifs and simply left the previous value in place if it was not a positive number, so
    // the box showed one figure while the nest was built to another - the same disagreement
    // between what is on screen and what was nested that the stock size pick was rewritten to
    // remove.
    internal static class SpacingInput
    {
        // What an empty box means, and what an unusable one falls back to. This is the value the
        // boxes were already defaulted to when left blank.
        public const float Default = 0.25f;

        // Returns the spacing to nest with. An empty box is not an error - it means take the
        // default. Anything else that cannot be used adds a line to `notes` explaining what was
        // used instead, for the caller to show and log.
        public static float Read(string? text, string label, List<string> notes) =>
            Read(text, label, notes, Default);

        // The same reading for a figure with a different default - a stick group's kerf and minimum
        // cut length - and, for the one that has it, a zero that means something: no clamp allowance
        // is a real setting on a saw, where a spacing of nothing is far more likely a slip.
        public static float Read(string? text, string label, List<string> notes, float defaultValue,
            bool allowZero = false)
        {
            string trimmed = (text ?? string.Empty).Trim();

            if (trimmed.Length == 0)
                return defaultValue;

            if (!float.TryParse(trimmed, out float value))
            {
                notes.Add($"{label} '{trimmed}' is not a number, so {defaultValue:0.###}\" was used instead.");
                return defaultValue;
            }

            // Zero is rejected along with the negatives. A kerf of nothing is a real setting - it
            // is what shearing does - but that belongs to the material policy, which already
            // applies it per material. Typed here it is far more likely a slip, and a nest built
            // with parts touching is not something to do by accident.
            if (value < 0 || (value == 0 && !allowZero))
            {
                notes.Add($"{label} must be {(allowZero ? "zero or more" : "greater than zero")}, so " +
                          $"{defaultValue:0.###}\" was used instead of {value:0.###}\".");
                return defaultValue;
            }

            return value;
        }
    }
}
