using System;
using System.Collections.Generic;
using Cutwright.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // ClusterBuilder in isolation: does it find an interleave, is the result geometrically real,
    // and is it the same answer every time.
    public sealed class InterleaveClusterTests : IDisposable
    {
        private const double UsableWidth = 60.0;
        private const double UsableLength = 120.0;

        private readonly TestShapes shapes = new();
        private readonly ITestOutputHelper output;

        public InterleaveClusterTests(ITestOutputHelper output) => this.output = output;

        public void Dispose() => shapes.Dispose();

        [Theory]
        [InlineData("l_bracket", 0.0)]
        [InlineData("l_bracket_spaced", 0.25)]
        public void LBracketInterleaves(string name, double spacing)
        {
            var geometry = shapes.Geometry(name, TestShapes.LBracket);
            AssertInterleaves(geometry, spacing, name);
        }

        // The shape that motivated the off-axis sweep: its walls are outboard, so every flush
        // slide is blocked immediately and only an off-axis offset puts a wall in the other
        // part's mouth.
        [Fact]
        public void UChannelInterleavesOffAxis()
        {
            var geometry = shapes.Geometry("u_channel", TestShapes.UChannel);
            AssertInterleaves(geometry, 0.125, "u_channel");
        }

        [Fact]
        public void TrapezoidInterleavesAlongItsSlant()
        {
            var geometry = shapes.Geometry("trapezoid", TestShapes.Trapezoid);
            AssertInterleaves(geometry, 0.0, "trapezoid");
        }

        // An asymmetric silhouette with two offset steps rather than the L's single notch -
        // CollectFlush has to find the right rotation and axis with no symmetry to lean on.
        [Fact]
        public void ZShapeInterleaves()
        {
            var geometry = shapes.Geometry("z_shape", TestShapes.ZShape);
            AssertInterleaves(geometry, 0.125, "z_shape");
        }

        [Fact]
        public void TShapeInterleaves()
        {
            var geometry = shapes.Geometry("t_shape", TestShapes.TShape);
            AssertInterleaves(geometry, 0.125, "t_shape");
        }

        // The hand-picked LBracket above (2in leg on a 12in square) is one point on a range of
        // concavity - how big a bite the notch takes out of the bounding box. A search that only
        // works at that one ratio would be a search that happens to work for the one shape anyone
        // thought to test, not one proven across the family ClusterBuilder actually has to handle.
        [Theory]
        [InlineData(1.0)]   // shallow notch, close to MinConcavity
        [InlineData(2.0)]   // the ratio LBracketInterleaves already covers
        [InlineData(4.0)]
        [InlineData(5.75)]  // deep notch, close to a plus-sign cross-section
        public void LBracketInterleavesAcrossConcavity(double leg)
        {
            const double size = 12.0;
            var outline = TestShapes.LBracketWithLeg(size, leg);
            var geometry = shapes.Geometry($"l_leg_{leg:0.##}", outline);

            if (!ClusterBuilder.IsCandidate(geometry))
            {
                // A leg shallow enough to fall under MinConcavity is correctly rejected - that is
                // IsCandidate doing its job, not a search failure - so record why instead of
                // asserting an interleave that should not happen.
                output.WriteLine($"leg={leg}: concavity {geometry.Concavity:0.###} is below " +
                                 "MinConcavity, correctly not a candidate");
                return;
            }

            AssertInterleaves(geometry, 0.125, $"l_bracket leg={leg}");
        }

        [Fact]
        public void RectangleIsNotACandidate()
        {
            var geometry = shapes.Geometry("rectangle", TestShapes.Rectangle);

            Assert.Equal(PartShapeKind.Rectangle, geometry.Kind);
            Assert.False(ClusterBuilder.IsCandidate(geometry));
        }

        // Gussets pair exactly and analytically. Searching them would be slower and no better, so
        // they must not reach the interleaver at all.
        [Fact]
        public void RightTriangleIsNotACandidate()
        {
            var geometry = shapes.Geometry("triangle", TestShapes.RightTriangle);

            Assert.Equal(PartShapeKind.RightTriangle, geometry.Kind);
            Assert.False(ClusterBuilder.IsCandidate(geometry));
        }

        [Fact]
        public void SearchIsDeterministic()
        {
            var geometry = shapes.Geometry("determinism", TestShapes.LBracket);

            var first = ClusterBuilder.Build(geometry, 0.125, UsableWidth, UsableLength, true);
            var second = ClusterBuilder.Build(geometry, 0.125, UsableWidth, UsableLength, true);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.Members.Count, second!.Members.Count);
            Assert.Equal(first.FootprintWidth, second.FootprintWidth);
            Assert.Equal(first.FootprintLength, second.FootprintLength);

            for (int i = 0; i < first.Members.Count; i++)
            {
                Assert.Equal(first.Members[i].QuarterTurns, second.Members[i].QuarterTurns);
                Assert.Equal(first.Members[i].DX, second.Members[i].DX);
                Assert.Equal(first.Members[i].DY, second.Members[i].DY);
            }
        }

        private void AssertInterleaves(PartGeometry geometry, double spacing, string name)
        {
            Assert.True(ClusterBuilder.IsCandidate(geometry), $"{name} should be a candidate");

            var cluster = ClusterBuilder.Build(geometry, spacing, UsableWidth, UsableLength, true);
            Assert.NotNull(cluster);
            Assert.True(cluster!.Members.Count >= 2, "a cluster needs at least two members");

            double standalone = (geometry.Width + spacing) * (geometry.Length + spacing);
            double footprint = (cluster.FootprintWidth + spacing) * (cluster.FootprintLength + spacing);
            double saved = 1.0 - footprint / (cluster.Members.Count * standalone);

            output.WriteLine($"{name}: {cluster.Members.Count} members in " +
                             $"{cluster.FootprintWidth:0.###} x {cluster.FootprintLength:0.###}, " +
                             $"{saved * 100:0.#}% less than nesting them separately");

            Assert.True(saved > 0, "a cluster must be smaller than the same parts nested separately");

            AssertClusterIsReal(geometry, cluster, spacing, name);

            // Any prefix of a growth sequence gets used as a shorter cluster when a quantity does
            // not divide evenly, so each one has to hold up on its own.
            for (int count = 2; count < cluster.Members.Count; count++)
            {
                var prefix = ClusterBuilder.Prefix(cluster, geometry, count);
                AssertClusterIsReal(geometry, prefix, spacing, $"{name} prefix[{count}]");
            }
        }

        private static void AssertClusterIsReal(PartGeometry geometry, InterleaveCluster cluster,
            double spacing, string name)
        {
            const double slack = 1e-6;
            var outlines = new List<List<(double X, double Y)>>();

            foreach (var member in cluster.Members)
            {
                var world = geometry.ToWorld(member.DX, member.DY, member.QuarterTurns * 90.0);
                outlines.Add(world.Outer);

                foreach (var p in world.Outer)
                {
                    Assert.True(
                        p.X >= -slack && p.Y >= -slack &&
                        p.X <= cluster.FootprintWidth + slack && p.Y <= cluster.FootprintLength + slack,
                        $"{name}: point ({p.X:0.####}, {p.Y:0.####}) falls outside the footprint " +
                        $"{cluster.FootprintWidth:0.###} x {cluster.FootprintLength:0.###} handed to the packer");
                }
            }

            var (closest, crossings) = OutlineChecks.Worst(outlines);

            Assert.Equal(0, crossings);
            Assert.True(closest >= spacing - 1e-9,
                $"{name}: closest pair {closest:0.#####} does not meet the {spacing:0.###} spacing");
        }
    }
}
