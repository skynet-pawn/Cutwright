using System;
using System.Collections.Generic;
using System.Linq;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // SwBuildPlan and SwProfileMatcher - everything about the SolidWorks parts export that can be
    // decided without SolidWorks. The profile libraries are stood in for by the real names: the shop
    // library's file names as they sit on O:, and ANSI configuration names as SolidWorks 2024
    // reported them.
    public sealed class SwBuildPlanTests
    {
        private static Part Row(int det, string description, string partNumber, float length, float width = 0f) =>
            new(det, 1, description, partNumber, width, length, width * length);

        // The DET section of a sample rack job as FileParser reads it: rows
        // with a length only, so the placard and the stacking cap never arrive.
        private static List<Part> SampleRack() => new()
        {
            Row(1, "SQ Tube 2 x 2 x 11 GA HR", "9", 67),
            Row(2, "SQ Tube 2 x 2 x 11 GA HR", "2", 33),
            Row(3, "Rec Tube 8 x 3 x 11 GA HR", "3", 71),
            Row(4, "Rec Tube 8 x 3 x 11 GA HR", "4", 16),
            Row(5, "Rec Tube 8 x 3 x 11 GA HR", "5", 19.5f),
            Row(6, "Rec Tube 3 x 2 x 11 GA HR", "6", 16),
            Row(7, "Rec Tube 3 x 2 x 11 GA HR", "7", 17.5f),
            Row(8, "Rec Tube 2 x 1 x 11 GA", "8", 67),
            Row(9, "Rec Tube 2 x 1 x 11 GA", "10", 27),
            Row(10, "L-Angle 1-1/2 x 1-1/2 x 1/8 HR", "15", 9.75f),
            Row(11, "Sheet 7 GA HR", "11", 67, 10),
            Row(12, "Sheet 11 GA HR", "14a", 68, 24),
            Row(13, "Sheet 11 GA HR", "14b", 68, 22),
            Row(14, "Sheet 14 GA HR", "12", 14, 8),
        };

        private static readonly (string Folder, StockForm Form, int Dims, string[] Names)[] ShopNames =
        {
            ("SQ Tube", StockForm.SquareTube, 2, new[]
            {
                "0.75 x 0.75 x 14GA", "0.75 x 0.75 x 16GA", "1 x 1 x 11GA", "1 x 1 x 14GA", "1 x 1 x 16GA",
                "1.5 x 1.5 x 11GA", "1.5 x 1.5 x 14GA", "1.5 x 1.5 x 16GA", "2 x 2 x 11GA", "2 x 2 x 14GA",
                "2 x 2 x 16GA", "2 x 2 x 7GA", "2.5 x 2.5 x 11GA", "2.5 x 2.5 x 14GA", "2.5 x 2.5 x 7GA",
                "3 x 3 x 0.25", "3 x 3 x 11GA", "3 x 3 x 7GA"
            }),
            ("Rect Tube", StockForm.RectangularTube, 2, new[]
            {
                "2 x 1 x 11GA", "2 x 1 x 14GA", "2 x 1 x 16GA", "2.5 x 1.5 x 11GA", "3 x 1 x 11GA", "3 x 1 x 14GA",
                "3 x 2 x 0.1875", "3 x 2 x 11GA", "8 x 3 x 0.1875", "8 x 3 x 0.25", "8 x 3 x 11GA"
            }),
            ("Round Tube", StockForm.RoundTube, 1, new[]
            {
                "0.75 x 14GA", "1 x 11GA", "1 x 14GA", "1 x 16GA", "1.5 x 0.1875", "1.5 x 11GA", "2 x 0.125", "2 x 0.25"
            })
        };

        private static List<SwProfileEntry> Shop() =>
            ShopNames.SelectMany(f => f.Names.Select(n =>
            {
                var (section, wall) = SwProfileLibrary.ParseShopName(n, f.Form, f.Dims)!.Value;
                return new SwProfileEntry(f.Form, section, wall, null, null, false, $@"D:\{f.Folder}\{n}.SLDLFP", null);
            })).ToList();

        private static List<SwProfileEntry> Ansi() =>
            SwProfileLibrary.ParseAnsi(@"C:\ansi\tube square.sldlfp", new[]
                { "TS2x2x0.1875", "TS2x2x0.25", "TS2x2x0.3125", "TS3x3x0.1875", "TS4x4x0.25" })
            .Concat(SwProfileLibrary.ParseAnsi(@"C:\ansi\tube rectangular.sldlfp", new[]
                { "TR3x2x0.1875", "TR3x2x0.25", "TR4x2x0.1875", "TR4x2x0.25" }))
            .Concat(SwProfileLibrary.ParseAnsi(@"C:\ansi\l angle.sldlfp", new[]
                { "L1.5x1.5x0.1875", "L1.5x1.5x0.25", "L2x2x0.125", "L2x2x0.1875", "L3x2x0.25" }))
            .Concat(SwProfileLibrary.ParseAnsi(@"C:\ansi\pipe standard s40.sldlfp", new[]
                { "Default", "PIPE 0.5 SCH 40", "PIPE 1 SCH 40", "PIPE 1.5 SCH 40" }))
            .Concat(SwProfileLibrary.ParseAnsi(@"C:\ansi\c channel.sldlfp", new[] { "C3x4.1", "C4x5.4" }))
            .Concat(SwProfileLibrary.ParseAnsi(@"C:\ansi\al tube square.sldlfp", new[]
                { "Al TUBE 2 SQR x 0.125 WALL", "Al TUBE 2 SQR x 0.25 WALL" }))
            .ToList();

        private static List<SwPartJob> Plan(IEnumerable<Part> parts)
        {
            var jobs = SwBuildPlan.Create(parts);
            SwProfileMatcher.Resolve(jobs, Shop(), Ansi());
            return jobs;
        }

        private static SwPartJob Only(params Part[] parts) => Assert.Single(Plan(parts));

        [Fact]
        public void SampleRackBuildsEveryPartPlusTheAngle()
        {
            var jobs = Plan(SampleRack());

            Assert.Equal(
                new[] { "9", "2", "3", "4", "5", "6", "7", "8", "10", "15", "11", "14a", "14b", "12" },
                jobs.Select(j => j.FileName));
            Assert.All(jobs, j => Assert.True(j.CanBuild, $"{j.FileName}: {j.Problem}"));

            Assert.All(jobs.Where(j => j.Kind == SwPartKind.Stick && j.Shape!.Form != StockForm.Angle),
                j => Assert.Equal(SwProfileSource.Shop, j.Profile!.Source));

            // 1/8 wall at 1-1/2 legs is in neither library - ANSI starts that size at 3/16.
            SwPartJob angle = jobs.Single(j => j.FileName == "15");
            Assert.Equal(SwProfileSource.Sketch, angle.Profile!.Source);
            Assert.Equal(0.125, angle.Thickness!.Value, 4);
        }

        [Fact]
        public void SampleRackSheetsGetRealGaugeThickness()
        {
            var jobs = Plan(SampleRack()).Where(j => j.Kind == SwPartKind.Sheet).ToDictionary(j => j.FileName);

            Assert.Equal(0.1793, jobs["11"].Thickness!.Value, 4);
            Assert.Equal(0.1196, jobs["14a"].Thickness!.Value, 4);
            Assert.Equal(0.0747, jobs["12"].Thickness!.Value, 4); // StockThickness would have said 11 GA
            Assert.Equal(67, jobs["11"].Length);
            Assert.Equal(10, jobs["11"].Width);
        }

        [Fact]
        public void ShopProfileIsPickedOnExactSizeRegardlessOfWordOrder()
        {
            var job = Only(Row(1, "Rect Tube 1 x 2 x 11 GA HR", "A", 10));

            Assert.Equal(SwProfileSource.Shop, job.Profile!.Source);
            Assert.EndsWith(@"Rect Tube\2 x 1 x 11GA.SLDLFP", job.Profile.Path);
        }

        [Fact]
        public void AnsiIsUsedWithinTenThou()
        {
            // 7 GA is 0.1793 - 0.0082 from ANSI's 3/16. Not in the shop library at 4 x 2.
            var job = Only(Row(1, "Rec Tube 4 x 2 x 7 GA HR", "A", 10));

            Assert.Equal(SwProfileSource.Ansi, job.Profile!.Source);
            Assert.Equal("TR4x2x0.1875", job.Profile.Configuration);
            Assert.Equal(0.1875, job.Profile.Wall);
        }

        [Fact]
        public void AnsiIsNotUsedPastTenThou()
        {
            // 11 GA (0.1196) is 0.068 off the nearest 4 x 2 wall.
            var job = Only(Row(1, "Rec Tube 4 x 2 x 11 GA HR", "A", 10));

            Assert.Equal(SwProfileSource.Sketch, job.Profile!.Source);
        }

        [Fact]
        public void ShopLibraryWinsOverAnsi()
        {
            // 8 x 3 x 3/16 is in both - the shop's own file is the one used.
            var job = Only(Row(1, "Rec Tube 8 x 3 x 3/16 HR", "A", 10));

            Assert.Equal(SwProfileSource.Shop, job.Profile!.Source);
        }

        [Fact]
        public void AnExactAnsiWallCarriesNoWallNote()
        {
            var job = Only(Row(1, "SQ Tube 2 x 2 x 1/4 HR", "A", 10));

            Assert.Equal("TS2x2x0.25", job.Profile!.Configuration);
            Assert.Null(job.Profile.Wall);
        }

        [Fact]
        public void AluminumOnlyMatchesAluminumProfiles()
        {
            var steel = Only(Row(1, "SQ Tube 2 x 2 x 1/8 HR", "A", 10));
            var alum = Only(Row(1, "SQ Tube 2 x 2 x 1/8 Alum", "A", 10));

            Assert.Equal(SwProfileSource.Sketch, steel.Profile!.Source);
            Assert.Equal(SwProfileSource.Ansi, alum.Profile!.Source);
            Assert.Equal("Al TUBE 2 SQR x 0.125 WALL", alum.Profile.Configuration);
            Assert.Equal(SwMaterial.Aluminum, alum.Material);
        }

        [Fact]
        public void PipeAndChannelComeFromAnsiOrNotAtAll()
        {
            var pipe = Only(Row(1, "Pipe 1\" Sch. 40", "A", 10));
            var channel = Only(Row(1, "C Channel C3 x 4.1 HR", "A", 10));
            var missing = Only(Row(1, "Pipe 3/4\" Sch. 40", "A", 10));

            Assert.Equal("PIPE 1 SCH 40", pipe.Profile!.Configuration);
            Assert.Equal("C3x4.1", channel.Profile!.Configuration);
            Assert.Null(missing.Profile);
            Assert.False(missing.CanBuild);
            Assert.False(missing.Include);
        }

        [Fact]
        public void BarsAndRodAreSketched()
        {
            Assert.Equal(SwProfileSource.Sketch, Only(Row(1, "FB 1/4 x 2 HR", "A", 10)).Profile!.Source);
            Assert.Equal(SwProfileSource.Sketch, Only(Row(1, "Rod 3/4\" HR", "A", 10)).Profile!.Source);
        }

        [Theory]
        [InlineData("Sheet 11 GA HR", "Steel", 0.1196)]
        [InlineData("Sheet 11 GA SS", "Stainless", 0.1250)]
        [InlineData("Sheet 11 GA Stainless", "Stainless", 0.1250)]
        [InlineData("Alum Sheet 11 GA", "Aluminum", 0.0907)]
        [InlineData("Sheet 11 GA Aluminum", "Aluminum", 0.0907)]
        [InlineData("Plate 3/8\" HR", "Steel", 0.375)]
        [InlineData("Plate 1/4 HR (96x48)", "Steel", 0.25)]
        [InlineData("Plate 6061 Alum 1/4", "Aluminum", 0.25)]
        [InlineData("Sheet 304 SS 16 GA", "Stainless", 0.0625)]
        [InlineData("Plate 1/4 x 4 x 5 HR", "Steel", 0.25)]
        public void SheetThicknessAndMaterialFollowTheDescription(string description, string material, double thickness)
        {
            var job = Only(Row(1, description, "A", 20, 10));

            Assert.True(job.CanBuild, job.Problem);
            Assert.Equal(Enum.Parse<SwMaterial>(material), job.Material);
            Assert.Equal(thickness, job.Thickness!.Value, 4);
        }

        [Fact]
        public void TubeGaugeWallFollowsTheMaterial()
        {
            // CalloutTranslator reads every gauge as steel.
            var job = Only(Row(1, "SQ Tube 2 x 2 x 11 GA SS", "A", 10));

            Assert.Equal(0.1250, job.Thickness!.Value, 4);
        }

        [Fact]
        public void UnknownGaugeIsReportedNotGuessed()
        {
            var sheet = Only(Row(1, "Sheet 15 GA HR", "A", 20, 10));
            var tube = Only(Row(1, "SQ Tube 2 x 2 x 15 GA HR", "A", 10));

            Assert.Contains("15 GA", sheet.Problem);
            Assert.Contains("15 GA", tube.Problem);
        }

        [Fact]
        public void SamePartNumberIsBuiltOnce()
        {
            var jobs = Plan(new[]
            {
                Row(1, "Sheet 11 GA HR", "P-1", 20, 10),
                Row(2, "Sheet 11 GA HR", "P-1", 20, 10),
                Row(3, "Sheet 11 GA HR", "P-1", 30, 10),
            });

            var job = Assert.Single(jobs);
            Assert.Equal(1, job.Det);
            Assert.Contains(job.Notes, n => n.StartsWith("DET 2 is the same part"));
            Assert.Contains(job.Notes, n => n.StartsWith("DET 3 has the same part number but differs"));
        }

        [Fact]
        public void NoPartNumberMergesOnlyIdenticalRows()
        {
            var jobs = Plan(new[]
            {
                Row(4, "Sheet 11 GA HR", "", 20, 10),
                Row(5, "Sheet 11 GA HR", "", 20, 10),
                Row(6, "Sheet 11 GA HR", "", 30, 10),
            });

            Assert.Equal(new[] { "Item#4", "Item#6" }, jobs.Select(j => j.FileName));
        }

        [Fact]
        public void RepeatedOrMissingDetsFallBackToAnIndex()
        {
            var repeated = Plan(new[]
            {
                Row(3, "Sheet 11 GA HR", "", 20, 10),
                Row(3, "Sheet 7 GA HR", "", 20, 10),
                Row(7, "Sheet 14 GA HR", "PN-7", 20, 10),
            });
            var missing = Plan(new[]
            {
                Row(0, "Sheet 11 GA HR", "", 20, 10),
                Row(2, "Sheet 7 GA HR", "", 20, 10),
            });

            Assert.Equal(new[] { "Item#1", "Item#2", "PN-7" }, repeated.Select(j => j.FileName));
            Assert.Equal(new[] { "Item#1", "Item#2" }, missing.OrderBy(j => j.FileName).Select(j => j.FileName));
        }

        [Fact]
        public void IllegalCharactersBecomeDashesAndCollisionsAreFlagged()
        {
            var jobs = Plan(new[]
            {
                Row(1, "Sheet 11 GA HR", "A/B", 20, 10),
                Row(2, "Sheet 7 GA HR", "A:B", 20, 10),
            });

            Assert.Equal("A-B", jobs[0].FileName);
            Assert.True(jobs[0].CanBuild);
            Assert.Equal("same file name as DET 1", jobs[1].Problem);

            jobs[1].FileName = "A-B 2";
            SwBuildPlan.CheckFileNames(jobs);
            Assert.True(jobs[1].CanBuild);
        }

        [Fact]
        public void UnbuildableRowsAreExplainedAndLeftOut()
        {
            var noTemplate = Only(Row(1, "Sheet 1/4 Delrin", "A", 20, 10));
            var stickWithWidth = Only(Row(1, "SQ Tube 2 x 2 x 11 GA HR", "A", 20, 10));
            var sheetWithoutWidth = Only(Row(1, "Sheet 11 GA HR", "A", 20));
            var purchasedWithLength = Only(Row(1, "Cable Assy 36 in", "A", 36));

            Assert.Contains("Delrin", noTemplate.Problem);
            Assert.Equal("has a width, but the description is a stick part", stickWithWidth.Problem);
            Assert.StartsWith("no width", sheetWithoutWidth.Problem);
            Assert.Equal("description is not a recognized stick shape", purchasedWithLength.Problem);
            Assert.All(new[] { noTemplate, stickWithWidth, sheetWithoutWidth, purchasedWithLength },
                j => Assert.False(j.Include));
        }

        [Theory]
        [InlineData("Sheet 1/2 HDPE", "HDPE", 0.5)]
        [InlineData("HDPE 3/4\" (48x96)", "HDPE", 0.75)]
        [InlineData("UHMW Sheet 1/4", "UHMW", 0.25)]
        [InlineData("UHMW 3/8 x 12 x 24", "UHMW", 0.375)]
        public void PlasticSheetsAreBuiltAtTheirCalledOutThickness(string description, string material, double thickness)
        {
            var job = Only(Row(1, description, "A", 24, 12));

            Assert.True(job.CanBuild, job.Problem);
            Assert.Equal(Enum.Parse<SwMaterial>(material), job.Material);
            Assert.Equal(thickness, job.Thickness!.Value, 4);
        }

        [Fact]
        public void PlasticIsNotGauged()
        {
            var job = Only(Row(1, "Sheet 11 GA HDPE", "A", 24, 12));

            Assert.Equal("HDPE is not gauged - call out its thickness instead of 11 GA", job.Problem);
        }

        [Fact]
        public void PlasticSticksAreAlwaysSketched()
        {
            // 2 x 2 x 1/4 square tube is in ANSI, and would be used for steel.
            var tube = Only(Row(1, "SQ Tube 2 x 2 x 1/4 UHMW", "A", 10));
            var rod = Only(Row(1, "Rod 1\" HDPE", "A", 10));
            var bar = Only(Row(1, "FB 1/2 x 2 UHMW", "A", 10));

            Assert.All(new[] { tube, rod, bar }, j => Assert.Equal(SwProfileSource.Sketch, j.Profile!.Source));
            Assert.Equal(SwMaterial.UHMW, tube.Material);
            Assert.Equal(SwMaterial.HDPE, rod.Material);
        }

        [Fact]
        public void AnsiNamesParseIntoSections()
        {
            var ts = Assert.Single(SwProfileLibrary.ParseAnsi(@"C:\x\tube square.sldlfp", new[] { "TS2.5x2.5x0.1875" }));
            var al = Assert.Single(SwProfileLibrary.ParseAnsi(@"C:\x\al tube rectangular.sldlfp",
                new[] { "AL TUBE 0.63 x 0.38 RECT x 0.048 WALL" }));
            var pipe = Assert.Single(SwProfileLibrary.ParseAnsi(@"C:\x\al pipe structural.sldlfp",
                new[] { "Al PIPE STRUCTURAL 0.125  S80" }));

            Assert.Equal(new[] { 2.5, 2.5 }, ts.Section);
            Assert.Equal(0.1875, ts.Wall);
            Assert.Equal(new[] { 0.63, 0.38 }, al.Section);
            Assert.True(al.Aluminum);
            Assert.Equal(80, pipe.Schedule);
            Assert.Empty(SwProfileLibrary.ParseAnsi(@"C:\x\square hss.sldlfp", new[] { "CS 6 x 3.51" }));
        }
    }
}
