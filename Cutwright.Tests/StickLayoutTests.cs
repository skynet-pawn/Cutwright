using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cutwright.Tests
{
    // How the 1D drawing places and shapes the parts on a stick. The nest decides which way each
    // miter runs (Stick.Placements); StickLayout turns that into positions and outlines. What has
    // to hold: shared cuts are one diagonal, and the drawing covers exactly the length the nest
    // charged - it used to draw every part at full length, past the end of a tightly mitered stick.
    public sealed class StickLayoutTests
    {
        private const string SquareTube = "SQ Tube 2 x 2 x 11GA HR";
        private const float Credit = 2f;

        private static TNest Nest(string description, TubeEndState ends, float length, int quantity)
        {
            var nest = new TNest { StickLength = 240f };
            nest.Parts.Add(new Part(1, quantity, description, "P1", 0f, length, 0f) { EndState = ends });
            nest.Nest();
            return nest;
        }

        private static StickPlacement Placed(bool shared, MiterSlant leading, MiterSlant trailing) =>
            new(shared, leading, trailing);

        [Fact]
        public void TwoSingleMitersShareOneDiagonal()
        {
            var stick = Nest(SquareTube, TubeEndState.SingleMiter, 118f, 2).Sticks.Single();

            Assert.Equal(new[]
            {
                Placed(false, MiterSlant.None, MiterSlant.LongTop),
                Placed(true, MiterSlant.LongBottom, MiterSlant.None),
            }, stick.Placements);

            Assert.Equal(new[] { 0f, 118f + 0.125f - Credit }, StickLayout.PartOffsets(stick));
        }

        // Facing miters taper, so each part has both long points on one edge and the next part,
        // sharing the diagonal, is flipped over.
        [Fact]
        public void TapersFlipFromOnePartToTheNext()
        {
            var stick = Nest(SquareTube, TubeEndState.DoubleMiterFacing, 70f, 3).Sticks.Single();

            Assert.Equal(new[]
            {
                Placed(false, MiterSlant.LongTop, MiterSlant.LongTop),
                Placed(true, MiterSlant.LongBottom, MiterSlant.LongBottom),
                Placed(true, MiterSlant.LongTop, MiterSlant.LongTop),
            }, stick.Placements);
        }

        // Opposed miters are parallelograms, which chain without flipping.
        [Fact]
        public void ParallelogramsChainTheSameWayUp()
        {
            var stick = Nest(SquareTube, TubeEndState.DoubleMiterOpposite, 70f, 3).Sticks.Single();

            Assert.Equal(new[]
            {
                Placed(false, MiterSlant.LongTop, MiterSlant.LongBottom),
                Placed(true, MiterSlant.LongTop, MiterSlant.LongBottom),
                Placed(true, MiterSlant.LongTop, MiterSlant.LongBottom),
            }, stick.Placements);
        }

        // No width to credit, so nothing is shared: each part keeps its own miter and its full
        // length on the stick.
        [Fact]
        public void WithoutACreditMitersAreDrawnButNotShared()
        {
            var nest = Nest("TUBE TEST", TubeEndState.SingleMiter, 60f, 2);
            var stick = nest.Sticks.Single();

            Assert.All(stick.Placements, p => Assert.Equal(Placed(false, MiterSlant.None, MiterSlant.LongTop), p));
            Assert.Equal(new[] { 0f, 60.125f }, StickLayout.PartOffsets(stick));
        }

        [Fact]
        public void TheDrawnMiterWidthFallsBackToTheNominalSize()
        {
            Assert.Equal(2f, Nest(SquareTube, TubeEndState.SingleMiter, 60f, 1).MiterDrawWidth());
            Assert.Equal(4f, Nest("Rect Tube 2 x 4 x 11GA HR", TubeEndState.SingleMiter, 60f, 1).MiterDrawWidth());
            Assert.Equal(0f, Nest("TUBE TEST", TubeEndState.SingleMiter, 60f, 1).MiterDrawWidth());
        }

        // The ends as the BOM's column M codes, since TubeEndState itself is internal.
        public static TheoryData<string, float, int> Mixes => new()
        {
            { "45", 118f, 6 },
            { "45T", 37.5f, 13 },
            { "45/45", 70f, 10 },
            { "45x45", 45f, 11 },
            { "TRUE", 50f, 9 },
        };

        // The last part ends where the nest stopped charging length: stick minus what is left,
        // less the kerf after the last part.
        [Theory]
        [MemberData(nameof(Mixes))]
        public void TheDrawingCoversTheLengthTheNestCharged(string ends, float length, int quantity)
        {
            foreach (var stick in Nest(SquareTube, TubeEndStateText.Parse(ends), length, quantity).Sticks)
            {
                List<float> offsets = StickLayout.PartOffsets(stick);
                float drawnEnd = offsets[^1] + stick.NestedParts[^1].length;

                Assert.Equal(stick.StickLength - stick.RemainingLength - stick.Kerf, drawnEnd, 3);
                Assert.True(drawnEnd <= stick.StickLength);
            }
        }

        // Where two parts share a cut, the first one's trailing diagonal and the second one's
        // leading diagonal are the same line, one kerf apart.
        [Theory]
        [MemberData(nameof(Mixes))]
        public void SharedDiagonalsLineUp(string ends, float length, int quantity)
        {
            foreach (var stick in Nest(SquareTube, TubeEndStateText.Parse(ends), length, quantity).Sticks)
            {
                List<float> offsets = StickLayout.PartOffsets(stick);

                for (int i = 1; i < stick.NestedParts.Count; i++)
                {
                    if (!stick.PlacementAt(i).SharedCut)
                        continue;

                    var before = StickLayout.Outline(stick.PlacementAt(i - 1), stick.NestedParts[i - 1].length, 5.0, Credit);
                    var after = StickLayout.Outline(stick.PlacementAt(i), stick.NestedParts[i].length, 5.0, Credit);

                    // Outline order: top left, top right, bottom right, bottom left.
                    Assert.Equal(offsets[i - 1] + before[1].X + stick.Kerf, offsets[i] + after[0].X, 3);
                    Assert.Equal(offsets[i - 1] + before[2].X + stick.Kerf, offsets[i] + after[3].X, 3);
                }
            }
        }

        // A miter is never in the clamp zone. Only a part with a clear square end may reach into
        // it, and then that end is the one at the clamp - so a trailing miter always stops short.
        [Theory]
        [MemberData(nameof(Mixes))]
        public void NoMiterIsDrawnInTheClampZone(string ends, float length, int quantity)
        {
            var nest = Nest(SquareTube, TubeEndStateText.Parse(ends), length, quantity);

            foreach (var stick in nest.Sticks)
                AssertNoMiterInTheClampZone(stick, nest.MinClampLength);
        }

        // The case from the screenshot: a miter + clear part (45T) is the last on the stick and
        // runs into the clamp zone, after a square part it cannot share a cut with. It goes on
        // clear end first toward the clamp, so its miter leads - it used to be drawn trailing,
        // inside the clamp.
        [Fact]
        public void AMiterAndClearPartInTheClampZoneLeadsWithItsMiter()
        {
            var nest = new TNest { StickLength = 240f };
            nest.Parts.Add(new Part(1, 1, SquareTube, "P1", 0f, 120f, 0f) { EndState = TubeEndState.Clean });
            nest.Parts.Add(new Part(2, 1, SquareTube, "P2", 0f, 116f, 0f) { EndState = TubeEndState.SingleMiterClean });
            nest.Nest();

            var stick = nest.Sticks.Single();
            Assert.True(StickLayout.PartOffsets(stick)[1] + 116f > stick.StickLength - nest.MinClampLength,
                "the 45T part should reach into the clamp zone for this test to mean anything");

            StickPlacement last = stick.PlacementAt(1);
            Assert.False(last.SharedCut);
            Assert.NotEqual(MiterSlant.None, last.Leading);
            Assert.Equal(MiterSlant.None, last.Trailing);
            Assert.False(stick.HasOpenMiterEnd);

            AssertNoMiterInTheClampZone(stick, nest.MinClampLength);
        }

        // The same part clear of the clamp zone keeps its miter trailing, where the next part can
        // share it.
        [Fact]
        public void AMiterAndClearPartOutsideTheClampZoneTrailsItsMiter()
        {
            var stick = Nest(SquareTube, TubeEndState.SingleMiterClean, 100f, 1).Sticks.Single();

            Assert.Equal(Placed(false, MiterSlant.None, MiterSlant.LongTop), stick.PlacementAt(0));
            Assert.True(stick.HasOpenMiterEnd);
        }

        private static void AssertNoMiterInTheClampZone(Stick stick, float clamp)
        {
            List<float> offsets = StickLayout.PartOffsets(stick);

            for (int i = 0; i < stick.NestedParts.Count; i++)
            {
                if (stick.PlacementAt(i).Trailing != MiterSlant.None)
                    Assert.True(offsets[i] + stick.NestedParts[i].length <= stick.StickLength - clamp + 0.001f,
                        $"part {i} ends its miter at {offsets[i] + stick.NestedParts[i].length}, inside the clamp zone");
            }
        }

        [Fact]
        public void OutlinesPullTheShortEdgeIn()
        {
            var taper = StickLayout.Outline(Placed(false, MiterSlant.LongTop, MiterSlant.LongTop), 10, 5, 2);
            Assert.Equal(new[] { (0.0, 0.0), (10.0, 0.0), (8.0, 5.0), (2.0, 5.0) }, taper);

            var parallelogram = StickLayout.Outline(Placed(false, MiterSlant.LongTop, MiterSlant.LongBottom), 10, 5, 2);
            Assert.Equal(new[] { (0.0, 0.0), (8.0, 0.0), (10.0, 5.0), (2.0, 5.0) }, parallelogram);

            var square = StickLayout.Outline(default, 10, 5, 2);
            Assert.Equal(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 5.0), (0.0, 5.0) }, square);
        }

        // A part shorter than its two miters still draws as a shape, not a bow tie.
        [Fact]
        public void AShortPartsMitersAreClamped()
        {
            var taper = StickLayout.Outline(Placed(false, MiterSlant.LongTop, MiterSlant.LongTop), 3, 5, 2);
            Assert.True(taper[3].X <= taper[2].X);
        }

        // The drawing's row multipliers fold identical sticks, so two sticks with the same cut
        // lengths but different miters must stay separate rows.
        [Fact]
        public void SameLengthsWithDifferentMitersAreDifferentPatterns()
        {
            Stick Fill(TubeEndState ends, bool shareSecond)
            {
                var stick = new Stick(240f, 0.125f, Credit);
                stick.AddPart(new Part(1, 1, SquareTube, "P1", 0f, 100f, 0f) { EndState = ends }, false);
                stick.AddPart(new Part(1, 1, SquareTube, "P1", 0f, 100f, 0f) { EndState = ends }, shareSecond);
                return stick;
            }

            Assert.True(StickPatterns.SameArrangement(Fill(TubeEndState.SingleMiter, true), Fill(TubeEndState.SingleMiter, true)));
            Assert.False(StickPatterns.SameArrangement(Fill(TubeEndState.SingleMiter, true), Fill(TubeEndState.Clean, false)));
            Assert.False(StickPatterns.SameArrangement(Fill(TubeEndState.SingleMiter, true), Fill(TubeEndState.SingleMiter, false)));
        }
    }
}
