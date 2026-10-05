using System.Collections.Generic;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // Buying a sheet group by the piece rather than nesting it. Some materials - dunnage is the
    // everyday case - arrive from the supplier already cut to size, so no sheet is consumed.
    //
    // The point of these is that the count, the nest and the utilisation figure cannot describe
    // different things. The selector option this restores was removed because they did: it counted
    // BOM lines instead of parts, left the previous nest in place so the drawing still showed
    // sheets, and had no way to tell the export that the number was not a sheet count.
    public sealed class SheetPurchaseTests
    {
        // Three lines, eleven parts. The gap between the two is deliberate: the old option used
        // Parts.Count, so a test where they matched would have passed against it.
        private static PNest ThreeLinesElevenParts()
        {
            var nest = new PNest(48.0f, 96.0f, 0.5f, 0.125f)
            {
                Description = "Sheet 11GA HR"
            };

            nest.Parts.Add(new Part(1, 2, "Sheet 11GA HR", "X101-406", 23.0625f, 42.5f, 980.0f));
            nest.Parts.Add(new Part(2, 4, "Sheet 11GA HR", "X101-308", 25.6875f, 39.0f, 1002.0f));
            nest.Parts.Add(new Part(3, 5, "Sheet 11GA HR", "X101-105", 2.625f, 7.625f, 20.0f));

            return nest;
        }

        [Fact]
        public void ANestedGroupBuysSheets()
        {
            var nest = ThreeLinesElevenParts();

            Assert.Equal(SheetPurchase.Sheets, nest.Purchase);
        }

        [Fact]
        public void BuyingByThePieceCountsPartsRatherThanBomLines()
        {
            var nest = ThreeLinesElevenParts();

            nest.BuyByThePiece();

            Assert.Equal(SheetPurchase.Pieces, nest.Purchase);
            Assert.Equal(11, nest.PurchaseQuantity);
            Assert.NotEqual(nest.Parts.Count, nest.PurchaseQuantity);
        }

        // The other half of what the removed option got wrong: it left the nest alone, so the
        // drawing went on showing sheets the group was no longer buying.
        [Fact]
        public void BuyingByThePieceClearsTheNest()
        {
            var nest = ThreeLinesElevenParts();

            //As if it had already been nested onto sheets.
            nest.Sheets.Add(new Sheet(48.0f, 96.0f));
            nest.SheetCount = 3;
            nest.Efficiency = "62.4";

            nest.BuyByThePiece();

            Assert.Empty(nest.Sheets);
            Assert.Equal(0, nest.SheetCount);
        }

        // Utilisation measures how well a sheet was used. With no sheet there is nothing to
        // measure, and a leftover percentage reads as one that was.
        [Fact]
        public void BuyingByThePieceLeavesNoUtilisationFigure()
        {
            var nest = ThreeLinesElevenParts();
            nest.Efficiency = "62.4";

            nest.BuyByThePiece();

            Assert.Equal("", nest.Efficiency);
        }

        // The order this actually happens in: a loaded BOM is nested first, and only then does the
        // estimator switch a group to "QTY". So the piece count is read off parts the nester has
        // already been over, and it has to still be the quantity the BOM called for.
        [Fact]
        public void NestingFirstDoesNotConsumeTheQuantitiesThePieceCountIsReadFrom()
        {
            var nest = ThreeLinesElevenParts();

            nest.Nest();
            nest.BuyByThePiece();

            Assert.Equal(11, nest.PurchaseQuantity);
        }

        // Picking a sheet size again has to undo all of it, not just set the size back - otherwise
        // the group keeps a piece count while the grid shows a sheet size.
        [Fact]
        public void NestingAgainGoesBackToBuyingSheets()
        {
            var nest = ThreeLinesElevenParts();
            nest.BuyByThePiece();

            nest.Nest();

            Assert.Equal(SheetPurchase.Sheets, nest.Purchase);
            Assert.NotEmpty(nest.Sheets);
            Assert.True(nest.SheetCount > 0, "Nesting eleven parts should use at least one sheet.");
            Assert.Equal(nest.SheetCount, nest.PurchaseQuantity);
            Assert.NotEqual("", nest.Efficiency);
        }
    }
}
