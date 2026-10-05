namespace Cutwright
{
    // What a stock/material/units pick actually did to a group, so a caller (MainWindow) knows
    // which views need refreshing without repeating the branching that decided it.
    internal enum NestChangeOutcome
    {
        // Nothing changed - the pick matched what the group already had, or named an option that
        // was not recognized.
        NoChange,

        // The group's data changed (material, purchase mode) but it was not re-nested, so the 2D/1D
        // drawing still matches what is on screen - only the grid needs refreshing.
        DataOnly,

        // The group was re-nested (or its stick count recomputed), so the drawing is stale too.
        Renested,
    }

    internal readonly struct SheetSizeChoiceResult
    {
        public NestChangeOutcome Outcome { get; init; }

        // Set only when the choice could not be applied at all - a preset list and this code
        // drifting apart, not a normal "nothing to do".
        public string? Error { get; init; }
    }

    // The pure state-mutation half of MainWindow's stock/material/units pickers: given a group and
    // a choice, decide what changes and make it, with no WPF dependency. MainWindow keeps the UI
    // half - the NestInProgress guard, MessageBox, Log, and deciding which views to refresh off the
    // returned outcome - so this class can be exercised directly against PNest/TNest in a test
    // instead of only ever running behind a live Window.
    internal static class NestGroupCoordinator
    {
        internal const string SheetQuantityOption = "QTY";
        // The stick list's "QTY" is the same choice as the sheet list's - buy the parts by the
        // piece - so it is the same text.
        internal const string StickQuantityOption = SheetQuantityOption;
        internal const string SmallestDropOption = "Smallest Drop";
        internal const string TubeLaserOption = "Tube Laser";
        internal const string SawOption = "Saw";

        // Matched on the policy's own Name rather than by parsing the label, so the list and the
        // lookup cannot drift apart - the options come from MaterialPolicy.All in the first place.
        public static NestChangeOutcome ApplyMaterialChoice(PNest pnest, string choice)
        {
            var policy = MaterialPolicy.All.FirstOrDefault(p => p.Name == choice);
            if (policy is null || ReferenceEquals(policy, pnest.Policy))
                return NestChangeOutcome.NoChange;

            pnest.Policy = policy;

            // A group bought by the piece is not nested, so there is nothing for the cutting rules
            // to change. The pick is still recorded, for if it goes back to a sheet size.
            if (pnest.Purchase == SheetPurchase.Pieces)
                return NestChangeOutcome.DataOnly;

            pnest.Nest();
            return NestChangeOutcome.Renested;
        }

        // Reads the dimensions out of the choice rather than matching it against known option text
        // - WithCurrent (MainWindow) deliberately injects sizes that are not in the preset list, so
        // matching against known text would leave the nest on its previous stock while the grid
        // showed the new size.
        public static SheetSizeChoiceResult ApplySheetSizeChoice(PNest pnest, string choice)
        {
            // Tested before parsing, because "QTY" is not a size and never will be - it says to
            // buy the parts by the piece, already cut to size by the supplier, so no sheet is
            // bought and nothing is nested.
            if (choice == SheetQuantityOption)
            {
                if (pnest.Purchase == SheetPurchase.Pieces)
                    return new SheetSizeChoiceResult { Outcome = NestChangeOutcome.NoChange };

                pnest.SizeToSmallestDrop = false;
                pnest.BuyByThePiece();
                return new SheetSizeChoiceResult { Outcome = NestChangeOutcome.Renested };
            }

            // Sizes the group to the smallest single sheet its own parts need, recomputed on every
            // re-nest rather than fixed once - a mode on the group, not a one-off size pick.
            if (choice == SmallestDropOption)
            {
                pnest.SizeToSmallestDrop = true;
                pnest.Nest();
                return new SheetSizeChoiceResult { Outcome = NestChangeOutcome.Renested };
            }

            if (!StockSize.TryParse(choice, out float width, out float length))
            {
                // Every remaining option is a "W x L" size, so this means the list and this method
                // have drifted apart.
                return new SheetSizeChoiceResult
                {
                    Outcome = NestChangeOutcome.NoChange,
                    Error = $"'{choice}' is not a usable sheet size, so the nest was left " +
                            "on its current stock.",
                };
            }

            // Picking a fixed size is also how a group comes back from "QTY" or "Smallest Drop", so
            // both are part of what "nothing changed" means.
            if (pnest.Purchase == SheetPurchase.Sheets && !pnest.SizeToSmallestDrop &&
                width == pnest.SheetWidth && length == pnest.SheetLength)
                return new SheetSizeChoiceResult { Outcome = NestChangeOutcome.NoChange };

            pnest.SizeToSmallestDrop = false;
            pnest.SheetWidth = width;
            pnest.SheetLength = length;
            pnest.Nest();
            return new SheetSizeChoiceResult { Outcome = NestChangeOutcome.Renested };
        }

        // Matched on the option's own name, same as ApplyMaterialChoice, so the list and the
        // lookup cannot drift apart.
        public static NestChangeOutcome ApplyCutMethodChoice(TNest tnest, string choice)
        {
            if (choice != SawOption && choice != TubeLaserOption)
                return NestChangeOutcome.NoChange;

            bool sawCut = choice == SawOption;
            if (sawCut == tnest.CutOnSaw)
                return NestChangeOutcome.NoChange;

            tnest.CutOnSaw = sawCut;

            // A group bought by the piece is not nested, so there is nothing for the cut method to
            // change. The pick is still recorded, for if it goes back to a stick length.
            if (tnest.Purchase == StickPurchase.Pieces)
                return NestChangeOutcome.DataOnly;

            tnest.Nest();
            return NestChangeOutcome.Renested;
        }

        public static NestChangeOutcome ApplyStickLengthChoice(TNest tnest, string choice)
        {
            // Tested first, because "QTY" is not a length: it says to buy the parts by the piece,
            // already cut to length, so no stick is bought and nothing is nested.
            if (choice == StickQuantityOption)
            {
                if (tnest.Purchase == StickPurchase.Pieces)
                    return NestChangeOutcome.NoChange;

                tnest.SizeToSmallestDrop = false;
                tnest.BuyByThePiece();
                return NestChangeOutcome.Renested;
            }

            if (choice == SmallestDropOption)
            {
                // Sizes the group to the shortest single stick its own parts need, recomputed on
                // every re-nest rather than fixed once - a mode on the group, not a one-off pick.
                tnest.SizeToSmallestDrop = true;
                ResetToSticks(tnest);
                tnest.Nest();
            }
            else if (float.TryParse(choice, out float stickLength))
            {
                tnest.SizeToSmallestDrop = false;
                tnest.StickLength = stickLength;
                ResetToSticks(tnest);
                tnest.Nest();
            }
            else if (choice == "FT")
            {
                //Buy by the foot instead of by the stick: no nesting, just total length.
                tnest.SizeToSmallestDrop = false;
                tnest.Sticks.Clear();

                float totalLength = 0.0f;

                foreach (var part in tnest.Parts)
                {
                    totalLength += part.length * part.quantity;
                    part.PartUOM = UOM.FT;
                }

                tnest.StickCount = (int)(totalLength / 12.0f);
            }
            else
            {
                return NestChangeOutcome.NoChange;
            }

            return NestChangeOutcome.Renested;
        }

        // Coming back to a stick length from "FT" leaves every part still marked by-the-foot, so the
        // export would go on quoting feet for a group that is nested on sticks again.
        private static void ResetToSticks(TNest tnest)
        {
            foreach (var part in tnest.Parts)
                part.PartUOM = UOM.EA;
        }

        // Rescales every part's quantity from its PerUnitQuantity - not from the quantity already
        // on it - so changing the unit count twice lands on the same numbers as changing it once
        // straight to the second value, instead of compounding rounding from the first change.
        //
        // A group bought by the piece, or linear stock bought by the foot, is left un-renested: there
        // is no sheet or stick count for those to disagree with, only a quantity, and that was
        // already updated above.
        public static void ApplyUnitsChange(FileParser prs, int newUnits)
        {
            foreach (var part in prs.PNestList.SelectMany(n => n.Parts)
                         .Concat(prs.TNestList.SelectMany(n => n.Parts)))
            {
                part.quantity = part.PerUnitQuantity * newUnits;
            }

            prs.Units = newUnits;

            foreach (var nest in prs.PNestList)
            {
                if (nest.Purchase == SheetPurchase.Pieces)
                    continue;

                nest.Nest();
            }

            foreach (var nest in prs.TNestList)
            {
                // Bought by the piece: nothing to renest, the quantity was already updated above.
                if (nest.Purchase == StickPurchase.Pieces)
                    continue;

                if (nest.Parts.Any(p => p.PartUOM == UOM.FT))
                {
                    float totalLength = nest.Parts.Sum(p => p.length * p.quantity);
                    nest.Sticks.Clear();
                    nest.StickCount = (int)(totalLength / 12.0f);
                    continue;
                }

                nest.Nest();
            }
        }

        // A stick part's ends were edited (End Features tab): the nest has to be redone, because a
        // miter or a clear end changes where the part can go. A group bought by the piece has no
        // nest to redo, so only the data changed.
        public static NestChangeOutcome ApplyEndFeaturesChange(TNest tnest)
        {
            if (tnest.Purchase == StickPurchase.Pieces)
                return NestChangeOutcome.DataOnly;

            tnest.Nest();
            return NestChangeOutcome.Renested;
        }

        // Applies a new kerf and minimum cut length (the clamp allowance) to one stick group. Per
        // group, like sheet spacing, because the cut a group takes is set by how it is cut. Returns
        // what changed so the caller knows which views are stale.
        public static NestChangeOutcome ApplyGroupStickSettingsChange(TNest tnest, float kerf, float minCutLength)
        {
            if (tnest.Kerf == kerf && tnest.MinClampLength == minCutLength)
                return NestChangeOutcome.NoChange;

            tnest.Kerf = kerf;
            tnest.MinClampLength = minCutLength;

            // A group bought by the piece is not nested, so the values are recorded for if it goes
            // back to a stick length and there is nothing to recompute.
            if (tnest.Purchase == StickPurchase.Pieces)
                return NestChangeOutcome.DataOnly;

            tnest.Nest();
            return NestChangeOutcome.Renested;
        }

        // Applies a new sheet/part spacing to one sheet group. Spacing is per group because what a
        // group is cut from decides what suits it: laser plate, expanded metal and foam do not want
        // the same gap. Returns what changed so the caller knows which views are stale.
        public static NestChangeOutcome ApplyGroupSpacingChange(PNest pnest, float sheetSpacing, float partSpacing)
        {
            if (pnest.SheetSpacing == sheetSpacing && pnest.PartSpacing == partSpacing)
                return NestChangeOutcome.NoChange;

            pnest.SheetSpacing = sheetSpacing;
            pnest.PartSpacing = partSpacing;

            // A group bought by the piece is not nested, so the values are recorded for if it goes
            // back to a sheet size and there is nothing to recompute.
            if (pnest.Purchase == SheetPurchase.Pieces)
                return NestChangeOutcome.DataOnly;

            pnest.Nest();
            return NestChangeOutcome.Renested;
        }

        // Applies a new sheet/part spacing to every sheet group and renests. Sheet spacing only
        // means anything for sheet groups - linear stock has its own kerf, already applied per
        // material by TNest.Nest - so TNestList is untouched here.
        public static void ApplySpacingChange(FileParser prs, float sheetSpacing, float partSpacing)
        {
            foreach (var nest in prs.PNestList)
            {
                nest.SheetSpacing = sheetSpacing;
                nest.PartSpacing = partSpacing;

                // A group bought by the piece is not nested, so there is nothing for a spacing
                // change to recompute - same as ApplyMaterialChoice and ApplyUnitsChange leave it.
                if (nest.Purchase == SheetPurchase.Pieces)
                    continue;

                nest.Nest();
            }
        }
    }
}
