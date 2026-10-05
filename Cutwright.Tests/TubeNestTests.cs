using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // The 1D nester. Each of these guards a fault that was really in there: sticks charged for
    // parts that could never be cut, a cut length that made Nest() spin forever, and a placement
    // policy that lost sticks to plain First-Fit-Decreasing.
    public sealed class TubeNestTests
    {
        private const float StickLength = 240f;
        private const float MinClamp = 4.5f;
        private const float Kerf = 0.125f;

        private readonly ITestOutputHelper output;

        public TubeNestTests(ITestOutputHelper output) => this.output = output;

        private static TNest Build((float Length, int Qty)[] cut)
        {
            var nest = new TNest { StickLength = StickLength };

            int line = 1;
            foreach (var (length, qty) in cut)
                nest.Parts.Add(new Part(line++, qty, "TUBE TEST", $"P{line}", 1f, length, length));

            return nest;
        }

        // A part exactly StickLength - MinClampLength long used to satisfy neither the placement
        // test nor the rejection test, both being strict, so it was never placed, never counted,
        // and the loop's exit condition could never be met.
        //
        // Run on a worker with a timeout rather than inline: if this ever regresses it should fail,
        // not hang the whole suite.
        [Fact]
        public void APartThatExactlyFillsAStickTerminates()
        {
            var nest = Build(new[] { (StickLength - MinClamp, 1) });

            int sticks = -1;
            var thread = new Thread(() => { nest.Nest(); sticks = nest.StickCount; }) { IsBackground = true };
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(5)),
                $"Nest() did not terminate for a part of {StickLength - MinClamp}in");
            Assert.Equal(1, sticks);
        }

        // An oversized part cannot be cut from stock, so it must not cost stock. The old code
        // opened a stick for every rejected part, and the branch had no quantity guard, so it kept
        // firing after the line was exhausted and invented parts as well as sticks.
        [Fact]
        public void OversizedPartsCostNoSticksAndAreCountedOnce()
        {
            var nest = Build(new[] { (300f, 3), (60f, 4) });
            nest.Nest();

            Assert.Equal(3, nest.UnnestedList.Count);
            Assert.DoesNotContain(nest.Sticks, s => s.NestedParts.Count == 0);
            Assert.Equal(2, nest.StickCount);
        }

        [Fact]
        public void EveryPieceEndsUpEitherNestedOrUnnested()
        {
            var cut = new (float Length, int Qty)[] { (300f, 2), (96f, 8), (72f, 10), (24f, 5) };
            var nest = Build(cut);
            nest.Nest();

            int expected = cut.Sum(c => c.Qty);
            Assert.Equal(expected, nest.NestedList.Count + nest.UnnestedList.Count);
            Assert.Equal(nest.NestedList.Count, nest.Sticks.Sum(s => s.NestedParts.Count));
        }

        // FileWriter reads the BOM's own quantities back for by-the-foot pricing, so nesting must
        // work on copies and leave them alone.
        [Fact]
        public void NestingDoesNotConsumeTheBomQuantities()
        {
            var nest = Build(new[] { (96f, 8), (48f, 6) });
            nest.Nest();
            nest.Nest();

            Assert.Equal(new[] { 8, 6 }, nest.Parts.Select(p => p.quantity).ToArray());
        }

        // Kerf was unsettable: the live path hardcoded 0.125 and only dead code read the property.
        //
        // Measured on stick count rather than on what is left of the first stick. Five 47in pieces
        // fit one stick with no kerf (235in of 237.5in usable), but a 4in kerf pushes the fifth
        // onto a second stick - and that spill leaves the first stick with MORE remaining, not
        // less, so comparing offcuts would read backwards.
        [Fact]
        public void KerfActuallyAffectsTheNest()
        {
            var tight = Build(new[] { (47f, 5) });
            tight.Kerf = 0.0f;
            tight.Nest();

            var wide = Build(new[] { (47f, 5) });
            wide.Kerf = 4.0f;
            wide.Nest();

            Assert.Equal(1, tight.StickCount);
            Assert.True(wide.StickCount > tight.StickCount,
                $"changing Kerf made no difference: {tight.StickCount} stick(s) either way");
        }

        // Kerf leaves as chips, not as part. Counting it as used flattered the reported number and
        // disagreed with how the sheet nester measures.
        [Fact]
        public void EfficiencyCountsPartLengthOnlyNotKerf()
        {
            var nest = Build(new[] { (60f, 3) });
            nest.Nest();

            float expected = 180f / (nest.StickCount * StickLength) * 100f;
            Assert.Equal(expected, nest.Efficiency, 3);
        }

        [Fact]
        public void GroupWithNoNestablePartsHasNoSticks()
        {
            var nest = Build(new[] { (300f, 4) });
            nest.Nest();

            Assert.Empty(nest.Sticks);
            Assert.Equal(0, nest.StickCount);
            Assert.Equal(0f, nest.Efficiency);
        }

        // A clean, reviewed end doesn't need the clamp's clearance, so it can be cut from stock a
        // reviewed-featured part could not - here, longer than usableLength but still within the
        // physical stick.
        [Fact]
        public void CleanPartCanUseTheClampZone()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 1, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f)
                { EndState = TubeEndState.Clean });
            nest.Nest();

            Assert.Equal(1, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
        }

        // The same length, unreviewed, is the existing behavior: it needs clearance the stick
        // cannot give it once the clamp allowance is honored, so it goes unnested rather than
        // quietly costing a stick it does not fit.
        [Fact]
        public void SameLengthUnreviewedStillRespectsTheClamp()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 1, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f));
            nest.Nest();

            Assert.Empty(nest.Sticks);
            Assert.Single(nest.UnnestedList);
        }

        // A single miter with the square end unreviewed is exactly as conservative as a plain
        // unreviewed part - the miter itself does not grant clamp access, only a reviewed square
        // end does.
        [Fact]
        public void SingleMiterWithoutReviewStillRespectsTheClamp()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 1, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f)
                { EndState = TubeEndState.SingleMiter });
            nest.Nest();

            Assert.Empty(nest.Sticks);
            Assert.Single(nest.UnnestedList);
        }

        // "45T" - a single miter whose square end HAS been reviewed clean - gets the same clamp
        // access as a fully square, fully reviewed part.
        [Fact]
        public void SingleMiterCleanCanUseTheClampZone()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 1, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f)
                { EndState = TubeEndState.SingleMiterClean });
            nest.Nest();

            Assert.Equal(1, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
        }

        // A saw has no clamp fixture for a feature to collide with, so a saw-cut group ignores
        // the clamp allowance even for an unreviewed part - the exact case that, cut on the tube
        // laser, is rejected by SameLengthUnreviewedStillRespectsTheClamp below.
        [Fact]
        public void SawCutGroupIgnoresTheClampAllowanceForAnUnreviewedPart()
        {
            var nest = new TNest { StickLength = StickLength, CutOnSaw = true };
            nest.Parts.Add(new Part(1, 1, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f));
            nest.Nest();

            Assert.Equal(1, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
        }

        // Smallest Drop must shrink to fit the parts alone on a saw, with no clamp allowance
        // added on top - the mirror of the assertion SmallestDropTests makes for the tube laser.
        [Fact]
        public void SawCutGroupSizesSmallestDropWithNoClampAllowance()
        {
            var nest = new TNest { StickLength = StickLength, CutOnSaw = true, SizeToSmallestDrop = true };
            nest.Parts.Add(new Part(1, 2, "TUBE TEST", "P1", 1f, 60f, 0f));
            nest.Nest();

            float expected = 2 * (60f + Kerf);
            Assert.Equal(expected, nest.StickLength, 3);
        }

        // A miter cut needs real support right at the cut, unlike a plain parting cut - so a
        // double-ended miter can never use the clamp zone, no matter how "reviewed" the rest of the
        // part is. There is no clean variant of DoubleMiterFacing/Opposite for exactly this reason.
        [Fact]
        public void DoubleMiterCanNeverUseTheClampZone()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 1, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f)
                { EndState = TubeEndState.DoubleMiterFacing });
            nest.Nest();

            Assert.Empty(nest.Sticks);
            Assert.Single(nest.UnnestedList);
        }

        // The end state has to survive Nest()'s per-piece copy, not just live on the BOM line that
        // goes in - the placement loop reads it off the copies in PartList, not off Parts.
        [Fact]
        public void EndStateSurvivesThePerPieceCopy()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 2, "TUBE TEST", "P1", 1f, StickLength - 1f, 0f)
                { EndState = TubeEndState.Clean });
            nest.Nest();

            Assert.Equal(2, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
        }

        // The actual "we gained tube back" case: two single-miter parts, nested facing each other
        // on the same stick, share one diagonal cut instead of each reserving its own long-point
        // length independently. Sized so 2x118in needs two sticks without the credit (236 > the
        // 235.375 that 2 parts + kerf + one clamp allowance leaves) but fits on one stick with it
        // (236 - 2in credit = 234, comfortably under).
        [Fact]
        public void SharedMiterCutCreditsBackTheTubeWidth()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 2, "SQ Tube 2 x 2 x 11GA HR", "P1", 0f, 118f, 0f)
                { EndState = TubeEndState.SingleMiter });
            nest.Nest();

            Assert.Equal(1, nest.StickCount);
            Assert.Empty(nest.UnnestedList);
            Assert.Equal(2, nest.Sticks.Single().NestedParts.Count);
        }

        // Same two parts, but on stock whose face width can't be read back out of the description
        // (the plain "TUBE TEST" fixture used everywhere else in this file). No credit can be
        // resolved, so this is the pre-miter-feature baseline: two sticks, same as any other pair
        // this size that doesn't get a shared cut.
        [Fact]
        public void SharedMiterCutNeedsAResolvableTubeWidth()
        {
            var nest = new TNest { StickLength = StickLength };
            nest.Parts.Add(new Part(1, 2, "TUBE TEST", "P1", 0f, 118f, 0f)
                { EndState = TubeEndState.SingleMiter });
            nest.Nest();

            Assert.Equal(2, nest.StickCount);
        }

        // The placement policy. Round-robin over BOM lines used to lose to First-Fit-Decreasing on
        // 41 of these 600 cut lists, by up to 3 sticks; it now matches on all of them. The seed is
        // fixed so a regression is reproducible.
        [Fact]
        public void PlacementMatchesFirstFitDecreasingAcrossGeneratedCutLists()
        {
            var random = new Random(20260821);
            int worse = 0, better = 0, worstGap = 0;
            string worstCase = "";

            for (int trial = 0; trial < 600; trial++)
            {
                int lines = 1 + random.Next(6);
                var cut = new (float Length, int Qty)[lines];

                for (int i = 0; i < lines; i++)
                {
                    // Quarter inches, from a few inches up to most of a stick - how a real cut
                    // list reads.
                    float length = (float)(Math.Round((4.0 + random.NextDouble() * 220.0) * 4.0) / 4.0);
                    cut[i] = (length, 1 + random.Next(12));
                }

                var nest = Build(cut);
                nest.Nest();

                int reference = ReferenceFirstFitDecreasing(cut);

                if (nest.StickCount > reference)
                {
                    worse++;
                    if (nest.StickCount - reference > worstGap)
                    {
                        worstGap = nest.StickCount - reference;
                        worstCase = string.Join(", ", cut.Select(c => $"{c.Length}x{c.Qty}"));
                    }
                }
                else if (nest.StickCount < reference)
                {
                    better++;
                }
            }

            output.WriteLine($"600 cut lists: {600 - worse - better} identical, {worse} worse, {better} better");

            Assert.True(worse == 0,
                $"lost to first-fit-decreasing on {worse} cut list(s), worst by {worstGap} stick(s): {worstCase}");
        }

        // First-Fit-Decreasing written independently of TNest, with the same stick length, kerf and
        // clamp allowance, so the only thing being compared is where a piece gets put.
        private static int ReferenceFirstFitDecreasing((float Length, int Qty)[] cut)
        {
            var pieces = cut.SelectMany(c => Enumerable.Repeat(c.Length, c.Qty))
                .OrderByDescending(length => length)
                .ToList();

            var remaining = new List<float>();

            foreach (float length in pieces)
            {
                if (length > StickLength - MinClamp)
                    continue;

                int target = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (length <= remaining[i] - MinClamp)
                    {
                        target = i;
                        break;
                    }
                }

                if (target < 0)
                {
                    remaining.Add(StickLength);
                    target = remaining.Count - 1;
                }

                remaining[target] -= length + Kerf;
            }

            return remaining.Count;
        }
    }
}
