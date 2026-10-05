using System.Collections.Generic;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // BomLineOrder.ByRule against the ruleset an estimator dictated by hand: Tubes -> Pipe ->
    // Other Sticks -> Plate Steel -> Sheet Steel -> Other Sheet Goods -> Purchased, with a
    // family/size/gauge ordering inside each. Real sample BOMs confirmed there is no sortable
    // convention to reverse-engineer instead - this is the actual rule, not a guess at one.
    public sealed class BomLineOrderTests
    {
        private static Part Stick(string description, float length = 100f) =>
            new(1, 1, description, "P-1", 0f, length, 0f);

        private static Part Sheet(string description, float width = 24f, float length = 48f) =>
            new(1, 1, description, "P-1", width, length, width * length);

        private static void AssertOrder(params Part[] partsInExpectedOrder)
        {
            var shuffled = new List<Part>(partsInExpectedOrder);
            shuffled.Reverse();
            shuffled.Sort(BomLineOrder.ByRule);

            for (int i = 0; i < partsInExpectedOrder.Length; i++)
            {
                Assert.True(
                    ReferenceEquals(partsInExpectedOrder[i], shuffled[i]),
                    $"Expected '{partsInExpectedOrder[i].Description}' at position {i}, " +
                    $"got '{shuffled[i].Description}'.");
            }
        }

        [Fact]
        public void CategoriesComeInTheDictatedOrder()
        {
            AssertOrder(
                Stick("SQ Tube 2 x 2 x 11GA HR"),
                Stick("Pipe 1/2\" Sch. 40"),
                Stick("L Angle 2 x 2 x 1/4 HR"),
                Sheet("Plate 1/4 HR"),
                Sheet("Sheet 11 GA HR"),
                Sheet("Sheet 1-1/2 HDPE Black"),
                Stick("Button Head Cap Screw 5/16-18 x 1")
            );
        }

        [Fact]
        public void TubesGoSquareThenRectThenRound()
        {
            AssertOrder(
                Stick("SQ Tube 2 x 2 x 11GA HR"),
                Stick("Rect Tube 8 x 3 x 11GA HR"),
                Stick("Round Tube 2\" OD x 11GA wall HR")
            );
        }

        // Round Tube's own canonical callout - "2" OD x 11GA wall HR" - separates its numbers
        // with "OD x ... wall" rather than joining them directly, which CalloutTranslator.Read
        // could not parse back. TubesGoSquareThenRectThenRound above does not catch a Round Tube
        // that fell through to Purchased instead of Tubes, since Purchased still sorts after
        // Rect Tube either way - this checks it against something that would land ahead of
        // Purchased only if Round Tube is genuinely still in the Tubes category.
        [Fact]
        public void RoundTubeSortsWithTubesNotPurchased()
        {
            AssertOrder(
                Stick("Round Tube 2\" OD x 11GA wall HR"),
                Sheet("Sheet 1-1/2 HDPE Black"),
                Stick("Button Head Cap Screw 5/16-18 x 1")
            );
        }

        [Fact]
        public void SquareTubeSortsLargestProfileFirst()
        {
            AssertOrder(
                Stick("SQ Tube 2 x 2 x 11GA HR"),
                Stick("SQ Tube 1-1/2 x 1-1/2 x 11GA HR")
            );
        }

        [Fact]
        public void RectTubeSortsByItsLongestDimension()
        {
            AssertOrder(
                Stick("Rect Tube 8 x 3 x 11GA HR"),
                Stick("Rect Tube 4 x 2 x 11GA HR")
            );
        }

        [Fact]
        public void SameTubeSizeThickerWallWinsTheTie()
        {
            AssertOrder(
                Stick("SQ Tube 2 x 2 x 7GA HR"),
                Stick("SQ Tube 2 x 2 x 11GA HR")
            );
        }

        [Fact]
        public void PipeSortsBySizeThenSchedule()
        {
            AssertOrder(
                Stick("Pipe 1-1/2\" Sch. 40 HR"),
                Stick("Pipe 1\" Sch. 40 HR"),
                Stick("Pipe 1\" Sch. 80 HR")
            );
        }

        [Fact]
        public void OtherStickFamiliesSortAlphabeticalNotByPriority()
        {
            // Deliberately given in reverse-alphabetical/insertion order, to prove there is no
            // fixed type priority the way Tubes has one.
            AssertOrder(
                Stick("L Angle 2 x 2 x 1/4 HR"),
                Stick("FB 1/4 x 2 HR"),
                Stick("PVC Pipe 1\" Sch. 40"),
                Stick("Rod 1\" HR"),
                Stick("SQ Bar Stock 1 x 1 HR")
            );
        }

        [Fact]
        public void WithinAFamilyOtherSticksSortLargestFirst()
        {
            AssertOrder(
                Stick("L Angle 3 x 3 x 1/4 HR"),
                Stick("L Angle 2 x 2 x 1/4 HR")
            );
        }

        [Fact]
        public void PlateThenSheetThenOtherSheetGoods()
        {
            AssertOrder(
                Sheet("Plate 1/4 HR"),
                Sheet("Sheet 11 GA HR"),
                Sheet("Sheet 1-1/2 HDPE Black")
            );
        }

        [Fact]
        public void SheetsAndPlatesSortThickestFirst()
        {
            AssertOrder(
                Sheet("Plate 1 HR"),
                Sheet("Plate 1/4 HR")
            );

            AssertOrder(
                Sheet("Sheet 7 GA HR"),
                Sheet("Sheet 11 GA HR")
            );
        }

        // The rule reads "all other non-steel materials", taken literally: stainless is not
        // steel for this split, so it lands in Other Sheet Goods at any thickness rather than
        // competing with mild steel plate on the 1/4" cutoff. Flagged as an assumption to confirm.
        [Fact]
        public void StainlessAndAluminumCountAsSteelForThePlateSheetSplit()
        {
            // Stainless is thicker than the mild steel plate, so it still sorts first on
            // thickness - proof it is competing in the same Plate Steel category, not parked in
            // Other Sheet Goods regardless of size.
            AssertOrder(
                Sheet("Plate 1/2 Stainless"),
                Sheet("Plate 1/4 HR"),
                Sheet("Sheet 11 GA HR"),
                Sheet("Sheet 1-1/2 HDPE Black")
            );
        }

        // At the exact same thickness, mild steel first, then stainless, then aluminum.
        [Fact]
        public void SameThicknessMildSteelThenStainlessThenAluminum()
        {
            AssertOrder(
                Sheet("Plate 1/4 HR"),
                Sheet("Plate 1/4 Stainless"),
                Sheet("Plate 1/4 Alum")
            );
        }

        // Named by wire gauge/pitch or density rather than a thickness, so neither reads as a
        // Sheet/Plate keyword - still sheet goods, not Purchased.
        [Fact]
        public void WireMeshAndFoamAreOtherSheetGoodsNotPurchased()
        {
            AssertOrder(
                Sheet("Sheet 11 GA HR"),
                Sheet("0.120 Wire 1p0 OC Wire Mesh"),
                Stick("Button Head Cap Screw 5/16-18 x 1")
            );

            AssertOrder(
                Sheet("Sheet 11 GA HR"),
                Sheet("6# XPLE Foam 4"),
                Stick("Button Head Cap Screw 5/16-18 x 1")
            );
        }

        [Fact]
        public void WithinOneMaterialPartsSortLargestFirst()
        {
            AssertOrder(
                Stick("SQ Tube 2 x 2 x 11GA HR", length: 96f),
                Stick("SQ Tube 2 x 2 x 11GA HR", length: 24f)
            );

            AssertOrder(
                Sheet("Sheet 11 GA HR", width: 48f, length: 96f),
                Sheet("Sheet 11 GA HR", width: 12f, length: 12f)
            );
        }

        [Fact]
        public void PurchasedComesAfterEveryMaterialCategory()
        {
            AssertOrder(
                Sheet("Sheet 1-1/2 HDPE Black"),
                Stick("Button Head Cap Screw 5/16-18 x 1")
            );
        }

        // A blank PartNumber is "", not null - falling back to it directly used to tie every
        // hardware row with no part number together, leaving their relative order to whatever
        // List.Sort felt like rather than the alphabetical fallback intended.
        [Fact]
        public void HardwareWithNoPartNumberFallsBackToAlphabeticalByDescription()
        {
            var caster = new Part(1, 1, "Caster 4\" Swivel", "", 0f, 0f, 0f);
            var hinge = new Part(2, 1, "Hinge Without Holes 12\"", "", 0f, 0f, 0f);

            AssertOrder(caster, hinge);
        }

        [Fact]
        public void FastenersComeAfterOtherPurchasedItems()
        {
            AssertOrder(
                Stick("Caster 4\" Swivel"),
                Stick("Button Head Cap Screw 5/16-18 x 1 Gr. 5 ZP")
            );
        }

        // Rect Tube by "longest dimension" leaves the other dimension unranked, which used to fall
        // straight through to individual part length once two different profiles tied on it -
        // interleaving 2x1 and 2x1-1/2 parts by length instead of keeping each material together.
        [Fact]
        public void RectTubesThatTieOnTheLongestDimensionStayGroupedByTheOtherOne()
        {
            var wideOne = Stick("Rect Tube 2 x 1-1/2 x 11GA HR", length: 2f);
            var narrowLong = Stick("Rect Tube 2 x 1 x 11GA HR", length: 40f);
            var narrowShort = Stick("Rect Tube 2 x 1 x 11GA HR", length: 4f);

            // Both tie on the longest dimension (2), so the other dimension (1-1/2 vs 1) breaks
            // the tie between materials first - if it did not, the single 2-inch-long 2x1-1/2 part
            // would land between the two 2x1 parts by length alone instead of its own material
            // staying together.
            AssertOrder(wideOne, narrowLong, narrowShort);
        }

        [Theory]
        [InlineData("Button Head Cap Screw 5/16-18 x 1 Gr. 5 ZP")]
        [InlineData("Hex Nut 5/16-18")]
        [InlineData("Flat Washer 5/16")]
        [InlineData("Rivnut 5/16-18 for 11GA Wall ZP")]
        [InlineData("3/8-16 Hex Flange Serrated Locknut GR5 ZP")]
        [InlineData("Cotter Pin 1/8 x 1")]
        public void FastenerWordsAreRecognized(string description)
        {
            Assert.True(BomLineOrder.IsFastener(description));
        }

        [Theory]
        [InlineData("Caster 4\" Swivel")]
        [InlineData("Stacking Cap for 2 x 2 SQ Tube")]
        [InlineData("Gas Spring 100lb")]
        public void NonFastenerHardwareIsNotFlaggedAsAFastener(string description)
        {
            Assert.False(BomLineOrder.IsFastener(description));
        }
    }
}
