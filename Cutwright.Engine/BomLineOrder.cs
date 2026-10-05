using System.Globalization;
using System.Text.RegularExpressions;

namespace Cutwright
{
    // Orders BOM lines the way an estimator actually lays them out on the page, not
    // alphabetically. There is no sortable convention in a real, hand-built or SolidWorks-exported
    // bill of materials - looking at several confirmed that - so this encodes the actual ruleset an
    // estimator gave by hand:
    //
    //   Tubes -> Pipe -> Other Sticks -> Plate Steel -> Sheet Steel -> Other Sheet Goods -> Purchased
    //
    // Tubes: SQ Tube -> Rect Tube -> Round Tube, each by profile size largest to smallest (Rect by
    // its longest dimension); same size, thicker wall wins. Pipe: by size largest to smallest, Sch
    // 40 before Sch 80 at the same size. Other Sticks (Angle, Channel, Flat Bar, PVC, Rod, Square
    // Bar - more will get added as they come up): no fixed priority between families, sorted
    // alphabetically, each internally by size largest to smallest. Plate/Sheet: mild steel,
    // stainless and aluminum all count as steel for this split - 1/4" or over is Plate, under is
    // Sheet - thickest to thinnest, and mild steel before stainless before aluminum at the same
    // thickness. Other Sheet Goods (wire mesh, expanded metal, foam, HDPE, UHMW, Delrin - anything
    // that is not a metal) regardless of thickness. Within any one material, individual parts sort
    // largest to smallest. Purchased: Other Purchased first, Fasteners last.
    internal static class BomLineOrder
    {
        private enum Category
        {
            Tubes = 0,
            Pipe = 1,
            OtherSticks = 2,
            PlateSteel = 3,
            SheetSteel = 4,
            OtherSheetGoods = 5,
            Purchased = 6
        }

        // True fasteners only - nuts, bolts, washers and the like. Deliberately narrower than
        // CalloutTranslator's own Hardware regex, which also catches CLIP/STRAP/PIN/CAP/LATCH and
        // the rest of "not stock to be cut" - those read as Other Purchased here, not Fasteners.
        //
        // NUT has no leading \b of its own - "Nylock Nut" needs it, but so does the compound
        // "Locknut", which has no boundary between K and N for a whole-word \bNUT\b to find.
        //
        // COTTER is the one exception pulled back out of that broader PIN bucket: a bare PIN is
        // ambiguous (a locating pin or hinge pin can be a custom machined part, not a stock
        // fastener), but a cotter pin always is one.
        private static readonly Regex Fastener = new(
            @"NUT\b|\b(BOLT|WASHER|SCREW|RIVNUT|RIVET|HHCS|HCS|F\.S\.H\.C\.S|SHCS|SETSCREW|STUD|COTTER)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsFastener(string? description) =>
            description is not null && Fastener.IsMatch(description);

        // Sheet goods named by wire gauge/pitch or density rather than a thickness - CalloutTranslator
        // has no form for either, so this is a narrow, separate recognizer just for routing them to
        // Other Sheet Goods instead of Purchased.
        private static readonly Regex OtherSheetGoodsWord = new(
            @"\b(MESH|FOAM)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The one thing most callers need - sort a List<Part> in place with List.Sort(ByRule), or
        // feed it to Enumerable.Order(ByRule) for a stable ordering.
        public static readonly IComparer<Part> ByRule = Comparer<Part>.Create(Compare);

        // No fixed priority among these - sorted alphabetically, which this list already is, so
        // its index is the alphabetical rank. New families (the estimator: "I'm sure there will be more
        // that will need adding") slot in here, in alphabetical order.
        private static readonly string[] OtherStickFamilies =
        {
            "Angle", "Channel", "Flat Bar", "PVC", "Rod", "Square Bar"
        };

        private static int FamilyRank(string family)
        {
            int index = Array.IndexOf(OtherStickFamilies, family);
            // A family not in the list yet sorts after all the known ones rather than throwing -
            // better to land somewhere than to crash a whole BOM over one new material.
            return index >= 0 ? index : OtherStickFamilies.Length;
        }

        private static int Compare(Part? a, Part? b)
        {
            if (a is null || b is null)
                return (a is null ? 0 : 1) - (b is null ? 0 : 1);

            SortKey keyA = KeyFor(a);
            SortKey keyB = KeyFor(b);

            int cmp;

            if ((cmp = keyA.Category.CompareTo(keyB.Category)) != 0) return cmp;
            if ((cmp = keyA.SubPriority.CompareTo(keyB.SubPriority)) != 0) return cmp;
            if ((cmp = keyB.PrimarySize.CompareTo(keyA.PrimarySize)) != 0) return cmp; // descending
            // Only meaningful for Plate/Sheet, where two different grades can land on the exact
            // same thickness - mild steel, then stainless, then aluminum.
            if ((cmp = keyA.GradeRank.CompareTo(keyB.GradeRank)) != 0) return cmp; // ascending
            if ((cmp = keyB.SecondarySize.CompareTo(keyA.SecondarySize)) != 0) return cmp; // descending
            // Not part of the dictated rule (only the longest dimension was named), but a Rect
            // Tube/Angle that ties on it otherwise falls straight through to individual part size,
            // interleaving two genuinely different materials - e.g. 2x1 and 2x1-1/2 both rank on
            // their "2". Breaking that tie on the other dimension first keeps each material's own
            // parts grouped together, which nothing the estimator said suggests they should not be.
            if ((cmp = keyB.MinorSize.CompareTo(keyA.MinorSize)) != 0) return cmp; // descending
            if ((cmp = keyA.Schedule.CompareTo(keyB.Schedule)) != 0) return cmp; // ascending: 40 before 80
            if ((cmp = string.Compare(keyA.FamilyTiebreak, keyB.FamilyTiebreak, StringComparison.OrdinalIgnoreCase)) != 0)
                return cmp;
            // FileParser groups a nesting group by consecutive DET rows sharing one exact
            // Description (FileParser.cs, PNestList/TNestList construction) - it has no tolerance
            // for two rows of the same material landing non-adjacent. Everything above this line
            // can tie for two genuinely different descriptions - most hardware has no
            // width/length, so an entire page of unrelated Purchased items can otherwise share an
            // identical key down to PartSize/PartNumber - so Description itself has to be a hard
            // tie-break here, before any individual part's own size or number, or one hardware
            // line can end up sorted in between two rows of another that only looks identical to
            // this comparer. Compared ordinal to match FileParser's own == check exactly.
            if ((cmp = string.CompareOrdinal(keyA.Description, keyB.Description)) != 0) return cmp;
            if ((cmp = keyB.PartSize.CompareTo(keyA.PartSize)) != 0) return cmp; // descending
            return string.Compare(keyA.Tiebreak, keyB.Tiebreak, StringComparison.OrdinalIgnoreCase);
        }

        private readonly record struct SortKey(
            int Category,
            int SubPriority,
            float PrimarySize,
            int GradeRank,
            float SecondarySize,
            float MinorSize,
            int Schedule,
            string FamilyTiebreak,
            string Description,
            float PartSize,
            string Tiebreak);

        private static SortKey KeyFor(Part part)
        {
            string description = part.Description ?? string.Empty;

            // A sheet/plate part's "size" is its footprint; a stick part's is its length. Neither
            // is meaningful for the other, so whichever pair is actually set decides which applies.
            float partSize = part.width > 0 && part.length > 0
                ? part.width * part.length
                : part.length;

            string tiebreak = string.IsNullOrEmpty(part.PartNumber) ? description : part.PartNumber;

            MaterialSpec? spec = CalloutTranslator.Read(description, null) ?? TryReadCanonicalFlatStock(description);

            if (spec is null)
            {
                // Wire mesh and foam are sheet goods - cut and nested the same way as a plate or
                // an HDPE sheet - but named by wire gauge/pitch or density rather than a thickness,
                // so neither CalloutTranslator nor the canonical-flat-stock read-back above
                // recognizes them at all. There is no size to rank them by, so they fall through to
                // the individual part's own footprint, the same as Expanded Metal already does.
                if (OtherSheetGoodsWord.IsMatch(description))
                    return new SortKey((int)Category.OtherSheetGoods, 0, 0f, 0, 0f, 0f, 0, string.Empty, description, partSize, tiebreak);

                int fastenerRank = IsFastener(description) ? 1 : 0;
                return new SortKey((int)Category.Purchased, fastenerRank, 0f, 0, 0f, 0f, 0, string.Empty, description, partSize, tiebreak);
            }

            float primary = spec.Section.Count > 0 ? spec.Section[0] : 0f;
            float minor = spec.Section.Count > 1 ? spec.Section[1] : 0f;
            float wall = spec.Thickness ?? 0f;

            return spec.Form switch
            {
                // CalloutTranslator already stores a tube/angle's Section as [max, min], so
                // Section[0] is always the longest dimension - exactly what "Rect Tube by its
                // longest dimension" needs, and no different for a square profile.
                StockForm.SquareTube =>
                    new SortKey((int)Category.Tubes, 0, primary, 0, wall, minor, 0, string.Empty, description, partSize, tiebreak),
                StockForm.RectangularTube =>
                    new SortKey((int)Category.Tubes, 1, primary, 0, wall, minor, 0, string.Empty, description, partSize, tiebreak),
                StockForm.RoundTube =>
                    new SortKey((int)Category.Tubes, 2, primary, 0, wall, 0f, 0, string.Empty, description, partSize, tiebreak),

                StockForm.Pipe =>
                    new SortKey((int)Category.Pipe, 0, primary, 0, 0f, 0f, spec.Schedule, string.Empty, description, partSize, tiebreak),

                StockForm.Angle =>
                    OtherStick("Angle", primary, wall, minor, string.Empty, description, partSize, tiebreak),
                StockForm.FlatBar =>
                    OtherStick("Flat Bar", primary, wall, 0f, string.Empty, description, partSize, tiebreak),
                StockForm.SquareBar =>
                    OtherStick("Square Bar", primary, 0f, 0f, string.Empty, description, partSize, tiebreak),
                StockForm.Rod =>
                    OtherStick("Rod", primary, 0f, 0f, string.Empty, description, partSize, tiebreak),
                StockForm.PvcPipe =>
                    OtherStick("PVC", primary, 0f, 0f, string.Empty, description, partSize, tiebreak),

                // No numeric section for an AISC designation - C3 x 4.1 sorts against W6 x 15 by
                // designation text, not a size nobody captured.
                StockForm.AiscSection =>
                    OtherStick("Channel", 0f, 0f, 0f, spec.Designation, description, partSize, tiebreak),

                // Mild steel, stainless and aluminum all count as steel for the Plate/Sheet split -
                // ranked thickest first, then by GradeRank (mild steel, stainless, aluminum) when
                // two different grades land on the same thickness.
                StockForm.Plate when IsMetal(spec) =>
                    new SortKey((int)Category.PlateSteel, 0, wall, GradeRank(spec), 0f, 0f, 0, string.Empty, description, partSize, tiebreak),
                StockForm.Sheet when IsMetal(spec) =>
                    new SortKey((int)Category.SheetSteel, 0, wall, GradeRank(spec), 0f, 0f, 0, string.Empty, description, partSize, tiebreak),

                // Non-metal flat stock (HDPE/UHMW/Delrin/anything else) and expanded metal are both
                // flat goods cut like a sheet, not a linear stick - neither was named a bucket of
                // its own, so both land here regardless of thickness.
                StockForm.Plate or StockForm.Sheet or StockForm.ExpandedMetal =>
                    new SortKey((int)Category.OtherSheetGoods, 0, wall, 0, 0f, 0f, 0, string.Empty, description, partSize, tiebreak),

                _ =>
                    new SortKey((int)Category.OtherSheetGoods, 0, wall, 0, 0f, 0f, 0, string.Empty, description, partSize, tiebreak),
            };
        }

        private static SortKey OtherStick(
            string family, float primarySize, float secondarySize, float minorSize, string familyTiebreak,
            string description, float partSize, string tiebreak) =>
            new(
                (int)Category.OtherSticks,
                FamilyRank(family),
                primarySize,
                0,
                secondarySize,
                minorSize,
                0,
                familyTiebreak.Length > 0 ? familyTiebreak : family,
                description,
                partSize,
                tiebreak);

        // CalloutTranslator.Read requires at least two embedded numbers to identify a thickness
        // among a customer's width/length/thickness combo - by design, so a part name like
        // CHECKERD_PLATE_1 is never mistaken for material. Our own canonical Sheet/Plate callouts
        // deliberately have only one number (StockCallout.Format never prints width/length for flat
        // stock - a real DET row just says "Sheet 11 GA HR", nothing else), so relaxing that guard
        // would reopen the exact false positive it exists to prevent. This is a narrow, separate
        // read-back of only what StockCallout.Format actually produces for Sheet/Plate, not a general
        // relaxation of the customer-text translator.
        private static readonly Regex FlatGauge = new(
            @"^(Sheet|Plate)\s+(\d+)\s*GA\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FlatFraction = new(
            @"^(Sheet|Plate)\s+([\d][\d\-/\.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static MaterialSpec? TryReadCanonicalFlatStock(string text)
        {
            Match match = FlatGauge.Match(text);
            float thickness;

            if (match.Success)
            {
                thickness = float.Parse(CalloutTranslator.GaugeInches(match.Groups[2].Value), CultureInfo.InvariantCulture);
            }
            else
            {
                match = FlatFraction.Match(text);
                if (!match.Success)
                    return null;

                thickness = CalloutTranslator.Value(match.Groups[2].Value);
                if (thickness <= 0f)
                    return null;
            }

            StockForm form = string.Equals(match.Groups[1].Value, "Plate", StringComparison.OrdinalIgnoreCase)
                ? StockForm.Plate
                : StockForm.Sheet;

            return new MaterialSpec { Form = form, Thickness = thickness, Grade = CalloutTranslator.Grade(text, null) };
        }

        // Mild steel, stainless and aluminum are all metal for the Plate/Sheet split - stainless is
        // stainless steel, and aluminum sheet is estimated and bought the same way, by thickness.
        // Everything else (HDPE/UHMW/Delrin/anything unrecognized) is Other Sheet Goods instead.
        // CalloutTranslator.Grade always returns exactly these three literal strings for a metal -
        // "HR" for every mild-steel synonym (HRS, CR, CRS, A-36, MS, STD, or nothing named at all),
        // "Stainless", "Alum" - so this is a precise check, not a guess.
        private static bool IsMetal(MaterialSpec spec) =>
            spec.Grade is "HR" or "Stainless" or "Alum";

        private static int GradeRank(MaterialSpec spec) => spec.Grade switch
        {
            "HR" => 0,
            "Stainless" => 1,
            "Alum" => 2,
            _ => 0
        };
    }
}
