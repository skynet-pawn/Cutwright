using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // NestGroupCoordinator is the pure state-mutation half of MainWindow's stock/material/units
    // pickers, split out specifically so it can be exercised here against a plain PNest/TNest -
    // without a Window, and without going through UI event plumbing to reach any of it.
    public sealed class NestGroupCoordinatorTests
    {
        private static PNest OnePartSheetGroup()
        {
            var nest = new PNest(48.0f, 96.0f, 0.5f, 0.125f) { Description = "Sheet 11GA HR" };
            nest.Parts.Add(new Part(1, 2, "Sheet 11GA HR", "P-1", 23.0625f, 42.5f, 980.0f));
            return nest;
        }

        [Fact]
        public void MaterialChoiceMatchingCurrentPolicyIsNoChange()
        {
            var nest = OnePartSheetGroup();
            nest.Policy = MaterialPolicy.SheetMetal;

            var outcome = NestGroupCoordinator.ApplyMaterialChoice(nest, MaterialPolicy.SheetMetal.Name);

            Assert.Equal(NestChangeOutcome.NoChange, outcome);
        }

        [Fact]
        public void MaterialChoiceNamingNoPolicyIsNoChange()
        {
            var nest = OnePartSheetGroup();
            nest.Policy = MaterialPolicy.SheetMetal;

            var outcome = NestGroupCoordinator.ApplyMaterialChoice(nest, "Not a real material");

            Assert.Equal(NestChangeOutcome.NoChange, outcome);
            Assert.Same(MaterialPolicy.SheetMetal, nest.Policy);
        }

        [Fact]
        public void MaterialChoiceOnANestedGroupSetsThePolicyAndRenests()
        {
            var nest = OnePartSheetGroup();
            nest.Policy = MaterialPolicy.SheetMetal;

            var outcome = NestGroupCoordinator.ApplyMaterialChoice(nest, MaterialPolicy.ExpandedMetal.Name);

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Same(MaterialPolicy.ExpandedMetal, nest.Policy);
            Assert.NotEmpty(nest.Sheets);
        }

        // A group bought by the piece is not nested, so the cutting rules changing has nothing to
        // recompute - only the grid (which shows the material name) needs to know.
        [Fact]
        public void MaterialChoiceOnAPieceGroupUpdatesThePolicyWithoutRenesting()
        {
            var nest = OnePartSheetGroup();
            nest.Policy = MaterialPolicy.SheetMetal;
            nest.BuyByThePiece();

            var outcome = NestGroupCoordinator.ApplyMaterialChoice(nest, MaterialPolicy.ExpandedMetal.Name);

            Assert.Equal(NestChangeOutcome.DataOnly, outcome);
            Assert.Same(MaterialPolicy.ExpandedMetal, nest.Policy);
            Assert.Equal(SheetPurchase.Pieces, nest.Purchase);
        }

        [Fact]
        public void SheetSizeChoiceOfQtySwitchesToBuyByThePiece()
        {
            var nest = OnePartSheetGroup();

            var result = NestGroupCoordinator.ApplySheetSizeChoice(nest, "QTY");

            Assert.Equal(NestChangeOutcome.Renested, result.Outcome);
            Assert.Null(result.Error);
            Assert.Equal(SheetPurchase.Pieces, nest.Purchase);
        }

        [Fact]
        public void SheetSizeChoiceOfQtyWhenAlreadyPiecesIsNoChange()
        {
            var nest = OnePartSheetGroup();
            nest.BuyByThePiece();

            var result = NestGroupCoordinator.ApplySheetSizeChoice(nest, "QTY");

            Assert.Equal(NestChangeOutcome.NoChange, result.Outcome);
        }

        [Fact]
        public void SheetSizeChoiceOfSmallestDropSizesToTheGroupsOwnParts()
        {
            var nest = OnePartSheetGroup();

            var result = NestGroupCoordinator.ApplySheetSizeChoice(nest, "Smallest Drop");

            Assert.Equal(NestChangeOutcome.Renested, result.Outcome);
            Assert.True(nest.SizeToSmallestDrop);
        }

        [Fact]
        public void SheetSizeChoiceThatIsNotAUsableSizeReportsAnErrorAndChangesNothing()
        {
            var nest = OnePartSheetGroup();
            nest.SheetWidth = 48.0f;
            nest.SheetLength = 96.0f;

            var result = NestGroupCoordinator.ApplySheetSizeChoice(nest, "garbage");

            Assert.Equal(NestChangeOutcome.NoChange, result.Outcome);
            Assert.NotNull(result.Error);
            Assert.Contains("garbage", result.Error, StringComparison.Ordinal);
            Assert.Equal(48.0f, nest.SheetWidth);
            Assert.Equal(96.0f, nest.SheetLength);
        }

        [Fact]
        public void SheetSizeChoiceMatchingTheCurrentFixedSizeIsNoChange()
        {
            var nest = OnePartSheetGroup();
            nest.SheetWidth = 48.0f;
            nest.SheetLength = 96.0f;
            nest.Nest();

            var result = NestGroupCoordinator.ApplySheetSizeChoice(nest, "48 x 96");

            Assert.Equal(NestChangeOutcome.NoChange, result.Outcome);
        }

        [Fact]
        public void SheetSizeChoiceOfADifferentFixedSizeRenests()
        {
            var nest = OnePartSheetGroup();
            nest.SheetWidth = 48.0f;
            nest.SheetLength = 96.0f;

            var result = NestGroupCoordinator.ApplySheetSizeChoice(nest, "48 x 120");

            Assert.Equal(NestChangeOutcome.Renested, result.Outcome);
            Assert.Equal(48.0f, nest.SheetWidth);
            Assert.Equal(120.0f, nest.SheetLength);
            Assert.False(nest.SizeToSmallestDrop);
        }

        private static TNest OnePartTubeGroup()
        {
            var nest = new TNest { Description = "SQ Tube 2 x 2 x 11 GA" };
            nest.Parts.Add(new Part(1, 3, "SQ Tube 2 x 2 x 11 GA", "P-1", 0f, 42.5f, 0f));
            return nest;
        }

        [Fact]
        public void StickLengthChoiceOfANumberSetsAFixedLengthAndRenests()
        {
            var nest = OnePartTubeGroup();

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(nest, "144");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Equal(144f, nest.StickLength);
            Assert.False(nest.SizeToSmallestDrop);
        }

        [Fact]
        public void StickLengthChoiceOfSmallestDropSizesToTheGroupsOwnParts()
        {
            var nest = OnePartTubeGroup();

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(nest, "Smallest Drop");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.True(nest.SizeToSmallestDrop);
        }

        [Fact]
        public void StickLengthChoiceOfFtBuysByTheFootInsteadOfNesting()
        {
            var nest = OnePartTubeGroup();

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(nest, "FT");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Empty(nest.Sticks);
            Assert.All(nest.Parts, p => Assert.Equal(UOM.FT, p.PartUOM));
        }

        [Fact]
        public void StickLengthChoiceThatIsNotRecognizedIsNoChange()
        {
            var nest = OnePartTubeGroup();
            nest.StickLength = 240f;

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(nest, "not a length");

            Assert.Equal(NestChangeOutcome.NoChange, outcome);
            Assert.Equal(240f, nest.StickLength);
        }

        [Fact]
        public void CutMethodChoiceOfSawSetsCutOnSawAndRenests()
        {
            var nest = OnePartTubeGroup();
            Assert.False(nest.CutOnSaw);

            var outcome = NestGroupCoordinator.ApplyCutMethodChoice(nest, "Saw");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.True(nest.CutOnSaw);
        }

        [Fact]
        public void CutMethodChoiceOfTubeLaserClearsCutOnSawAndRenests()
        {
            var nest = OnePartTubeGroup();
            nest.CutOnSaw = true;

            var outcome = NestGroupCoordinator.ApplyCutMethodChoice(nest, "Tube Laser");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.False(nest.CutOnSaw);
        }

        [Fact]
        public void CutMethodChoiceMatchingTheCurrentMethodIsNoChange()
        {
            var nest = OnePartTubeGroup();

            var outcome = NestGroupCoordinator.ApplyCutMethodChoice(nest, "Tube Laser");

            Assert.Equal(NestChangeOutcome.NoChange, outcome);
        }

        [Fact]
        public void CutMethodChoiceThatIsNotRecognizedIsNoChange()
        {
            var nest = OnePartTubeGroup();

            var outcome = NestGroupCoordinator.ApplyCutMethodChoice(nest, "not a method");

            Assert.Equal(NestChangeOutcome.NoChange, outcome);
            Assert.False(nest.CutOnSaw);
        }

        // The whole point of rescaling from PerUnitQuantity rather than the live quantity: two
        // single-step changes (1 -> 2, 2 -> 3) must land on the same numbers as one change straight
        // from 1 to 3, with no rounding compounding from the first step.
        [Fact]
        public void UnitsChangeRescalesFromPerUnitQuantityNotFromTheCurrentQuantity()
        {
            var parser = new FileParser();
            var nest = OnePartSheetGroup();
            nest.Parts[0].PerUnitQuantity = 5;
            nest.Parts[0].quantity = 5;
            parser.PNestList.Add(nest);

            NestGroupCoordinator.ApplyUnitsChange(parser, 2);
            NestGroupCoordinator.ApplyUnitsChange(parser, 3);

            Assert.Equal(15, nest.Parts[0].quantity);
            Assert.Equal(3, parser.Units);
        }

        // A group bought by the piece has no sheet count for a units change to disagree with -
        // only the quantity, which is updated regardless of purchase mode.
        [Fact]
        public void UnitsChangeUpdatesQuantityButDoesNotRenestAPieceGroup()
        {
            var parser = new FileParser();
            var nest = OnePartSheetGroup();
            nest.Parts[0].PerUnitQuantity = 2;
            nest.BuyByThePiece();
            var sheetsBefore = nest.Sheets;
            parser.PNestList.Add(nest);

            NestGroupCoordinator.ApplyUnitsChange(parser, 4);

            Assert.Equal(8, nest.Parts[0].quantity);
            Assert.Equal(SheetPurchase.Pieces, nest.Purchase);
            Assert.Same(sheetsBefore, nest.Sheets);
        }

        [Fact]
        public void SpacingChangeAppliesToEveryGroupAndRenests()
        {
            var parser = new FileParser();
            var nest = OnePartSheetGroup();
            nest.SheetSpacing = 0.125f;
            nest.PartSpacing = 0.125f;
            parser.PNestList.Add(nest);

            NestGroupCoordinator.ApplySpacingChange(parser, 0.25f, 0.25f);

            Assert.Equal(0.25f, nest.SheetSpacing);
            Assert.Equal(0.25f, nest.PartSpacing);
            Assert.NotEmpty(nest.Sheets);
        }

        // A group bought by the piece is not nested, so a spacing change has nothing to recompute
        // - the value is still recorded, for if it goes back to a sheet size.
        [Fact]
        public void SpacingChangeUpdatesAPieceGroupWithoutRenesting()
        {
            var parser = new FileParser();
            var nest = OnePartSheetGroup();
            nest.BuyByThePiece();
            var sheetsBefore = nest.Sheets;
            parser.PNestList.Add(nest);

            NestGroupCoordinator.ApplySpacingChange(parser, 0.25f, 0.25f);

            Assert.Equal(0.25f, nest.SheetSpacing);
            Assert.Equal(0.25f, nest.PartSpacing);
            Assert.Same(sheetsBefore, nest.Sheets);
        }

        [Fact]
        public void GroupSpacingChangeRenestsOnlyThatGroup()
        {
            var changed = OnePartSheetGroup();
            var untouched = OnePartSheetGroup();
            changed.SheetSpacing = 0.125f;
            changed.PartSpacing = 0.125f;
            untouched.SheetSpacing = 0.125f;
            untouched.PartSpacing = 0.125f;

            var outcome = NestGroupCoordinator.ApplyGroupSpacingChange(changed, 0.5f, 0.375f);

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Equal(0.5f, changed.SheetSpacing);
            Assert.Equal(0.375f, changed.PartSpacing);
            Assert.Equal(0.125f, untouched.SheetSpacing);
            Assert.Equal(0.125f, untouched.PartSpacing);
        }

        [Fact]
        public void GroupSpacingChangeToTheSameValuesIsNoChange()
        {
            var nest = OnePartSheetGroup();
            nest.SheetSpacing = 0.25f;
            nest.PartSpacing = 0.25f;

            Assert.Equal(NestChangeOutcome.NoChange,
                NestGroupCoordinator.ApplyGroupSpacingChange(nest, 0.25f, 0.25f));
        }

        [Fact]
        public void StickLengthChoiceOfQtyBuysThePartsByThePieceAndClearsTheNest()
        {
            var nest = OnePartTubeGroup();
            nest.Nest();
            Assert.NotEmpty(nest.Sticks);

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(nest, "QTY");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Equal(StickPurchase.Pieces, nest.Purchase);
            Assert.Empty(nest.Sticks);
            Assert.Equal(0, nest.StickCount);
            Assert.Equal(3, nest.PurchaseQuantity);
        }

        [Fact]
        public void StickLengthChoiceOfQtyWhenAlreadyPiecesIsNoChange()
        {
            var nest = OnePartTubeGroup();
            nest.BuyByThePiece();

            Assert.Equal(NestChangeOutcome.NoChange, NestGroupCoordinator.ApplyStickLengthChoice(nest, "QTY"));
        }

        [Fact]
        public void PickingALengthAfterQtyGoesBackToSticks()
        {
            var nest = OnePartTubeGroup();
            NestGroupCoordinator.ApplyStickLengthChoice(nest, "QTY");

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(nest, "240");

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Equal(StickPurchase.Sticks, nest.Purchase);
            Assert.NotEmpty(nest.Sticks);
            Assert.Equal(nest.StickCount, nest.PurchaseQuantity);
        }

        [Fact]
        public void ALengthPickedAfterByTheFootMarksThePartsAsSticksAgain()
        {
            var nest = OnePartTubeGroup();
            NestGroupCoordinator.ApplyStickLengthChoice(nest, "FT");
            Assert.All(nest.Parts, p => Assert.Equal(UOM.FT, p.PartUOM));

            NestGroupCoordinator.ApplyStickLengthChoice(nest, "240");

            Assert.All(nest.Parts, p => Assert.Equal(UOM.EA, p.PartUOM));
        }

        [Fact]
        public void CutMethodChangeOnAByThePieceGroupIsRecordedWithoutRenesting()
        {
            var nest = OnePartTubeGroup();
            nest.BuyByThePiece();

            var outcome = NestGroupCoordinator.ApplyCutMethodChoice(nest, "Saw");

            Assert.Equal(NestChangeOutcome.DataOnly, outcome);
            Assert.True(nest.CutOnSaw);
            Assert.Empty(nest.Sticks);
            Assert.Equal(StickPurchase.Pieces, nest.Purchase);
        }

        [Fact]
        public void UnitsChangeUpdatesAByThePieceStickGroupWithoutRenesting()
        {
            var parser = new FileParser();
            var nest = OnePartTubeGroup();
            nest.Parts[0].PerUnitQuantity = 3;
            nest.BuyByThePiece();
            parser.TNestList.Add(nest);

            NestGroupCoordinator.ApplyUnitsChange(parser, 5);

            Assert.Equal(15, nest.Parts[0].quantity);
            Assert.Equal(15, nest.PurchaseQuantity);
            Assert.Empty(nest.Sticks);
            Assert.Equal(StickPurchase.Pieces, nest.Purchase);
        }

        [Fact]
        public void StickSettingsChangeRenestsOnlyThatGroup()
        {
            var changed = OnePartTubeGroup();
            var untouched = OnePartTubeGroup();

            var outcome = NestGroupCoordinator.ApplyGroupStickSettingsChange(changed, 0.25f, 2f);

            Assert.Equal(NestChangeOutcome.Renested, outcome);
            Assert.Equal(0.25f, changed.Kerf);
            Assert.Equal(2f, changed.MinClampLength);
            Assert.Equal(TNest.DefaultKerf, untouched.Kerf);
            Assert.Equal(TNest.DefaultMinClampLength, untouched.MinClampLength);
        }

        [Fact]
        public void StickSettingsChangeToTheSameValuesIsNoChange()
        {
            var nest = OnePartTubeGroup();

            Assert.Equal(NestChangeOutcome.NoChange,
                NestGroupCoordinator.ApplyGroupStickSettingsChange(nest, TNest.DefaultKerf, TNest.DefaultMinClampLength));
        }

        [Fact]
        public void StickSettingsChangeOnAByThePieceGroupIsRecordedWithoutRenesting()
        {
            var nest = OnePartTubeGroup();
            nest.BuyByThePiece();

            var outcome = NestGroupCoordinator.ApplyGroupStickSettingsChange(nest, 0.25f, 2f);

            Assert.Equal(NestChangeOutcome.DataOnly, outcome);
            Assert.Equal(0.25f, nest.Kerf);
            Assert.Empty(nest.Sticks);
        }

        // The point of the setting: a bigger reserved length at the clamped end really does change
        // what fits on a stick.
        [Fact]
        public void AMinCutLengthThatLeavesNoRoomStopsThePartFitting()
        {
            var nest = OnePartTubeGroup();
            nest.StickLength = 48f;
            nest.Nest();
            Assert.Empty(nest.UnnestedList);

            NestGroupCoordinator.ApplyGroupStickSettingsChange(nest, TNest.DefaultKerf, 10f);

            Assert.NotEmpty(nest.UnnestedList);
        }
    }
}
