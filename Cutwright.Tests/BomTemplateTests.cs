using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // What the generated bill of materials (Cutwright format 2, see docs/bom-format-v2.md) actually
    // contains: named cells and tables rather than fixed addresses, no merged cells, and the unit
    // count reaching the sheet - the Units column is a formula pointing at it with every Total
    // multiplying by that, so a bill of materials built from a CSV once came out with every total
    // reading zero.
    public sealed class BomTemplateTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory;

        public BomTemplateTests(ITestOutputHelper output)
        {
            this.output = output;
            directory = Path.Combine(Path.GetTempPath(), "Cutwright.Tests", Guid.NewGuid().ToString("N"));
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
            }
        }

        private static BillOfMaterials Filled(int units = 35)
        {
            var bom = new BillOfMaterials
            {
                CustomerName = "Acme Trucking",
                JobEst = "12345",
                PrevJobEst = "N/A",
                Units = units,
                Description = "Sample Racks",
                DrawingNo = "D-100",
                EngName = "SD",
                CheckedBy = "KJ",
                RevisionNo = "2",
                Date = "2026-08-21"
            };

            bom.parts.Add(new Part(1, 2, "SQ Tube 2 x 2 x 11 GA", "P-1", 0f, 83.9375f, 0f));
            bom.parts.Add(new Part(2, 6, "Plate 1/4 HR", "P-2", 12f, 24f, 0f));
            bom.parts.Add(new Part(3, 4, "Sheet 11GA HR", "P-3", 8f, 16f, 0f));

            return bom;
        }

        private IXLWorksheet Write(BillOfMaterials bom, out XLWorkbook workbook)
        {
            string path = Path.Combine(directory, "bom.xlsx");
            bom.WriteToFile(path);

            Assert.Empty(bom.Errors);
            Assert.True(File.Exists(path), "no file was written");

            workbook = new XLWorkbook(path);
            return workbook.Worksheet(1);
        }

        private const int FirstPartRow = 10;

        private static readonly (string Name, string Expected)[] JobFields =
        {
            (BomWorkbook.JobCustomer, "Acme Trucking"),
            (BomWorkbook.JobEst, "12345"),
            (BomWorkbook.JobPrevEst, "N/A"),
            (BomWorkbook.JobUnits, "35"),
            (BomWorkbook.JobBy, "SD"),
            (BomWorkbook.JobDrawing, "D-100"),
            (BomWorkbook.JobCheckedBy, "KJ"),
            (BomWorkbook.JobDescription, "Sample Racks"),
            (BomWorkbook.JobDate, "2026-08-21"),
            (BomWorkbook.JobRevision, "2"),
        };

        [Fact]
        public void TheWorkbookIsMarkedAsACutwrightBomOfTheCurrentFormat()
        {
            Write(Filled(), out var workbook);

            using (workbook)
            {
                Assert.Equal(BomWorkbook.FormatVersion, BomWorkbook.FormatOf(workbook));
                Assert.Null(BomWorkbook.Refusal(workbook, "bom.xlsx"));
            }
        }

        [Fact]
        public void EveryJobFieldIsANamedCellHoldingItsValue()
        {
            Write(Filled(), out var workbook);

            using (workbook)
            {
                foreach (var (name, expected) in JobFields)
                {
                    IXLCell? cell = BomWorkbook.NamedCell(workbook, name);
                    Assert.True(cell is not null, $"{name} is not defined");
                    Assert.Equal(expected, BomWorkbook.Text(cell!));
                }
            }
        }

        // The point of the format: nothing depends on where a cell is, and merged cells make
        // sorting, filtering and inserting rows awkward.
        [Fact]
        public void NoSheetHasMergedCells()
        {
            Write(Filled(), out var workbook);

            using (workbook)
            {
                foreach (IXLWorksheet sheet in workbook.Worksheets)
                    Assert.Empty(sheet.MergedRanges);
            }
        }

        [Fact]
        public void ThePartsAreATableWithTheTemplateColumns()
        {
            Write(Filled(), out var workbook);

            using (workbook)
            {
                Assert.True(BomWorkbook.TryGetTable(workbook, BomWorkbook.PartsTable, out IXLTable table));

                Assert.Equal(
                    new[] { "Item", "Per unit", "Units", "Total", "Description", "Length", "Width", "Part number" },
                    table.HeadersRow().Cells().Select(c => c.GetValue<string>()).ToArray());
            }
        }

        [Fact]
        public void TheFinishesSheetIsATableWithStarterRowsAndNoNumbersBuiltIn()
        {
            Write(Filled(), out var workbook);

            using (workbook)
            {
                Assert.True(BomWorkbook.TryGetTable(workbook, BomWorkbook.FinishTable, out IXLTable table));
                var columns = BomWorkbook.Columns(table);

                foreach (string header in new[]
                {
                    BomWorkbook.FinItem, BomWorkbook.FinSpecification, BomWorkbook.FinBasis, BomWorkbook.FinArea,
                    BomWorkbook.FinCoverage, BomWorkbook.FinWaste, BomWorkbook.FinEach, BomWorkbook.FinQtyPerUnit,
                    BomWorkbook.FinUnit, BomWorkbook.FinTotalQty, BomWorkbook.FinUnitCost, BomWorkbook.FinExtended,
                    BomWorkbook.FinNotes,
                })
                    Assert.True(columns.ContainsKey(header), $"no '{header}' column");

                IXLWorksheet sheet = table.Worksheet;
                var items = table.DataRange!.Rows().Select(r => BomWorkbook.Text(sheet.Cell(r.RowNumber(), columns[BomWorkbook.FinItem]))).ToList();
                Assert.Equal(new[] { "Powder coat", "Stencil", "Stickers" }, items);

                // With no settings file, coverage, waste, area and cost are all left blank.
                foreach (IXLRangeRow r in table.DataRange.Rows())
                {
                    foreach (string blank in new[] { BomWorkbook.FinArea, BomWorkbook.FinCoverage, BomWorkbook.FinWaste, BomWorkbook.FinUnitCost })
                        Assert.True(sheet.Cell(r.RowNumber(), columns[blank]).IsEmpty(), blank);
                }
            }
        }

        [Fact]
        public void FinishStartersTakeCoverageAndWasteFromSettings()
        {
            string path = Path.Combine(directory, "settings-bom.xlsx");
            using (var wb = new XLWorkbook())
            {
                BomWorkbook.WriteTemplate(wb, Filled(), Filled().parts, key => key switch
                {
                    CutwrightSettings.PowderCoverageSqFtPerLb => "60",
                    CutwrightSettings.FinishWastePercent => "15",
                    _ => "",
                });
                wb.SaveAs(path);
            }

            using var read = new XLWorkbook(path);
            BomWorkbook.TryGetTable(read, BomWorkbook.FinishTable, out IXLTable table);
            var columns = BomWorkbook.Columns(table);
            int powder = table.DataRange!.FirstRow().RowNumber();

            Assert.Equal(60.0, table.Worksheet.Cell(powder, columns[BomWorkbook.FinCoverage]).GetValue<double>(), 6);
            Assert.Equal(0.15, table.Worksheet.Cell(powder, columns[BomWorkbook.FinWaste]).GetValue<double>(), 6);
        }

        // One case per basis, worked by hand: 24 sq ft, 15% waste.
        //   Coverage at 60 sq ft per lb:  24 x 1.15 / 60 = 0.46 lb per unit
        //   Per area:                     24 x 1.15      = 27.6 sq ft per unit
        //   Each:                         2 per unit
        [Theory]
        [InlineData(BomWorkbook.BasisCoverage, 60.0, 0.46)]
        [InlineData(BomWorkbook.BasisPerArea, 0.0, 27.6)]
        [InlineData(BomWorkbook.BasisEach, 0.0, 2.0)]
        public void FinishQuantitiesFollowTheirBasis(string basis, double coverage, double expectedPerUnit)
        {
            string path = Path.Combine(directory, "finish.xlsx");
            Filled(units: 10).WriteToFile(path);

            using (var wb = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(wb, BomWorkbook.FinishTable, out IXLTable table);
                var columns = BomWorkbook.Columns(table);
                int row = table.DataRange!.FirstRow().RowNumber();
                IXLWorksheet sheet = table.Worksheet;

                sheet.Cell(row, columns[BomWorkbook.FinBasis]).Value = basis;
                sheet.Cell(row, columns[BomWorkbook.FinArea]).Value = 24;
                sheet.Cell(row, columns[BomWorkbook.FinWaste]).Value = 0.15;
                sheet.Cell(row, columns[BomWorkbook.FinEach]).Value = 2;
                if (coverage > 0)
                    sheet.Cell(row, columns[BomWorkbook.FinCoverage]).Value = coverage;

                Assert.Equal(expectedPerUnit, sheet.Cell(row, columns[BomWorkbook.FinQtyPerUnit]).GetValue<double>(), 6);
                Assert.Equal(expectedPerUnit * 10, sheet.Cell(row, columns[BomWorkbook.FinTotalQty]).GetValue<double>(), 6);
            }
        }

        // The fault that mattered. Job_Units holds the unit count, Units is a formula pointing at it,
        // and Total multiplies the two - so a unit count of zero made every total on the sheet zero.
        [Fact]
        public void TheUnitCountReachesTheSheetSoTotalsAreNotZero()
        {
            var bom = Filled(units: 35);
            var sheet = Write(bom, out var workbook);

            using (workbook)
            {
                Assert.Equal(35, sheet.Cell("G6").GetValue<int>());

                for (int i = 0; i < bom.parts.Count; i++)
                {
                    int row = i + FirstPartRow;

                    Assert.Equal(bom.parts[i].quantity, sheet.Cell(row, 2).GetValue<int>());
                    Assert.Equal(BomWorkbook.JobUnits, sheet.Cell(row, 3).FormulaA1);
                    Assert.Equal($"B{row}*C{row}", sheet.Cell(row, 4).FormulaA1);
                    Assert.Equal(bom.parts[i].quantity * 35, sheet.Cell(row, 4).GetValue<int>());
                }
            }
        }

        [Fact]
        public void PartRowsUseTheTemplateColumns()
        {
            var bom = Filled();
            var sheet = Write(bom, out var workbook);

            using (workbook)
            {
                // Rows come out in estimator order, not the order the parts were added, so the row
                // is found rather than assumed.
                int row = Enumerable.Range(FirstPartRow, bom.parts.Count)
                    .First(r => sheet.Cell(r, 5).GetValue<string>() == "Plate 1/4 HR");

                // 12 wide, 24 long. Length is column F and width column G.
                Assert.Equal(24.0, sheet.Cell(row, 6).GetValue<double>(), 3);
                Assert.Equal(12.0, sheet.Cell(row, 7).GetValue<double>(), 3);
                Assert.Equal("P-2", sheet.Cell(row, 8).GetValue<string>());
            }
        }

        // A part checked for miters comes back out of FileParser with its ends set - through the End
        // Features sheet, since the old TUBE END column no longer exists.
        [Fact]
        public void EndStateRoundTripsThroughFileParser()
        {
            var bom = Filled();
            bom.parts[0].EndState = TubeEndState.SingleMiterClean; // SQ Tube - a tube part, width <= 0
            string path = Path.Combine(directory, "bom.xlsx");
            bom.WriteToFile(path);
            Assert.Empty(bom.Errors);

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            var tubeParts = parser.TNestList.SelectMany(n => n.Parts).ToList();

            Assert.Contains(tubeParts, p => p.PartNumber == "P-1" && p.EndState == TubeEndState.SingleMiterClean);
        }

        [Fact]
        public void TheJobBlockAndPartsRoundTripThroughFileParser()
        {
            var bom = Filled(units: 35);
            string path = Path.Combine(directory, "roundtrip.xlsx");
            bom.WriteToFile(path);

            var parser = new FileParser();
            parser.Parse(path);
            Assert.Empty(parser.Errors);

            Assert.Equal(35, parser.Units);
            Assert.Equal("Acme Trucking", parser.CustomerName);
            Assert.Equal("12345", parser.JobEst);
            Assert.Equal("N/A", parser.PrevJobEst);
            Assert.Equal("SD", parser.EngName);
            Assert.Equal("D-100", parser.DrawingNo);
            Assert.Equal("KJ", parser.CheckedBy);
            Assert.Equal("Sample Racks", parser.Description);
            Assert.Equal("2026-08-21", parser.Date);
            Assert.Equal("2", parser.RevisionNo);

            Part plate = Assert.Single(parser.PNestList.SelectMany(n => n.Parts), p => p.PartNumber == "P-2");
            Assert.Equal(12f, plate.width);
            Assert.Equal(24f, plate.length);
            Assert.Equal(6, plate.PerUnitQuantity);
            Assert.Equal(6 * 35, plate.quantity);
        }

        // A row added by hand and an extra column must not stop the file being read - that is what
        // reading by name buys. The table is grown with Resize rather than by inserting a row, which
        // ClosedXML cannot do in a workbook holding the Cutwright_BomFormat constant (Excel can).
        [Fact]
        public void ARowAddedByHandAndAnExtraColumnStillRead()
        {
            var bom = Filled(units: 2);
            string path = Path.Combine(directory, "edited.xlsx");
            bom.WriteToFile(path);

            using (var wb = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(wb, BomWorkbook.PartsTable, out IXLTable table);
                IXLWorksheet sheet = table.Worksheet;
                var columns = BomWorkbook.Columns(table);
                int last = table.DataRange!.LastRow().RowNumber();

                sheet.Cell(table.HeadersRow().RowNumber(), 9).Value = "Notes";
                table.Resize(sheet.Range(table.HeadersRow().RowNumber(), 1, last + 1, 9));

                int added = last + 1;
                sheet.Cell(added, columns[BomWorkbook.ColDescription]).Value = "Flat Bar 1/4 x 2";
                sheet.Cell(added, columns[BomWorkbook.ColPerUnit]).Value = 3;
                sheet.Cell(added, columns[BomWorkbook.ColLength]).Value = 30;
                sheet.Cell(added, columns[BomWorkbook.ColPartNumber]).Value = "P-9";
                sheet.Cell(added, 9).Value = "typed by hand";

                wb.SaveAs(path);
            }

            var parser = new FileParser();
            parser.Parse(path);
            Assert.Empty(parser.Errors);

            Part extra = Assert.Single(parser.TNestList.SelectMany(n => n.Parts), p => p.PartNumber == "P-9");
            Assert.Equal(6, extra.quantity);
            Assert.Equal(3, extra.PerUnitQuantity);
            Assert.Equal(4, parser.TNestList.SelectMany(n => n.Parts).Count() + parser.PNestList.SelectMany(n => n.Parts).Count());
        }

        // Sorting the table by hand reorders the rows and nothing else about them.
        [Fact]
        public void AResortedTableStillReadsTheSameParts()
        {
            var bom = Filled(units: 1);
            string path = Path.Combine(directory, "sorted.xlsx");
            bom.WriteToFile(path);

            using (var wb = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(wb, BomWorkbook.PartsTable, out IXLTable table);
                table.DataRange!.Sort("8 DESC");
                wb.SaveAs(path);
            }

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Equal(new[] { "P-1", "P-2", "P-3" },
                parser.TNestList.SelectMany(n => n.Parts).Concat(parser.PNestList.SelectMany(n => n.Parts))
                    .Select(p => p.PartNumber).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void AWorkbookThatIsNotACutwrightBomIsRefusedNotGuessedAt()
        {
            string path = Path.Combine(directory, "other.xlsx");
            using (var wb = new XLWorkbook())
            {
                var sheet = wb.AddWorksheet();
                sheet.Cell(1, 1).Value = "DESCRIPTION";
                sheet.Cell(2, 1).Value = "Plate 1/4 HR";
                wb.SaveAs(path);
            }

            var parser = new FileParser();
            parser.Parse(path);

            string error = Assert.Single(parser.Errors);
            Assert.Contains("not a Cutwright bill of materials", error);
            Assert.Empty(parser.PNestList);
            Assert.Empty(parser.TNestList);
        }

        [Fact]
        public void ABlankTemplateReadsBackAsAnEmptyBom()
        {
            string path = Path.Combine(directory, "blank.xlsx");
            var bom = new BillOfMaterials();
            bom.WriteToFile(path);
            Assert.Empty(bom.Errors);

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            Assert.Empty(parser.PNestList);
            Assert.Empty(parser.TNestList);
            Assert.Equal(1, parser.Units);
        }

        // Sorted the way an estimator lays a BOM out (BomLineOrder.ByRule), not alphabetically -
        // Tube before Plate before Sheet is the opposite of alphabetical order for this fixture,
        // so this only passes if the rule is actually being applied rather than OrderBy(Description).
        [Fact]
        public void PartsComeOutInEstimatorOrderNotAlphabetically()
        {
            var bom = Filled();
            var sheet = Write(bom, out var workbook);

            using (workbook)
            {
                var descriptions = Enumerable.Range(FirstPartRow, bom.parts.Count)
                    .Select(r => sheet.Cell(r, 5).GetValue<string>())
                    .ToList();

                Assert.Equal(
                    new[] { "SQ Tube 2 x 2 x 11 GA", "Plate 1/4 HR", "Sheet 11GA HR" },
                    descriptions);
            }
        }

        // FileParser groups parts by one exact Description, wherever the rows are. "SQ Tube 2 x 2 x
        // 11GA HR" and "SQ TUBE 2 X 2 X 11GA" read as the exact same MaterialSpec to
        // CalloutTranslator (case and the trailing "HR" do not change the parsed spec), so under
        // BomLineOrder alone every field up to PartSize ties between them - which used to let the
        // second, differently-worded description sort in between two rows of the first and
        // silently split one nesting group into two. Individual lengths (50, 30, 10) are chosen
        // so that is exactly what a PartSize-only tiebreak would do.
        [Fact]
        public void SameDescriptionRowsStayOneGroupAfterAWriteAndReParse()
        {
            var bom = new BillOfMaterials { Units = 1 };
            bom.parts.Add(new Part(1, 1, "SQ Tube 2 x 2 x 11GA HR", "P-100", 0f, 50f, 0f) { PerUnitQuantity = 1 });
            bom.parts.Add(new Part(2, 1, "SQ TUBE 2 X 2 X 11GA", "P-101", 0f, 30f, 0f) { PerUnitQuantity = 1 });
            bom.parts.Add(new Part(3, 1, "SQ Tube 2 x 2 x 11GA HR", "P-102", 0f, 10f, 0f) { PerUnitQuantity = 1 });

            string path = Path.Combine(directory, "grouping.xlsx");
            bom.WriteToFile(path);
            Assert.Empty(bom.Errors);

            var parser = new FileParser();
            parser.Parse(path);
            Assert.Empty(parser.Errors);

            var groups = parser.TNestList.Where(n => n.Description == "SQ Tube 2 x 2 x 11GA HR").ToList();
            Assert.True(groups.Count == 1,
                $"expected one group, found {groups.Count} - the row was split apart by another " +
                "description sorting in between its two occurrences.");
            Assert.Equal(2, groups[0].Parts.Count);
        }

        // Parts with no quantity are dropped, which is how a header row that came in from a CSV
        // stops being a part.
        [Fact]
        public void ZeroQuantityPartsAreLeftOut()
        {
            var bom = Filled();
            bom.parts.Add(new Part(0, 0, "QTY", "PART NO", 0f, 0f, 0f));

            var sheet = Write(bom, out var workbook);

            using (workbook)
            {
                var descriptions = Enumerable.Range(FirstPartRow, 4)
                    .Select(r => sheet.Cell(r, 5).GetValue<string>())
                    .ToList();

                Assert.DoesNotContain("PART NO", descriptions);
            }
        }

        // Writing takes a path now. It used to open its own SaveFileDialog, which is why a bill of
        // materials could not be produced without a person in front of it - and why none of this
        // could be tested.
        [Fact]
        public void AnExtensionIsAddedWhenThePathHasNone()
        {
            var bom = Filled();
            string path = Path.Combine(directory, "no-extension");

            bom.WriteToFile(path);

            Assert.Empty(bom.Errors);
            Assert.True(File.Exists(path + ".xlsx"));
            Assert.Equal(path + ".xlsx", bom.Filename);
        }

        [Fact]
        public void AnUnwritablePathIsReportedRatherThanThrown()
        {
            var bom = Filled();

            // The failure this list exists for: the estimator still has the workbook open, so the
            // file is locked and the save throws. Held open here with no sharing to reproduce it.
            string path = Path.Combine(directory, "locked.xlsx");

            using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                bom.WriteToFile(path);

            Assert.NotEmpty(bom.Errors);
        }

        // Reformatting an existing spreadsheet works out the unit count from the job quantity over
        // the per-unit quantity, so the form comes up prefilled rather than asking again.
        [Fact]
        public void ReadingASpreadsheetDerivesTheUnitCount()
        {
            string path = Path.Combine(directory, "source.xlsx");

            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet();

                sheet.Cell(2, 1).SetValue("P-1");
                sheet.Cell(2, 2).SetValue("Plate 1/4 HR");
                sheet.Cell(2, 3).SetValue(4);
                sheet.Cell(2, 4).SetValue(140);
                sheet.Cell(2, 5).SetValue(24);
                sheet.Cell(2, 6).SetValue(12);

                workbook.SaveAs(path);
            }

            var bom = new BillOfMaterials();
            bom.ReadFile(path);

            Assert.Empty(bom.Errors);
            output.WriteLine($"units derived as {bom.Units}");

            // 140 for the job, 4 per unit, so 35 units.
            Assert.Equal(35, bom.Units);
        }
    }
}
