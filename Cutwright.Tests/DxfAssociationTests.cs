using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using netDxf;
using netDxf.Entities;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // Matching BOM lines to their DXF flat patterns.
    //
    // A part that finds no DXF still nests, as a rectangle from its BOM dimensions, which changes
    // the sheet count. So both halves matter: finding the file when it exists, and saying so when
    // it does not. Driven through Parse rather than against the matching helpers directly, because
    // the searching and the part list are built in the same pass.
    public sealed class DxfAssociationTests : IDisposable
    {
        private readonly string root;

        public DxfAssociationTests()
        {
            root = Path.Combine(Path.GetTempPath(), "Cutwright.Tests.Dxf", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leaked temp directory is not worth failing a test over.
            }
        }

        private string WriteBom(string jobFolder, params (string PartNumber, string Description)[] lines)
        {
            string folder = Path.Combine(root, jobFolder);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{jobFolder} BOM.xlsx");

            int item = 0;
            return TestBom.Write(path, 1, lines
                .Select(l => TestBom.Line(++item, 2, l.Description, l.PartNumber, 12f, 8f))
                .ToArray());
        }

        private string WriteDxf(string relativeFolder, string fileName)
        {
            string folder = Path.Combine(root, relativeFolder);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, fileName);

            var document = new DxfDocument();
            document.DrawingVariables.InsUnits = netDxf.Units.DrawingUnits.Inches;
            var corners = new[] { (0.0, 0.0), (12.0, 0.0), (12.0, 8.0), (0.0, 8.0) };

            for (int i = 0; i < corners.Length; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % corners.Length];
                document.Entities.Add(new Line(new Vector2(a.Item1, a.Item2), new Vector2(b.Item1, b.Item2)));
            }

            document.Save(path);
            return path;
        }

        private static Part? PartNumbered(FileParser parser, string partNumber) =>
            parser.PNestList.SelectMany(l => l.Parts)
                .FirstOrDefault(p => p.PartNumber == partNumber);

        [Fact]
        public void ADxfInADeeplyNestedSubfolderIsFound()
        {
            string bom = WriteBom("job-nested", ("X102-001", "Sheet 11GA HR"));
            WriteDxf(@"job-nested\Programs\Laser\DXF", "0p25 Flat pattern - X102-001.DXF");

            var parser = new FileParser();
            parser.Parse(bom);

            var part = PartNumbered(parser, "X102-001");
            Assert.NotNull(part);
            Assert.True(part!.hasDXF, "A DXF several folders below the BOM should still be found.");
        }

        // The search must stay inside the job folder: one job picking up another job's geometry
        // would nest the wrong shape while looking entirely successful.
        [Fact]
        public void ADxfAboveTheBomFolderIsNotUsed()
        {
            string bom = WriteBom("job-inside", ("Y101-001", "Sheet 7GA HR"));
            WriteDxf(".", "0p25 Flat pattern - Y101-001.DXF");

            var parser = new FileParser();
            parser.Parse(bom);

            var part = PartNumbered(parser, "Y101-001");
            Assert.NotNull(part);
            Assert.False(part!.hasDXF, "A DXF outside the job folder must never be associated.");
        }

        [Fact]
        public void PartsWithNoDxfAreReported()
        {
            string bom = WriteBom("job-none", ("A-1", "Sheet 7GA HR"), ("A-2", "Sheet 7GA HR"));
            WriteDxf(@"job-none\DXF", "Flat pattern - A-1.DXF");

            var parser = new FileParser();
            parser.Parse(bom);

            Assert.Contains(parser.Warnings, w => w.Contains("nest as rectangles", StringComparison.Ordinal));
        }

        // "8" is a substring of "85", so a loose match would silently pick the wrong drawing.
        [Fact]
        public void APartNumberPrefersTheFileWhereItStandsAlone()
        {
            string bom = WriteBom("job-prefix", ("8", "Sheet 11GA HR"));
            WriteDxf(@"job-prefix\DXF", "11GA Flat pattern - Item 85.DXF");
            string wanted = WriteDxf(@"job-prefix\DXF", "11GA Flat pattern - Item 8.DXF");

            var parser = new FileParser();
            parser.Parse(bom);

            var part = PartNumbered(parser, "8");
            Assert.NotNull(part);
            Assert.True(part!.hasDXF);
            Assert.Equal(wanted, part.DXFPath);
        }

        // The same filename in two places is one part copied, not a choice - jobs routinely keep a
        // set under DXF and another under Programs. The shallower copy wins, every run.
        [Fact]
        public void DuplicateCopiesOfOneDrawingResolveToTheShallowestAndDoNotWarn()
        {
            string bom = WriteBom("job-dupes", ("X103-004", "Plate 1/2\" HR"));
            string shallow = WriteDxf(@"job-dupes\DXF", "0p5 Flat pattern - X103-004.DXF");
            WriteDxf(@"job-dupes\Programs\Laser\DXF", "0p5 Flat pattern - X103-004.DXF");

            var parser = new FileParser();
            parser.Parse(bom);

            var part = PartNumbered(parser, "X103-004");
            Assert.NotNull(part);
            Assert.Equal(shallow, part!.DXFPath);
            Assert.DoesNotContain(parser.Warnings, w => w.Contains("different files", StringComparison.Ordinal));
        }

        // Two differently named drawings for one part number is a real ambiguity and must be said.
        [Fact]
        public void TwoDifferentDrawingsForOnePartNumberAreReported()
        {
            string bom = WriteBom("job-ambiguous", ("X-9", "Sheet 7GA HR"));
            WriteDxf(@"job-ambiguous\DXF", "7GA Flat pattern - X-9.DXF");
            WriteDxf(@"job-ambiguous\DXF", "7GA Revised - X-9.DXF");

            var parser = new FileParser();
            parser.Parse(bom);

            var part = PartNumbered(parser, "X-9");
            Assert.NotNull(part);
            Assert.True(part!.hasDXF);
            Assert.Contains(parser.Warnings, w => w.Contains("different files", StringComparison.Ordinal));
        }

        [Fact]
        public void AFolderWithNoDxfFilesAtAllIsNotAnError()
        {
            string bom = WriteBom("job-empty", ("Q-1", "Sheet 7GA HR"));

            var parser = new FileParser();
            parser.Parse(bom);

            Assert.Empty(parser.Errors);
            Assert.NotEmpty(parser.PNestList);
        }
    }
}
