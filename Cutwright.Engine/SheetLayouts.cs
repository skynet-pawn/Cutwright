using System.Runtime.CompilerServices;

namespace Cutwright
{
    // One distinct sheet layout and how many sheets were nested that way.
    internal sealed class SheetLayout
    {
        public Sheet Representative { get; init; } = null!;
        public int Count { get; set; }
    }

    // Folds sheets that came out identical down to one apiece.
    //
    // On a repeat-quantity job this is nearly everything. A job of a few part types times a
    // thousand gives the packer the same sub-problem over and over, and it is deterministic, so it
    // fills sheet after sheet exactly the same way: 3,000 expanded metal parts measured as 834
    // sheets in 4 distinct arrangements, and 40 sheets of 14GA as a single one.
    //
    // Two things ride on this, and both need the same answer or they contradict each other - the
    // drawing collapses to one picture per layout with a multiplier, and the export writes one DXF
    // per layout because the shop runs one program that many times.
    //
    // Sheets are matched on the geometry object, position and rotation of every part, not on part
    // number. Geometry is cached per DXF and per rectangle size, so two placements sharing a
    // PartGeometry really are the same outline - which makes this exactly as strict as it needs to
    // be: identical here means the drawn picture and the written DXF are identical, whatever BOM
    // line each part came from.
    internal static class SheetLayouts
    {
        public static List<SheetLayout> Group(List<Sheet>? sheets)
        {
            var layouts = new List<SheetLayout>();

            if (sheets is null || sheets.Count == 0)
                return layouts;

            // First appearance order, so the drawing and the file names are stable run to run.
            var byKey = new Dictionary<string, SheetLayout>(StringComparer.Ordinal);

            // Geometry identity by reference, numbered in encounter order. Reference equality is
            // the right test - the engine's cache hands back one PartGeometry per distinct outline -
            // and numbering keeps the key short on a sheet holding fifty parts.
            var geometryIds = new Dictionary<PartGeometry, int>(ReferenceEqualityComparer.Instance);

            foreach (var sheet in sheets)
            {
                string key = LayoutKey(sheet, geometryIds);

                if (byKey.TryGetValue(key, out var existing))
                {
                    existing.Count++;
                    continue;
                }

                var layout = new SheetLayout { Representative = sheet, Count = 1 };
                byKey[key] = layout;
                layouts.Add(layout);
            }

            return layouts;
        }

        // True when two sheets would draw and export identically.
        public static bool SameLayout(Sheet a, Sheet b)
        {
            var geometryIds = new Dictionary<PartGeometry, int>(ReferenceEqualityComparer.Instance);

            return LayoutKey(a, geometryIds) == LayoutKey(b, geometryIds);
        }

        private static string LayoutKey(Sheet sheet, Dictionary<PartGeometry, int> geometryIds)
        {
            // Sorted, because two sheets holding the same parts in a different list order are the
            // same layout - the packer has no reason to emit them in a fixed order.
            var placements = new List<string>(sheet.NestedParts.Count);

            foreach (var part in sheet.NestedParts)
            {
                if (!geometryIds.TryGetValue(part.Geometry, out int id))
                {
                    id = geometryIds.Count;
                    geometryIds[part.Geometry] = id;
                }

                // Rounded to a ten-thousandth of an inch. Placements come from the same arithmetic
                // on the same inputs, so identical sheets agree exactly - the rounding is only so a
                // formatting difference in the last bit cannot split a layout in two.
                placements.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{id}@{part.X:F4},{part.Y:F4}r{part.RotationDegrees:F1}"));
            }

            placements.Sort(StringComparer.Ordinal);

            return string.Concat(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{sheet.SheetWidth:F4}x{sheet.SheetLength:F4}|"),
                string.Join("|", placements));
        }
    }
}
