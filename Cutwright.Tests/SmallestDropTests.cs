using System;
using System.Linq;
using Xunit;

namespace Cutwright.Tests
{
    // A group that needs less than one full sheet or stick buys the smallest one that actually
    // holds its own parts, instead of a full standard size. SizeToSmallestDrop is a mode on the
    // group rather than a one-off size pick, so it has to keep recomputing on every later re-nest
    // (a material change, a Units change) rather than freezing at whatever size the first nest
    // happened to land on.
    public sealed class SmallestDropTests
    {
        private static PNest SheetGroup() =>
            new PNest(48.0f, 96.0f, 0.5f, 0.125f)
            {
                Description = "Sheet Test",
                SizeToSmallestDrop = true
            };

        // The failure this search replaced: an unconstrained single packing pass had nothing
        // telling it to fill a sheet's width before running its length out, so a job that would
        // nest comfortably on a near-square sheet could come back as one long, mostly-empty strip
        // (a real case: 13.3 x 110, nearly 10:1). Many parts of ordinary, similar proportions
        // should land close to square instead - as long as that square fits under the 48in width
        // cap; a job big enough to want more than that is SmallestDropCapsWidthAt48ForALargeJob
        // below.
        [Fact]
        public void SmallestDropStaysNearSquareForAnOrdinaryBom()
        {
            var nest = SheetGroup();
            for (int i = 0; i < 4; i++)
                nest.Parts.Add(new Part(i, 1, "Sheet Test", $"P-{i}", 8.0f, 40.0f, 320.0f));

            nest.Nest();

            Assert.Equal(1, nest.SheetCount);
            Assert.Empty(nest.UnnestedList);

            float longSide = Math.Max(nest.SheetWidth, nest.SheetLength);
            float shortSide = Math.Min(nest.SheetWidth, nest.SheetLength);
            Assert.True(longSide / shortSide < 1.5f,
                $"{nest.SheetWidth} x {nest.SheetLength} is more elongated than an ordinary BOM should need");
        }

        // This shop nests a Smallest Drop onto 48in-wide stock and nothing wider - a 60in sheet is
        // a deliberate pick for a specific job, not something this size search should reach for.
        // A job whose near-square footprint would otherwise want more than that gets pinned to
        // 48in wide instead, with length absorbing however much more room the parts need - a long
        // drop off standard-width stock, not a custom-width sheet.
        [Fact]
        public void SmallestDropCapsWidthAt48ForALargeJob()
        {
            var nest = SheetGroup();
            for (int i = 0; i < 24; i++)
                nest.Parts.Add(new Part(i, 1, "Sheet Test", $"P-{i}", 8.0f, 40.0f, 320.0f));

            nest.Nest();

            Assert.Equal(1, nest.SheetCount);
            Assert.Empty(nest.UnnestedList);
            Assert.Equal(48.0f, nest.SheetWidth);
            Assert.True(nest.SheetLength > 48.0f,
                $"length {nest.SheetLength} should have grown past 48in to make up for the width cap");
        }

        // The other half of the same behaviour: a job genuinely dominated by one long, narrow
        // part has to end up rectangular to match it, not bloated out to a square sized to that
        // part's own length just to stay square. Forcing a square there would be the same mistake
        // in the other direction - buying far more material than the parts need.
        [Fact]
        public void SmallestDropStaysRectangularWhenOnePartTrulyNeedsIt()
        {
            var nest = SheetGroup();
            nest.Parts.Add(new Part(1, 1, "Sheet Test", "P-1", 4.0f, 110.0f, 440.0f));

            nest.Nest();

            Assert.Equal(1, nest.SheetCount);
            Assert.Empty(nest.UnnestedList);

            // Close to the part's own footprint (plus edge/spacing allowance) on both axes, not
            // ballooned out toward a 110 x 110 square.
            Assert.True(nest.SheetWidth < 10.0f, $"width {nest.SheetWidth} should track the part's own 4in width");
            Assert.True(nest.SheetLength < 115.0f, $"length {nest.SheetLength} should track the part's own 110in length");
        }

        [Fact]
        public void SmallestDropSheetHoldsEveryPartOnOneSheetSmallerThanStandard()
        {
            var nest = SheetGroup();
            nest.Parts.Add(new Part(1, 4, "Sheet Test", "P-1", 5.0f, 8.0f, 40.0f));
            nest.Parts.Add(new Part(2, 2, "Sheet Test", "P-2", 3.0f, 3.0f, 9.0f));

            nest.Nest();

            Assert.Equal(1, nest.SheetCount);
            Assert.Empty(nest.UnnestedList);
            Assert.True(nest.SheetWidth < 48.0f, $"width {nest.SheetWidth} should be smaller than a standard 48in sheet");
            Assert.True(nest.SheetLength < 96.0f, $"length {nest.SheetLength} should be smaller than a standard 96in sheet");
        }

        // The case a Units change or a material change puts a "Smallest Drop" group through: the
        // same group, nested again with more of its own parts, must land on a bigger drop rather
        // than keep the size the first, smaller nest picked.
        [Fact]
        public void SmallestDropGrowsWhenTheGroupGetsMoreParts()
        {
            var nest = SheetGroup();
            nest.Parts.Add(new Part(1, 2, "Sheet Test", "P-1", 5.0f, 8.0f, 40.0f));
            nest.Nest();

            float smallArea = nest.SheetWidth * nest.SheetLength;

            nest.Parts.Add(new Part(2, 20, "Sheet Test", "P-2", 5.0f, 8.0f, 40.0f));
            nest.Nest();

            Assert.Equal(1, nest.SheetCount);
            Assert.Empty(nest.UnnestedList);
            Assert.True(nest.SheetWidth * nest.SheetLength > smallArea,
                "22 parts should need a bigger drop than 2 of the same part did");
        }

        // Picking a fixed size afterward has to actually stick - a group that used to be a drop
        // must not keep recomputing one behind a size the estimator explicitly chose.
        [Fact]
        public void TurningSmallestDropOffLeavesALaterFixedSizeAlone()
        {
            var nest = SheetGroup();
            nest.Parts.Add(new Part(1, 2, "Sheet Test", "P-1", 5.0f, 8.0f, 40.0f));
            nest.Nest();

            nest.SizeToSmallestDrop = false;
            nest.SheetWidth = 48.0f;
            nest.SheetLength = 96.0f;
            nest.Nest();

            Assert.Equal(48.0f, nest.SheetWidth);
            Assert.Equal(96.0f, nest.SheetLength);
        }

        private static TNest StickGroup() =>
            new TNest { StickLength = 240.0f, SizeToSmallestDrop = true };

        [Fact]
        public void SmallestDropStickIsExactlyLongEnoughForEveryPart()
        {
            var nest = StickGroup();
            nest.Parts.Add(new Part(1, 3, "Tube Test", "T-1", 2.0f, 40.0f, 80.0f));
            nest.Parts.Add(new Part(2, 2, "Tube Test", "T-2", 2.0f, 25.0f, 50.0f));

            nest.Nest();

            float expected = nest.MinClampLength +
                (3 * (40.0f + nest.Kerf)) + (2 * (25.0f + nest.Kerf));

            Assert.Equal(1, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
            Assert.True(Math.Abs(nest.StickLength - expected) < 0.001f,
                $"expected a stick of {expected}in, got {nest.StickLength}in");
        }

        [Fact]
        public void SmallestDropStickGrowsWhenTheGroupGetsMoreParts()
        {
            var nest = StickGroup();
            nest.Parts.Add(new Part(1, 2, "Tube Test", "T-1", 2.0f, 40.0f, 80.0f));
            nest.Nest();

            float smallLength = nest.StickLength;

            nest.Parts.Add(new Part(2, 20, "Tube Test", "T-2", 2.0f, 40.0f, 80.0f));
            nest.Nest();

            Assert.Equal(1, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
            Assert.True(nest.StickLength > smallLength,
                "22 pieces should need a longer drop than 2 of the same piece did");
        }

        [Fact]
        public void TurningStickSmallestDropOffLeavesALaterFixedLengthAlone()
        {
            var nest = StickGroup();
            nest.Parts.Add(new Part(1, 2, "Tube Test", "T-1", 2.0f, 40.0f, 80.0f));
            nest.Nest();

            nest.SizeToSmallestDrop = false;
            nest.StickLength = 240.0f;
            nest.Nest();

            Assert.Equal(240.0f, nest.StickLength);
        }
    }
}
