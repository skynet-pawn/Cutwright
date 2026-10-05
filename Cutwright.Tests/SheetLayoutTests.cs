using System;
using System.Collections.Generic;
using System.Linq;
using Cutwright.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // Layout grouping, which the 2D drawing's multipliers and the one-file-per-layout export both
    // read. The claim that has to hold is the same one the stick patterns carry: the counts must
    // sum to the sheet count the estimate is built on, or the picture and the file names contradict
    // the number being quoted.
    public sealed class SheetLayoutTests : IDisposable
    {
        private const float SheetWidth = 48f;
        private const float SheetLength = 96f;

        private readonly TestShapes shapes = new();
        private readonly ITestOutputHelper output;

        public SheetLayoutTests(ITestOutputHelper output) => this.output = output;

        public void Dispose() => shapes.Dispose();

        private static SheetNestEngine Nest(List<Part> parts, MaterialPolicy? policy = null)
        {
            var engine = new SheetNestEngine();
            engine.Nest(parts, SheetWidth, SheetLength, 0.125f, 0f, policy ?? MaterialPolicy.SheetMetal);
            return engine;
        }

        [Fact]
        public void CountsSumToTheSheetCount()
        {
            var engine = Nest(new List<Part> { TestShapes.PlainPart(1, 400, "PLT-1", 10, 18) });
            var layouts = SheetLayouts.Group(engine.Sheets);

            output.WriteLine($"{engine.SheetCount} sheets in {layouts.Count} layout(s) - " +
                             string.Join(" ", layouts.Select(l => $"x{l.Count}")));

            Assert.Equal(engine.SheetCount, layouts.Sum(l => l.Count));
        }

        // The case the feature exists for. A quantity of one part fills sheet after sheet the same
        // way, so a job like this is one picture and one program.
        [Fact]
        public void ManyOfOnePartCollapseToVeryFewLayouts()
        {
            var engine = Nest(new List<Part> { TestShapes.PlainPart(1, 400, "PLT-1", 10, 18) });
            var layouts = SheetLayouts.Group(engine.Sheets);

            Assert.True(engine.SheetCount > 10, $"expected a multi-sheet nest, got {engine.SheetCount}");
            Assert.True(layouts.Count < engine.SheetCount / 4,
                $"{engine.SheetCount} sheets collapsed to only {layouts.Count} layout(s)");
        }

        [Fact]
        public void EverySheetBelongsToExactlyOneLayout()
        {
            var engine = Nest(new List<Part>
            {
                TestShapes.PlainPart(1, 120, "PLT-1", 10, 18),
                TestShapes.PlainPart(2, 60, "PLT-2", 22, 30),
                TestShapes.PlainPart(3, 200, "PLT-3", 6, 8)
            });

            var layouts = SheetLayouts.Group(engine.Sheets);

            foreach (var sheet in engine.Sheets)
            {
                int matches = layouts.Count(l => SheetLayouts.SameLayout(l.Representative, sheet));
                Assert.Equal(1, matches);
            }
        }

        [Fact]
        public void NoTwoLayoutsAreTheSame()
        {
            var engine = Nest(new List<Part>
            {
                TestShapes.PlainPart(1, 90, "PLT-1", 10, 18),
                TestShapes.PlainPart(2, 40, "PLT-2", 14, 20)
            });

            var layouts = SheetLayouts.Group(engine.Sheets);

            for (int i = 0; i < layouts.Count; i++)
            {
                for (int j = i + 1; j < layouts.Count; j++)
                {
                    Assert.False(SheetLayouts.SameLayout(layouts[i].Representative, layouts[j].Representative),
                        $"layouts {i} and {j} are identical and should have been merged");
                }
            }
        }

        // Order within a sheet's part list is not meaningful - the packer has no reason to emit a
        // fixed order - so two sheets holding the same parts in the same places must match whatever
        // order they are listed in.
        [Fact]
        public void ListOrderWithinASheetDoesNotSplitALayout()
        {
            var geometry = PartGeometry.FromRectangle(10, 18);
            var part = TestShapes.PlainPart(1, 1, "PLT-1", 10, 18);

            var a = new Sheet(SheetWidth, SheetLength);
            a.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = geometry, X = 0, Y = 0 });
            a.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = geometry, X = 20, Y = 0 });

            var b = new Sheet(SheetWidth, SheetLength);
            b.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = geometry, X = 20, Y = 0 });
            b.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = geometry, X = 0, Y = 0 });

            Assert.True(SheetLayouts.SameLayout(a, b));
            Assert.Single(SheetLayouts.Group(new List<Sheet> { a, b }));
        }

        [Fact]
        public void ADifferentPositionIsADifferentLayout()
        {
            var geometry = PartGeometry.FromRectangle(10, 18);
            var part = TestShapes.PlainPart(1, 1, "PLT-1", 10, 18);

            var a = new Sheet(SheetWidth, SheetLength);
            a.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = geometry, X = 0, Y = 0 });

            var b = new Sheet(SheetWidth, SheetLength);
            b.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = geometry, X = 0, Y = 5 });

            Assert.False(SheetLayouts.SameLayout(a, b));
            Assert.Equal(2, SheetLayouts.Group(new List<Sheet> { a, b }).Count);
        }

        // Same box, different outline. The export writes the real contour, so two sheets that only
        // agree on bounding boxes are not one file.
        [Fact]
        public void SameBoxButADifferentOutlineIsADifferentLayout()
        {
            var rectangle = PartGeometry.FromRectangle(12, 8);
            var trapezoid = shapes.Geometry("layout_trapezoid", TestShapes.Trapezoid);
            var part = TestShapes.PlainPart(1, 1, "PLT-1", 12, 8);

            Assert.Equal(rectangle.Width, trapezoid.Width);
            Assert.Equal(rectangle.Length, trapezoid.Length);

            var a = new Sheet(SheetWidth, SheetLength);
            a.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = rectangle, X = 0, Y = 0 });

            var b = new Sheet(SheetWidth, SheetLength);
            b.NestedParts.Add(new PlacedPart { SourcePart = part, Geometry = trapezoid, X = 0, Y = 0 });

            Assert.False(SheetLayouts.SameLayout(a, b));
        }

        [Fact]
        public void NoSheetsMeansNoLayouts()
        {
            Assert.Empty(SheetLayouts.Group(new List<Sheet>()));
            Assert.Empty(SheetLayouts.Group(null));
        }

        // Grouping is read for the drawing and again for the export, so it has to give the same
        // answer both times or the multiplier on screen disagrees with the file names.
        [Fact]
        public void GroupingIsRepeatable()
        {
            var engine = Nest(new List<Part>
            {
                TestShapes.PlainPart(1, 150, "PLT-1", 10, 18),
                TestShapes.PlainPart(2, 30, "PLT-2", 22, 30)
            });

            var first = SheetLayouts.Group(engine.Sheets);
            var second = SheetLayouts.Group(engine.Sheets);

            Assert.Equal(first.Count, second.Count);

            for (int i = 0; i < first.Count; i++)
            {
                Assert.Same(first[i].Representative, second[i].Representative);
                Assert.Equal(first[i].Count, second[i].Count);
            }
        }
    }
}
