using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cutwright.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // The whole 2D engine on a BOM mixing concave DXF parts with plain rectangles.
    //
    // This is the pass that matters most. The packer is free to turn a cluster 90 degrees, and if
    // the rotation arithmetic in EmitPlacements is wrong the members of a cluster come apart in a
    // way no test of ClusterBuilder alone would notice.
    public sealed class SheetNestEngineTests : IDisposable
    {
        private const float SheetWidth = 60f;
        private const float SheetLength = 120f;
        private const float Spacing = 0.125f;

        private readonly TestShapes shapes = new();
        private readonly ITestOutputHelper output;

        public SheetNestEngineTests(ITestOutputHelper output) => this.output = output;

        public void Dispose() => shapes.Dispose();

        private List<Part> MixedBom(int multiplier = 1)
        {
            string l = shapes.Write("engine_l", TestShapes.LBracket);
            string u = shapes.Write("engine_u", TestShapes.UChannel);
            string t = shapes.Write("engine_t", TestShapes.Trapezoid);

            return new List<Part>
            {
                shapes.DxfPart(1, 7 * multiplier, "BRK-1", l, 12, 12),
                shapes.DxfPart(2, 5 * multiplier, "CHN-1", u, 12, 8),
                shapes.DxfPart(3, 6 * multiplier, "TPR-1", t, 12, 8),
                TestShapes.PlainPart(4, 9 * multiplier, "PLT-1", 10, 18),
                TestShapes.PlainPart(5, 3 * multiplier, "PLT-2", 22, 30)
            };
        }

        [Fact]
        public void EveryPartIsPlacedAndCountsSurviveClustering()
        {
            var parts = MixedBom();
            var engine = new SheetNestEngine();
            engine.Nest(parts, SheetWidth, SheetLength, Spacing, 0f, MaterialPolicy.SheetMetal);

            int placed = engine.Sheets.Sum(s => s.NestedParts.Count);
            Assert.Equal(parts.Sum(p => p.quantity), placed);
            Assert.Empty(engine.UnnestedParts);

            // A cluster's members can come from different BOM rows, so this is where a bad
            // distribution would show up as a part quietly gained or lost.
            foreach (var part in parts)
            {
                int count = engine.Sheets.Sum(s => s.NestedParts.Count(p => ReferenceEquals(p.SourcePart, part)));
                Assert.Equal(part.quantity, count);
            }
        }

        [Fact]
        public void NothingOverlapsAndSpacingSurvivesToTheFinishedNest()
        {
            var engine = new SheetNestEngine();
            engine.Nest(MixedBom(), SheetWidth, SheetLength, Spacing, 0f, MaterialPolicy.SheetMetal);

            Assert.NotEmpty(engine.Sheets);

            foreach (var sheet in engine.Sheets)
            {
                var outlines = sheet.NestedParts.Select(p => p.ToWorld().Outer).ToList();
                var (closest, crossings) = OutlineChecks.Worst(outlines);

                output.WriteLine($"sheet {sheet.SheetID}: {outlines.Count} parts, closest pair {closest:0.####}in");

                Assert.Equal(0, crossings);
                Assert.True(closest >= Spacing - 1e-6,
                    $"sheet {sheet.SheetID}: closest pair {closest:0.#####} does not meet the {Spacing:0.###} spacing");

                bool onSheet = outlines.All(o => o.All(p =>
                    p.X >= -1e-6 && p.Y >= -1e-6 &&
                    p.X <= sheet.SheetWidth + 1e-6 && p.Y <= sheet.SheetLength + 1e-6));

                Assert.True(onSheet, $"sheet {sheet.SheetID} has a part hanging off the sheet");
            }
        }

        // A member sitting at 90 or 270 degrees can only have come from the packer rotating its
        // whole unit, so this confirms the rotated branch of EmitPlacements was actually taken and
        // the checks above therefore covered it.
        [Fact]
        public void PackerRotatesAtLeastOneUnit()
        {
            var engine = new SheetNestEngine();
            engine.Nest(MixedBom(), SheetWidth, SheetLength, Spacing, 0f, MaterialPolicy.SheetMetal);

            bool rotated = engine.Sheets
                .SelectMany(s => s.NestedParts)
                .Any(p => Math.Abs(p.RotationDegrees % 180.0) > 1e-9);

            Assert.True(rotated, "no unit was rotated, so the rotated placement path went untested");
        }

        [Fact]
        public void InterleavingNeverCostsMoreSheetsThanNotInterleaving()
        {
            // Scaled up so the difference lands on sheet count rather than just leftover space.
            var parts = MixedBom(multiplier: 8);

            var off = new MaterialPolicy
            {
                Name = "Sheet / Plate (no interleaving)",
                AllowRotation = true,
                CutMethod = CutMethod.FreePlacement,
                MinimumPartSpacing = 0.0,
                AllowInterleaving = false
            };

            var without = new SheetNestEngine();
            without.Nest(parts, SheetWidth, SheetLength, Spacing, 0f, off);

            var with = new SheetNestEngine();
            with.Nest(parts, SheetWidth, SheetLength, Spacing, 0f, MaterialPolicy.SheetMetal);

            output.WriteLine($"{parts.Sum(p => p.quantity)} parts:  " +
                             $"off = {without.SheetCount} sheets @ {without.MaterialUtilizationPercent:0.#}%,  " +
                             $"on = {with.SheetCount} sheets @ {with.MaterialUtilizationPercent:0.#}%");

            Assert.True(with.SheetCount <= without.SheetCount);
            Assert.True(with.MaterialUtilizationPercent >= without.MaterialUtilizationPercent);
        }

        [Fact]
        public void ExportWritesOneDxfPerSheet()
        {
            var engine = new SheetNestEngine();
            engine.Nest(MixedBom(), SheetWidth, SheetLength, Spacing, 0f, MaterialPolicy.SheetMetal);

            string outDir = shapes.NewSubdirectory("export");
            var writer = new DXFWriter();

            for (int i = 0; i < engine.Sheets.Count; i++)
                writer.WriteDXF(engine.Sheets[i], Path.Combine(outDir, $"sheet_{i + 1}.dxf"));

            Assert.Equal(engine.Sheets.Count, Directory.GetFiles(outDir, "*.dxf").Length);
        }

        // Gussets meet along their shared hypotenuse on purpose - a common-line cut, so zero gap
        // is correct. What must not happen is one crossing the other, which is what an inexact
        // 180-degree rotation used to cause.
        [Fact]
        public void PairedGussetsTouchButDoNotCross()
        {
            string path = shapes.Write("engine_gusset", TestShapes.RightTriangle);
            var parts = new List<Part> { shapes.DxfPart(1, 8, "GUS-1", path, 12, 8) };

            var engine = new SheetNestEngine();
            engine.Nest(parts, SheetWidth, SheetLength, 0f, 0f, MaterialPolicy.SheetMetal);

            Assert.Equal(8, engine.Sheets.Sum(s => s.NestedParts.Count));
            Assert.Contains(engine.Warnings, w => w.Contains("paired into"));

            foreach (var sheet in engine.Sheets)
            {
                var outlines = sheet.NestedParts.Select(p => p.ToWorld().Outer).ToList();
                var (_, crossings) = OutlineChecks.Worst(outlines);
                Assert.Equal(0, crossings);
            }
        }

        // Guillotine materials cannot separate an interleaved cluster with cuts that span the full
        // piece, so the search must not run for them at all.
        [Fact]
        public void ShearMaterialsDoNotInterleave()
        {
            Assert.False(MaterialPolicy.ExpandedMetal.AllowInterleaving);
            Assert.False(MaterialPolicy.WireMesh.AllowInterleaving);

            var engine = new SheetNestEngine();
            engine.Nest(MixedBom(), SheetWidth, SheetLength, 0f, 0f, MaterialPolicy.WireMesh);

            Assert.DoesNotContain(engine.Warnings, w => w.Contains("interleaved"));
        }
    }
}
