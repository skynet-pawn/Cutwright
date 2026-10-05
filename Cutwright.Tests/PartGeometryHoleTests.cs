using System;
using System.Linq;
using Cutwright.Tests.Support;
using Xunit;

namespace Cutwright.Tests
{
    // PartGeometry.FromDxf reading a hole's position correctly relative to the outer contour -
    // the bug behind a hole rendering and cutting off-center: holes were normalized against
    // geometry.Outer (already shifted to start at the origin, so its own min is trivially (0, 0))
    // instead of the outline's original, pre-shift position, so a hole came out wherever the DXF's
    // absolute coordinates happened to put it rather than where it actually sits on the part. Only
    // invisible when a part's outline happened to already sit at the DXF origin, which is why every
    // shape here is drawn well away from (0, 0) - the same as a real flat-pattern export usually is.
    public sealed class PartGeometryHoleTests : IDisposable
    {
        private readonly TestShapes shapes = new();

        public void Dispose() => shapes.Dispose();

        private static (double X, double Y)[] Square(double originX, double originY, double side) =>
            new[]
            {
                (originX, originY),
                (originX + side, originY),
                (originX + side, originY + side),
                (originX, originY + side),
            };

        [Fact]
        public void AHoleDeadCenterOfAnOffsetPartStaysDeadCenterAfterReading()
        {
            const double side = 12.0;
            const double holeSide = 2.0;

            // The part's outer contour sits far from the DXF's own origin - the common case for a
            // real flat-pattern export, and exactly the case the old code silently mishandled.
            var outer = Square(500.0, 300.0, side);
            var hole = Square(500.0 + side / 2 - holeSide / 2, 300.0 + side / 2 - holeSide / 2, holeSide);

            string path = shapes.WriteWithHoles("offset_with_hole", outer, hole);
            var geometry = PartGeometry.FromDxf(path);

            var holeRead = Assert.Single(geometry.Holes);
            double holeMinX = holeRead.Min(p => p.X);
            double holeMinY = holeRead.Min(p => p.Y);
            double holeMaxX = holeRead.Max(p => p.X);
            double holeMaxY = holeRead.Max(p => p.Y);

            double holeCenterX = (holeMinX + holeMaxX) / 2.0;
            double holeCenterY = (holeMinY + holeMaxY) / 2.0;

            Assert.Equal(side / 2.0, holeCenterX, 6);
            Assert.Equal(side / 2.0, holeCenterY, 6);
        }

        // The same check carried through ToWorld at a placement and every quarter turn - proving
        // the fix holds for what actually gets drawn and cut, not just what FromDxf returns.
        [Theory]
        [InlineData(0.0)]
        [InlineData(90.0)]
        [InlineData(180.0)]
        [InlineData(270.0)]
        public void AHoleDeadCenterStaysDeadCenterAfterPlacementAndRotation(double rotationDegrees)
        {
            const double side = 12.0;
            const double holeSide = 2.0;

            var outer = Square(500.0, 300.0, side);
            var hole = Square(500.0 + side / 2 - holeSide / 2, 300.0 + side / 2 - holeSide / 2, holeSide);

            string path = shapes.WriteWithHoles("offset_with_hole_placed", outer, hole);
            var geometry = PartGeometry.FromDxf(path);

            var (worldOuter, worldHoles) = geometry.ToWorld(50.0, 40.0, rotationDegrees);
            var worldHole = Assert.Single(worldHoles);

            double outerCenterX = (worldOuter.Min(p => p.X) + worldOuter.Max(p => p.X)) / 2.0;
            double outerCenterY = (worldOuter.Min(p => p.Y) + worldOuter.Max(p => p.Y)) / 2.0;
            double holeCenterX = (worldHole.Min(p => p.X) + worldHole.Max(p => p.X)) / 2.0;
            double holeCenterY = (worldHole.Min(p => p.Y) + worldHole.Max(p => p.Y)) / 2.0;

            Assert.Equal(outerCenterX, holeCenterX, 6);
            Assert.Equal(outerCenterY, holeCenterY, 6);
        }
    }
}
