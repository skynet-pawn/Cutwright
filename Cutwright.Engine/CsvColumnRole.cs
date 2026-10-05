namespace Cutwright
{
    // What a column of an imported CSV means.
    //
    // This is the whole contract of the import, and the reason there is no fixed CSV layout to
    // describe: the file comes from a customer's drawing by way of Tabula, and every customer's
    // table is shaped differently. Of four real samples in this repository, one has eight columns
    // and a header row, one has five with the dimensions combined into a single cell, and two have
    // no dimension columns at all. So the estimator says what each column holds, and these are the
    // things it can say.
    internal enum CsvColumnRole
    {
        // Left out of the bill of materials entirely. The default, so a column nobody has looked
        // at cannot quietly become part of a description.
        Ignore = 0,

        // Detail number - the estimator's line number for the part.
        Detail,

        // How many per unit.
        Quantity,

        // The material callout. More than one column may carry it, and they are joined in column
        // order.
        Description,

        // Also description, kept separate only because it reads as a different thing on the
        // drawing. Joined into the description alongside Description columns.
        Profile,

        // The material or grade - A-36, CST, S-7, or a customer's own code. Real drawings carry
        // this in a column of its own, and it decides how a group is nested: rotation locks, edge
        // allowance, minimum part spacing.
        //
        // It joins the description like Profile does, because our bill of materials template has no
        // material column to put it in, and because the description is what MaterialPolicy reads to
        // guess the cutting rules. So marking a column Material is what gets "A-36" in front of the
        // guesser instead of thrown away - which is the whole point, since a wrong material is a
        // wrong quote rather than a mislabel. Kept distinct from Profile so the intent is recorded
        // and so a material column can drive the policy directly if the template ever gains a place
        // for it.
        Material,

        Length,
        Width,

        // The material's thickness, and - for a tube or angle's second cross-section leg - its
        // height, each in a column of its own rather than embedded in Description (customer drawing
        // A drawing: "SHEET STEEL (LASER)"/"RECT BAR"/"ANGLE" name no dimension in the text at
        // all). Feed CalloutTranslator.TranslateFromColumns directly; unlike Length/Width they
        // never become Part.width/length themselves.
        Thickness,
        Height,

        PartNumber
    }

    internal static class CsvColumnRoles
    {
        // What the selector shows, and what each choice means. One list, so the options offered and
        // the roles understood cannot drift apart - they used to be a hardcoded array in a view
        // model and a switch over the same strings in the parser.
        private static readonly (CsvColumnRole Role, string Label)[] Ordered =
        {
            (CsvColumnRole.Ignore, "Ignore"),
            (CsvColumnRole.Detail, "DET"),
            (CsvColumnRole.Quantity, "QTY"),
            (CsvColumnRole.Description, "Description"),
            (CsvColumnRole.Profile, "Profile"),
            (CsvColumnRole.Material, "Material"),
            (CsvColumnRole.Length, "Length"),
            (CsvColumnRole.Width, "Width"),
            (CsvColumnRole.Thickness, "Thickness"),
            (CsvColumnRole.Height, "Height"),
            (CsvColumnRole.PartNumber, "Part Number")
        };

        public static IReadOnlyList<string> Labels { get; } =
            Ordered.Select(entry => entry.Label).ToList();

        public static string LabelOf(CsvColumnRole role) =>
            Ordered.First(entry => entry.Role == role).Label;

        // Unrecognised text reads as Ignore rather than throwing: the selector only ever offers
        // these labels, so anything else means the two lists have drifted, and dropping a column is
        // a better failure than losing the import.
        public static CsvColumnRole FromLabel(string? label)
        {
            foreach (var entry in Ordered)
            {
                if (string.Equals(entry.Label, label, StringComparison.OrdinalIgnoreCase))
                    return entry.Role;
            }

            return CsvColumnRole.Ignore;
        }
    }
}
