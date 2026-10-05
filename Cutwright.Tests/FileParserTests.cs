using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The parser's failure paths. These exist because the parser used to raise its own
    // MessageBox: every one of these cases would have blocked on a modal dialog instead of
    // returning, which made a batch run over a folder of real BOMs impossible and meant a
    // malformed BOM in an unattended run hung forever with nothing written to a log.
    public sealed class FileParserTests : IDisposable
    {
        private readonly string directory;

        public FileParserTests()
        {
            directory = Path.Combine(Path.GetTempPath(), "Cutwright.Tests.Parser", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leaked temp directory is not worth failing a test over.
            }
        }

        [Fact]
        public void AnUnreadableFileIsReportedRatherThanOpeningADialog()
        {
            string path = Path.Combine(directory, "not-really-a-workbook.xlsx");
            File.WriteAllText(path, "This is not a spreadsheet.");

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Single(parser.Errors);
            Assert.Contains("not-really-a-workbook.xlsx", parser.Errors[0], StringComparison.Ordinal);
            Assert.Empty(parser.PNestList);
            Assert.Empty(parser.TNestList);
        }

        [Fact]
        public void APartsTableWithNoDescriptionColumnIsReported()
        {
            string path = TestBom.Write(Path.Combine(directory, "no-description.xlsx"), 1,
                TestBom.Line(1, 4, "Plate 1/4 HR", "P-1", 24f, 12f));

            using (var workbook = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(workbook, BomWorkbook.PartsTable, out IXLTable table);
                table.Field(BomWorkbook.ColDescription).Name = "Material";
                workbook.Save();
            }

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Single(parser.Errors);
            Assert.Contains("Description", parser.Errors[0], StringComparison.Ordinal);
            Assert.Empty(parser.PNestList);
        }

        [Fact]
        public void AWorkbookWithoutTheCutwrightMarkerIsRefused()
        {
            string path = Path.Combine(directory, "plain.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Sheet1");
                sheet.Cell(1, 1).Value = "ITEM";
                sheet.Cell(1, 2).Value = "QTY";
                sheet.Cell(2, 1).Value = 1;
                sheet.Cell(2, 2).Value = 4;
                workbook.SaveAs(path);
            }

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Single(parser.Errors);
            Assert.Contains("not a Cutwright bill of materials", parser.Errors[0], StringComparison.Ordinal);
            Assert.Empty(parser.PNestList);
        }

        [Fact]
        public void AFileFromANewerFormatIsRefused()
        {
            string path = TestBom.Write(Path.Combine(directory, "future.xlsx"), 1,
                TestBom.Line(1, 4, "Plate 1/4 HR", "P-1", 24f, 12f));

            using (var workbook = new XLWorkbook(path))
            {
                workbook.DefinedNames.Delete(BomWorkbook.FormatName);
                workbook.DefinedNames.Add(BomWorkbook.FormatName, "3");
                workbook.Save();
            }

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Single(parser.Errors);
            Assert.Contains("newer version", parser.Errors[0], StringComparison.Ordinal);
        }

        [Fact]
        public void AnEmptyPathIsNotAnError()
        {
            var parser = new FileParser();
            parser.Parse("");

            Assert.Empty(parser.Errors);
        }

        // Errors describe one Parse call, not every call the instance has ever made, so a
        // successful parse after a failed one must not still look broken.
        [Fact]
        public void ErrorsAreClearedBetweenParses()
        {
            string bad = Path.Combine(directory, "bad.xlsx");
            File.WriteAllText(bad, "junk");

            var parser = new FileParser();
            parser.Parse(bad);
            Assert.NotEmpty(parser.Errors);

            parser.Parse("");
            Assert.Empty(parser.Errors);
        }

        // Units is the job block's unit count and Per unit is what the file scales by it - both are
        // what a units change on an already-loaded BOM needs to scale from.
        [Fact]
        public void UnitsAndPerUnitQuantityAreReadFromTheTemplate()
        {
            string path = TestBom.Write(Path.Combine(directory, "units.xlsx"), 3,
                TestBom.Line(1, 4, "Plate 1/4 HR", "P-1", 24f, 12f));

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Equal(3, parser.Units);

            var part = Assert.Single(parser.PNestList.SelectMany(n => n.Parts));
            Assert.Equal(4, part.PerUnitQuantity);
            Assert.Equal(12, part.quantity);
        }

        // A blank unit count must not fall back to 0 (a units change would then zero every quantity
        // in the job), so it reads as one unit and the quantity is the per-unit figure.
        [Fact]
        public void ABlankUnitCountFallsBackToOneUnit()
        {
            string path = TestBom.Write(Path.Combine(directory, "no-units.xlsx"), 5,
                TestBom.Line(1, 12, "Plate 1/4 HR", "P-1", 24f, 12f));
            TestBom.SetJobCell(path, BomWorkbook.JobUnits, null);

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Equal(1, parser.Units);

            var part = Assert.Single(parser.PNestList.SelectMany(n => n.Parts));
            Assert.Equal(12, part.PerUnitQuantity);
            Assert.Equal(12, part.quantity);
        }

        // The nesting engine is proven to 52,000 parts, so a few hundred rows was never a safe
        // ceiling for the reader either.
        [Fact]
        public void ABomWithManyRowsStillParses()
        {
            var parts = Enumerable.Range(1, 700)
                .Select(i => TestBom.Line(i, 1, "Plate 1/4 HR", "P-" + i, 24f, 12f))
                .ToArray();
            string path = TestBom.Write(Path.Combine(directory, "large.xlsx"), 1, parts);

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Equal(700, parser.PNestList.SelectMany(n => n.Parts).Count());
        }

        // A row with a description but no length is a purchased item (hardware): not nested, and
        // not an error.
        [Fact]
        public void ARowWithNoLengthIsNotAPartToNest()
        {
            string path = TestBom.Write(Path.Combine(directory, "hardware.xlsx"), 2,
                TestBom.Line(1, 2, "Plate 1/4 HR", "P-1", 24f, 12f),
                TestBom.Line(2, 8, "1/2-13 x 1 Hex Bolt", "HW-1", 0f));

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Equal("P-1", Assert.Single(parser.PNestList.SelectMany(n => n.Parts)).PartNumber);
            Assert.Empty(parser.TNestList);
        }

        // Rows are laid out by detail/drawing order, not by material, so the same material's rows
        // are routinely non-adjacent (here, a tube row, an angle row, then the same tube description
        // again). Grouping by adjacency would split the tube into two separate 1D nest groups that
        // nest independently and cost more stock than the combined cut list needed. BillOfMaterials
        // sorts on write, so this edits the file into that order afterwards.
        [Fact]
        public void NonAdjacentSameDescriptionTubeRowsLandInOneGroup()
        {
            string path = TestBom.Write(Path.Combine(directory, "interleaved-tube.xlsx"), 1,
                TestBom.Line(1, 1, "SQ Tube 2 x 2 x 11GA HR", "P-1", 50f),
                TestBom.Line(2, 1, "Angle 2 x 2 x 1/4 HR", "P-2", 30f),
                TestBom.Line(3, 1, "SQ Tube 2 x 2 x 11GA HR", "P-3", 10f));

            using (var workbook = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(workbook, BomWorkbook.PartsTable, out IXLTable table);
                var columns = BomWorkbook.Columns(table);
                int first = table.DataRange!.FirstRow().RowNumber();
                var sheet = table.Worksheet;

                // Whatever order the writer chose, put the angle between the two tube rows.
                string[] order = { "SQ Tube 2 x 2 x 11GA HR", "Angle 2 x 2 x 1/4 HR", "SQ Tube 2 x 2 x 11GA HR" };
                string[] partNumbers = { "P-1", "P-2", "P-3" };
                double[] lengths = { 50, 30, 10 };
                for (int i = 0; i < 3; i++)
                {
                    sheet.Cell(first + i, columns[BomWorkbook.ColDescription]).Value = order[i];
                    sheet.Cell(first + i, columns[BomWorkbook.ColPartNumber]).Value = partNumbers[i];
                    sheet.Cell(first + i, columns[BomWorkbook.ColLength]).Value = lengths[i];
                }

                workbook.Save();
            }

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Equal(2, parser.TNestList.Count);

            var tube = parser.TNestList.Single(n => n.Description == "SQ Tube 2 x 2 x 11GA HR");
            Assert.Equal(new[] { 50f, 10f }, tube.Parts.Select(p => p.length).ToArray());
        }
    }
}
