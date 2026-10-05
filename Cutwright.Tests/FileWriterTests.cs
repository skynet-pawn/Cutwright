using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The export paths' failure reporting, and what lands on the Purchase sheet. Every save in
    // FileWriter used to swallow its exception, so an export that did nothing was indistinguishable
    // from one that worked. These cover the ways that actually happens in use, and then the purchase
    // list itself: what is bought, in what order, and what survives a re-save.
    public sealed class FileWriterTests : IDisposable
    {
        private readonly string directory;

        public FileWriterTests()
        {
            directory = Path.Combine(Path.GetTempPath(), "Cutwright.Tests.Writer", Guid.NewGuid().ToString("N"));
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

        // A Cutwright bill of materials with no parts: what the writer needs to find, and nothing else.
        private string WriteEmptyBom(string name)
        {
            string path = Path.Combine(directory, name);
            TestBom.Write(path, 1);
            return path;
        }

        // A sheet part bill of materials: two rows of one description with lengths and widths.
        private string WriteSheetBom(string name, string description)
        {
            return TestBom.Write(Path.Combine(directory, name), 1,
                TestBom.Line(1, 1, description, "X101-406", 42.5f, 23.0625f),
                TestBom.Line(2, 1, description, "X101-308", 39.0f, 25.6875f));
        }

        // Material rows (already represented by whatever PNest/TNest groups a test passes to
        // SendToFile, so they exist only to give the purchase builder its order), then hardware rows
        // - a quantity with no length or width, the one thing FileParser never reads back into
        // PNestList/TNestList.
        private string WriteFullBom(
            string name,
            IEnumerable<string> materialDescriptions,
            IEnumerable<(string Description, int Quantity)>? hardware = null,
            int units = 1)
        {
            int item = 0;
            var parts = materialDescriptions
                .Select(d => TestBom.Line(++item, 1, d, "M-" + item, 10f, 5f))
                .Concat((hardware ?? Enumerable.Empty<(string, int)>())
                    .Select(h => TestBom.Line(++item, h.Quantity, h.Description, "HW-" + item, 0f)))
                .ToArray();

            return TestBom.Write(Path.Combine(directory, name), units, parts);
        }

        // Sets the descriptions of the parts table's rows, in row order, after the writer has
        // sorted them - so a test can put the table in whatever order a hand-edited job has.
        private static void Redescribe(string path, params string[] descriptions)
        {
            using var workbook = new XLWorkbook(path);
            BomWorkbook.TryGetTable(workbook, BomWorkbook.PartsTable, out IXLTable table);
            int column = BomWorkbook.Columns(table)[BomWorkbook.ColDescription];
            int first = table.DataRange!.FirstRow().RowNumber();

            for (int i = 0; i < descriptions.Length; i++)
                table.Worksheet.Cell(first + i, column).Value = descriptions[i];

            workbook.Save();
        }

        // Three lines, eleven parts. Kept apart on purpose: the removed "QTY" option counted BOM
        // lines, so a group where the two matched would not have caught it.
        private static PNest SheetGroup(string description)
        {
            var nest = new PNest(48.0f, 96.0f, 0.5f, 0.125f) { Description = description };

            nest.Parts.Add(new Part(1, 2, description, "X101-406", 23.0625f, 42.5f, 980.0f));
            nest.Parts.Add(new Part(2, 4, description, "X101-308", 25.6875f, 39.0f, 1002.0f));
            nest.Parts.Add(new Part(3, 5, description, "X101-105", 2.625f, 7.625f, 20.0f));

            return nest;
        }

        private static TNest TubeGroup(string description, int quantity = 1)
        {
            var tube = new TNest { Description = description, StickLength = 240f };
            tube.Parts.Add(new Part(1, quantity, description, "T-1", 2.0f, 60.0f, 120.0f));
            tube.Sticks.Add(new Stick(240f, 0.125f));
            return tube;
        }

        private sealed record PurchaseRow(int Line, double Quantity, string QuantityFormula, string Unit,
            string Description, double? UnitCost, string UnitCostFormula);

        private static List<PurchaseRow> ReadPurchase(string path)
        {
            using var workbook = new XLWorkbook(path);
            Assert.True(BomWorkbook.TryGetTable(workbook, BomWorkbook.PurchaseTable, out IXLTable table),
                "The Purchase sheet has no table.");

            var columns = BomWorkbook.Columns(table);
            var rows = new List<PurchaseRow>();
            IXLWorksheet sheet = table.Worksheet;

            foreach (IXLRangeRow dataRow in table.DataRange!.Rows())
            {
                int r = dataRow.RowNumber();
                IXLCell qty = sheet.Cell(r, columns[BomWorkbook.PurQty]);
                IXLCell cost = sheet.Cell(r, columns[BomWorkbook.PurUnitCost]);
                string description = BomWorkbook.Text(sheet.Cell(r, columns[BomWorkbook.PurDescription]));

                if (description.Length == 0)
                    continue;

                BomWorkbook.TryNumber(qty, out double quantity);

                rows.Add(new PurchaseRow(
                    sheet.Cell(r, columns[BomWorkbook.PurLine]).GetValue<int>(),
                    quantity,
                    qty.HasFormula ? qty.FormulaA1 : "",
                    BomWorkbook.Text(sheet.Cell(r, columns[BomWorkbook.PurUnit])),
                    description,
                    BomWorkbook.TryNumber(cost, out double c) ? c : null,
                    cost.HasFormula ? cost.FormulaA1 : ""));
            }

            return rows;
        }

        private static PurchaseRow ReadPurchaseRow(string path, string description)
        {
            var row = ReadPurchase(path)
                .FirstOrDefault(r => r.Description.Contains(description, StringComparison.Ordinal));

            Assert.True(row is not null, $"No purchase row for '{description}' was written.");
            return row!;
        }

        // The footer is found by its label rather than assumed, since it follows however many
        // lines were written.
        private static (int Row, string FormulaA1) ReadTotalCostRow(string path)
        {
            using var workbook = new XLWorkbook(path);
            IXLWorksheet sheet = workbook.Worksheet(BomWorkbook.PurchaseSheet);

            for (int i = 1; i < 1000; i++)
            {
                if (sheet.Cell(i, 4).GetValue<string>().Equals("Total cost", StringComparison.Ordinal))
                    return (i, sheet.Cell(i, 6).FormulaA1);
            }

            Assert.Fail("No Total cost row was written.");
            return default;
        }

        [Fact]
        public void ExportingToAPathThatIsNotAWorkbookIsReported()
        {
            string path = Path.Combine(directory, "not-a-workbook.xlsx");
            File.WriteAllText(path, "This is not a spreadsheet.");

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.NotEmpty(writer.Errors);
            Assert.Contains("not-a-workbook.xlsx", writer.Errors[0], StringComparison.Ordinal);
        }

        // The Save dialog invites a brand new filename. With no source to copy from - the ordinary
        // case being a caller that never passed one - there is still nothing to write results into.
        [Fact]
        public void ExportingToAFileThatDoesNotExistWithNoSourceIsReported()
        {
            string path = Path.Combine(directory, "brand-new.xlsx");

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.NotEmpty(writer.Errors);
            Assert.False(File.Exists(path), "Nothing should be written when there is no BOM to write into.");
        }

        // Same as above, but a source was named and it does not exist either - a stale or deleted
        // path rather than simply none at all.
        [Fact]
        public void ExportingToAFileThatDoesNotExistWithAMissingSourceIsReported()
        {
            string path = Path.Combine(directory, "brand-new-2.xlsx");
            string missingSource = Path.Combine(directory, "never-existed.xlsx");

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>(), missingSource);

            Assert.NotEmpty(writer.Errors);
            Assert.False(File.Exists(path));
        }

        // "Save As": a filename that does not exist yet, with the bill of materials that was
        // actually loaded and nested named as the source to copy from. This is what makes Save
        // work for a brand new filename instead of only for the file that was originally loaded.
        [Fact]
        public void SavingToANewFilenameCopiesTheLoadedBomFirst()
        {
            const string description = "Sheet 11GA HR";
            string source = WriteSheetBom("loaded.xlsx", description);
            string dest = Path.Combine(directory, "save-as.xlsx");

            var group = SheetGroup(description);
            group.SheetCount = 4;

            var writer = new FileWriter();
            writer.SendToFile(dest, new List<PNest> { group }, new List<TNest>(), source);

            Assert.Empty(writer.Errors);
            Assert.True(File.Exists(dest));
            Assert.Equal(4, ReadPurchaseRow(dest, description).Quantity);

            // The parts came across too, not just a blank workbook with a purchase sheet grafted on.
            var parser = new FileParser();
            parser.Parse(dest);
            Assert.Empty(parser.Errors);
            Assert.Equal(2, parser.PNestList.SelectMany(n => n.Parts).Count());
        }

        // The file that was loaded is a source to copy from, never a destination to write over -
        // "Save As" must not mutate the original job file still sitting on disk.
        [Fact]
        public void SavingToANewFilenameLeavesTheOriginalUntouched()
        {
            const string description = "Sheet 11GA HR";
            string source = WriteSheetBom("original.xlsx", description);
            string dest = Path.Combine(directory, "copy.xlsx");

            var group = SheetGroup(description);
            group.SheetCount = 7;

            var writer = new FileWriter();
            writer.SendToFile(dest, new List<PNest> { group }, new List<TNest>(), source);

            Assert.Empty(writer.Errors);

            using var workbook = new XLWorkbook(source);
            Assert.False(workbook.TryGetWorksheet(BomWorkbook.PurchaseSheet, out _),
                "The original file should not have gained a Purchase sheet from the copy's save.");
        }

        // A source is harmless noise, not a fallback, once the destination already exists - the
        // ordinary "save back into the file that was loaded" case must behave exactly as before
        // even though a source path is now always passed in from MainWindow.
        [Fact]
        public void SavingIntoAnExistingFileIgnoresSourcePath()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("existing.xlsx", description);
            string unrelatedSource = WriteSheetBom("unrelated.xlsx", "Plate 1/4 HR");

            var group = SheetGroup(description);
            group.SheetCount = 2;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>(), unrelatedSource);

            Assert.Empty(writer.Errors);
            Assert.Equal(2, ReadPurchaseRow(path, description).Quantity);
        }

        // The everyday failure: the estimator still has the workbook open, so it is locked.
        [Fact]
        public void ALockedFileIsReportedRatherThanSilentlyFailing()
        {
            string path = WriteEmptyBom("locked.xlsx");

            using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var writer = new FileWriter();
                writer.SendToFile(path, new List<PNest>(), new List<TNest>());

                Assert.NotEmpty(writer.Errors);
                Assert.Contains(writer.Errors, e =>
                    e.Contains("locked.xlsx", StringComparison.Ordinal));
            }
        }

        // Results go into a Cutwright bill of materials; a workbook that is not one is a stop, with the
        // way to get one.
        [Fact]
        public void AWorkbookThatIsNotACutwrightBomIsReported()
        {
            string path = Path.Combine(directory, "other.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Sheet1");
                sheet.Cell(1, 5).Value = "DESCRIPTION";
                sheet.Cell(2, 5).Value = "Sheet 11GA HR";
                workbook.SaveAs(path);
            }

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.NotEmpty(writer.Errors);
            Assert.Contains(writer.Errors, e => e.Contains("not a Cutwright bill of materials", StringComparison.Ordinal));
        }

        [Fact]
        public void AWritableBomReportsNoErrors()
        {
            string path = WriteEmptyBom("writable.xlsx");

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.Empty(writer.Errors);
            Assert.True(File.Exists(path));
        }

        // A nested group buys sheets, so the quantity is a sheet count and the size it is a count
        // of belongs in the description.
        [Fact]
        public void ASheetGroupExportsItsSheetCountAndSize()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("by-the-sheet.xlsx", description);

            var group = SheetGroup(description);
            group.SheetCount = 6;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            Assert.Empty(writer.Errors);

            var row = ReadPurchaseRow(path, description);

            Assert.Equal(6, row.Quantity);
            Assert.Equal("EA", row.Unit);
            Assert.Contains("96x48", row.Description, StringComparison.Ordinal);
        }

        // Only a Smallest Drop group puts its size on its own line under the material: the size was
        // worked out from the group's own parts, so it is what to quote. A catalog size keeps its
        // usual suffix.
        [Fact]
        public void ASmallestDropSheetGroupPutsItsSizeOnALineUnderTheMaterial()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("smallest-drop-sheet.xlsx", description);

            var group = SheetGroup(description);
            group.SheetCount = 6;
            group.SizeToSmallestDrop = true;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            Assert.Empty(writer.Errors);

            Assert.Equal(description + "\nSheet Size Needed = 96\" x 48\"",
                ReadPurchaseRow(path, description).Description);
        }

        [Fact]
        public void ASmallestDropStickGroupPutsItsLengthOnALineUnderTheMaterial()
        {
            const string description = "Tube 2x2x.25 SQ";
            string path = TestBom.Write(Path.Combine(directory, "smallest-drop-stick.xlsx"), 1,
                TestBom.Line(1, 1, description, "T-1", 60f));

            var group = new TNest { Description = description, StickLength = 195.375f, SizeToSmallestDrop = true };
            group.Parts.Add(new Part(1, 1, description, "T-1", 2.0f, 60.0f, 120.0f));
            group.Sticks.Add(new Stick(195.375f, 0.125f));

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest> { group });

            Assert.Empty(writer.Errors);

            Assert.Equal(description + "\nLength Needed = 195.38\"",
                ReadPurchaseRow(path, description).Description);
        }

        [Fact]
        public void ACatalogSizeKeepsItsSuffixAndNoSizeLine()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("catalog-size.xlsx", description);

            var group = SheetGroup(description);
            group.SheetCount = 6;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            string text = ReadPurchaseRow(path, description).Description;
            Assert.Contains("96x48", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Needed", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', text);
        }

        // The size is on a second line of the cell, which only shows if the cell wraps and the row is
        // tall enough for both lines.
        [Fact]
        public void ASizeLineGetsAWrappedCellAndATallEnoughRow()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("two-line-row.xlsx", description);

            var group = SheetGroup(description);
            group.SheetCount = 2;
            group.SizeToSmallestDrop = true;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            Assert.Empty(writer.Errors);

            using var workbook = new XLWorkbook(path);
            Assert.True(BomWorkbook.TryGetTable(workbook, BomWorkbook.PurchaseTable, out IXLTable table));
            IXLWorksheet sheet = table.Worksheet;
            int column = BomWorkbook.Columns(table)[BomWorkbook.PurDescription];

            int row = table.DataRange!.Rows()
                .Select(r => r.RowNumber())
                .First(r => BomWorkbook.Text(sheet.Cell(r, column)).Contains("Sheet Size Needed", StringComparison.Ordinal));

            Assert.True(sheet.Cell(row, column).Style.Alignment.WrapText);
            Assert.True(sheet.Row(row).Height >= 30, "Two lines of text need a taller row than one.");
        }

        // Changing Units in the app rescales the quantities in memory, but every total in the file
        // multiplies by Job_Units, so a save that did not write it back kept the old totals.
        [Fact]
        public void ChangingUnitsReachesTheJobUnitsCellAndTheTotals()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteFullBom("units-change.xlsx",
                materialDescriptions: new[] { description },
                hardware: new[] { ("Hex Bolt 1/2-13 x 1", 8) },
                units: 1);

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>(), units: 5);

            Assert.Empty(writer.Errors);

            using var workbook = new XLWorkbook(path);
            Assert.Equal(5, BomWorkbook.ReadUnits(workbook));

            // The purchase list was built from the new count too: 8 bolts a unit, 5 units.
            Assert.Equal(40, ReadPurchaseRow(path, "Hex Bolt").Quantity);
        }

        // A workbook with no Job_Units cell was not laid out around one, so none is invented.
        [Fact]
        public void SavingWithUnitsLeavesAWorkbookWithoutAUnitsCellAlone()
        {
            string path = WriteEmptyBom("no-units-cell.xlsx");

            using (var workbook = new XLWorkbook(path))
            {
                workbook.DefinedNames.Delete(BomWorkbook.JobUnits);
                workbook.Save();
            }

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>(), units: 5);

            Assert.Empty(writer.Errors);

            using var saved = new XLWorkbook(path);
            Assert.Null(BomWorkbook.NamedCell(saved, BomWorkbook.JobUnits));
        }

        // A stick group's description used to only show a size for four hardcoded lengths
        // (144/240/252/288) - anything else, including a "Smallest Drop" group's own custom
        // length, silently lost its "(x...)" suffix instead of showing what it actually cut from.
        [Fact]
        public void AStickGroupExportsTheLengthItActuallyCutFrom()
        {
            const string description = "Tube 2x2x.25 SQ";
            string path = TestBom.Write(Path.Combine(directory, "by-the-stick.xlsx"), 1,
                TestBom.Line(1, 1, description, "T-1", 60f));

            var group = new TNest { Description = description, StickLength = 195.375f };
            group.Parts.Add(new Part(1, 1, description, "T-1", 2.0f, 60.0f, 120.0f));
            group.Sticks.Add(new Stick(195.375f, 0.125f));

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest> { group });

            Assert.Empty(writer.Errors);

            var row = ReadPurchaseRow(path, description);

            Assert.Equal(1, row.Quantity);
            Assert.Equal("EA", row.Unit);
            Assert.Contains($"x{group.StickLength:0.##}", row.Description, StringComparison.Ordinal);
        }

        // Bought by the piece, on the stick side: the quantity is the parts the BOM asks for, in
        // each, with nothing nested - not the (cleared) stick count, and not a stick length.
        [Fact]
        public void AByThePieceStickGroupExportsAPieceCountInEach()
        {
            const string description = "Tube 2x2x.25 SQ";
            string path = TestBom.Write(Path.Combine(directory, "stick-by-the-piece.xlsx"), 1,
                TestBom.Line(1, 4, description, "T-1", 60f),
                TestBom.Line(2, 3, description, "T-2", 30f));

            var group = new TNest { Description = description, StickLength = 240f };
            group.Parts.Add(new Part(1, 4, description, "T-1", 2.0f, 60.0f, 120.0f));
            group.Parts.Add(new Part(2, 3, description, "T-2", 2.0f, 30.0f, 60.0f));
            group.Sticks.Add(new Stick(240f, 0.125f));
            group.BuyByThePiece();

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest> { group });

            Assert.Empty(writer.Errors);

            var row = ReadPurchaseRow(path, description);

            Assert.Equal(7, row.Quantity);
            Assert.Equal("EA", row.Unit);
            Assert.Contains("CUT PARTS", row.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("x240", row.Description, StringComparison.Ordinal);
        }

        // Bought by the piece: the supplier delivers the parts already cut, so the quantity counts
        // parts - eleven of them across three BOM lines - and no sheet is bought.
        [Fact]
        public void AByThePieceSheetGroupExportsAPieceCountInEach()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("by-the-piece.xlsx", description);

            var group = SheetGroup(description);
            group.BuyByThePiece();

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            Assert.Empty(writer.Errors);

            var row = ReadPurchaseRow(path, description);

            Assert.Equal(11, row.Quantity);
            Assert.Equal("EA", row.Unit);
            Assert.Contains("CUT PARTS", row.Description, StringComparison.Ordinal);
        }

        // The reason the option was removed rather than left in place. The export wrote the count
        // under a "(Length x Width)" description unconditionally, so a piece count was quoted as
        // that many sheets of that size: eleven pieces of dunnage became eleven 48x96 sheets, on a
        // row that looked no different from a real sheet buy.
        //
        // The stale sheet count and size here are what a group that had been nested before the
        // user switched it to "QTY" carries, which is the case that produced the wrong quote.
        [Fact]
        public void APieceCountIsNeverQuotedAsSheetsOfASize()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("not-sheets.xlsx", description);

            var group = SheetGroup(description);
            group.SheetCount = 3;
            group.Efficiency = "62.4";
            group.BuyByThePiece();

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            Assert.Empty(writer.Errors);

            var row = ReadPurchaseRow(path, description);

            Assert.Equal(11, row.Quantity);
            Assert.DoesNotContain("96x48", row.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("48x96", row.Description, StringComparison.Ordinal);
        }

        // SendToFile is handed the groups to write; it used to read a field set by AddSheetNest
        // instead and ignore the argument, which only worked because the one caller passed the
        // same list to both.
        [Fact]
        public void TheGroupsPassedToSendToFileAreTheOnesWritten()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("passed-in.xlsx", description);

            var group = SheetGroup(description);
            group.BuyByThePiece();

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            Assert.Equal(11, ReadPurchaseRow(path, description).Quantity);
        }

        // A job needing more distinct purchase materials than any fixed template ever had rows for
        // used to write straight through the footer instead of stopping. There is no fixed capacity
        // to overrun - every group gets a row, however many there are.
        [Fact]
        public void EveryMaterialGetsAPurchaseRowHoweverManyThereAre()
        {
            var sheetGroups = Enumerable.Range(1, 10)
                .Select(i => SheetGroup($"Sheet Material {i:00}"))
                .ToList();
            foreach (var group in sheetGroups)
                group.SheetCount = 2;

            var tubeGroups = Enumerable.Range(1, 10)
                .Select(i => TubeGroup($"Tube Material {i:00}"))
                .ToList();

            var descriptions = sheetGroups.Select(g => g.Description!)
                .Concat(tubeGroups.Select(g => g.Description))
                .ToList();

            string path = WriteFullBom("many-materials.xlsx", descriptions);

            var writer = new FileWriter();
            writer.SendToFile(path, sheetGroups, tubeGroups);

            Assert.Empty(writer.Errors);

            foreach (var group in sheetGroups)
                Assert.Equal(2, ReadPurchaseRow(path, group.Description!).Quantity);

            foreach (var group in tubeGroups)
                Assert.Equal(1, ReadPurchaseRow(path, group.Description).Quantity);

            var total = ReadTotalCostRow(path);
            Assert.StartsWith("SUM(F", total.FormulaA1, StringComparison.Ordinal);
            Assert.Equal(20, ReadPurchase(path).Count);
        }

        // A re-save after re-nesting with fewer materials than last time must not leave the earlier
        // save's rows stranded below the new, shorter footer.
        [Fact]
        public void ReSavingWithFewerMaterialsShrinksThePurchaseList()
        {
            var groups = Enumerable.Range(1, 5)
                .Select(i => SheetGroup($"Shrink Material {i:00}"))
                .ToList();
            foreach (var group in groups)
                group.SheetCount = 1;

            string path = WriteFullBom("shrink.xlsx", groups.Select(g => g.Description!));

            var writer = new FileWriter();
            writer.SendToFile(path, groups, new List<TNest>());
            Assert.Empty(writer.Errors);
            var firstTotal = ReadTotalCostRow(path);

            writer.SendToFile(path, groups.Take(2).ToList(), new List<TNest>());
            Assert.Empty(writer.Errors);
            var secondTotal = ReadTotalCostRow(path);

            Assert.True(secondTotal.Row < firstTotal.Row,
                "A re-save with fewer materials should move the footer up, not leave it where it was.");
            Assert.Equal(2, ReadPurchase(path).Count);

            // Nothing from the first save's longer list survives below the new footer.
            using var workbook = new XLWorkbook(path);
            IXLWorksheet sheet = workbook.Worksheet(BomWorkbook.PurchaseSheet);
            for (int row = secondTotal.Row + 2; row <= firstTotal.Row + 1; row++)
                Assert.True(sheet.Row(row).IsEmpty(), $"Row {row} should have been cleared.");
        }

        // Only one Purchase sheet, in the same place, however many times it is rebuilt.
        [Fact]
        public void ReSavingKeepsOnePurchaseSheetInPlace()
        {
            string path = WriteFullBom("resave.xlsx", new[] { "Sheet 11GA HR" });
            var group = SheetGroup("Sheet 11GA HR");
            group.SheetCount = 1;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());
            Assert.Empty(writer.Errors);

            using var workbook = new XLWorkbook(path);
            Assert.Equal(1, workbook.Worksheets.Count(w => w.Name == BomWorkbook.PurchaseSheet));
            Assert.Equal(BomWorkbook.PurchaseSheet, workbook.Worksheets.Last(w => w.Name != "End Features").Name);
            Assert.True(BomWorkbook.TryGetTable(workbook, BomWorkbook.PurchaseTable, out _));
        }

        // Hardware - a fastener line with a quantity but no length or width - only ever exists in
        // the parts table, one row per BOM line. Two rows with the same description (a CSV that
        // listed the same part twice, say) collapse into the one purchase line the user asked for.
        [Fact]
        public void DuplicateHardwareDescriptionsCollapseToOneLineWithSummedQuantity()
        {
            string path = WriteFullBom("dup-hardware.xlsx",
                materialDescriptions: Array.Empty<string>(),
                hardware: new[]
                {
                    ("Button Head Cap Screw 5/16-18", 960),
                    ("Button Head Cap Screw 5/16-18", 40),
                });

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.Empty(writer.Errors);

            var lines = ReadPurchase(path).Where(r => r.Description.Contains("Button Head Cap Screw")).ToList();
            Assert.Equal(1000, Assert.Single(lines).Quantity);
        }

        // Hardware is bought in the units it is counted in, so the quantity is the job's total.
        [Fact]
        public void HardwareQuantityIsScaledByTheJobsUnits()
        {
            string path = WriteFullBom("hardware-units.xlsx",
                materialDescriptions: Array.Empty<string>(),
                hardware: new[] { ("Hex Bolt 1/2-13 x 1", 8) },
                units: 25);

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.Empty(writer.Errors);
            Assert.Equal(200, ReadPurchaseRow(path, "Hex Bolt").Quantity);
        }

        // The parts table is not always alphabetical - a CSV import or a hand-edited job keeps
        // whatever order it arrived in - so the purchase list has to track that order rather than
        // assume one.
        [Fact]
        public void PurchaseLinesMatchThePartsTablesOrderNotAlphabeticalOrder()
        {
            // Deliberately out of alphabetical order: SQ Tube, then Rec Tube, then Sheet -
            // alphabetically that would be Rec Tube, Sheet, then SQ Tube.
            var descriptions = new[] { "SQ Tube 2x2x11GA HR", "Rec Tube 8x3x11GA", "Sheet 11GA HR" };
            string path = WriteFullBom("table-order.xlsx", new[] { "A", "B", "C" });
            Redescribe(path, descriptions);

            var sqTube = TubeGroup("SQ Tube 2x2x11GA HR");
            var recTube = TubeGroup("Rec Tube 8x3x11GA");
            var sheetGroup = SheetGroup("Sheet 11GA HR");
            sheetGroup.SheetCount = 1;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { sheetGroup }, new List<TNest> { recTube, sqTube });

            Assert.Empty(writer.Errors);

            var lines = ReadPurchase(path);
            Assert.Contains("SQ Tube", lines[0].Description, StringComparison.Ordinal);
            Assert.Contains("Rec Tube", lines[1].Description, StringComparison.Ordinal);
            Assert.Contains("Sheet 11GA HR", lines[2].Description, StringComparison.Ordinal);
            Assert.Equal(new[] { 1, 2, 3 }, lines.Select(l => l.Line).ToArray());
        }

        // Unit costs a purchaser typed in belong to the line, not to the save: a re-nest that
        // rebuilds the list must not wipe them.
        [Fact]
        public void UnitCostsTypedInSurviveAReSave()
        {
            const string description = "Sheet 11GA HR";
            string path = WriteSheetBom("costs.xlsx", description);
            var group = SheetGroup(description);
            group.SheetCount = 2;

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());

            using (var workbook = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(workbook, BomWorkbook.PurchaseTable, out IXLTable table);
                int column = BomWorkbook.Columns(table)[BomWorkbook.PurUnitCost];
                table.Worksheet.Cell(table.DataRange!.FirstRow().RowNumber(), column).Value = 85.5;
                workbook.Save();
            }

            group.SheetCount = 3;
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());
            Assert.Empty(writer.Errors);

            var row = ReadPurchaseRow(path, description);
            Assert.Equal(3, row.Quantity);
            Assert.Equal(85.5, row.UnitCost);

            using var reopened = new XLWorkbook(path);
            IXLWorksheet sheet = reopened.Worksheet(BomWorkbook.PurchaseSheet);
            var total = ReadTotalCostRow(path);
            Assert.Equal(3 * 85.5, sheet.Cell(total.Row, 6).GetValue<double>(), 6);
        }

        // Each treatment on the Finishes sheet becomes a purchase line that reads its quantity and
        // cost live off that sheet, so changing an area or a coverage there changes what is bought.
        // 24 sq ft, 15% waste, 60 sq ft per lb, 10 units: 0.46 lb each, 4.6 lb in all.
        [Fact]
        public void FinishLinesFollowTheFinishesSheet()
        {
            string path = WriteFullBom("finishes.xlsx", new[] { "Sheet 11GA HR" }, units: 10);

            using (var workbook = new XLWorkbook(path))
            {
                BomWorkbook.TryGetTable(workbook, BomWorkbook.FinishTable, out IXLTable table);
                var columns = BomWorkbook.Columns(table);
                IXLWorksheet finishes = table.Worksheet;
                int powder = table.DataRange!.FirstRow().RowNumber();

                finishes.Cell(powder, columns[BomWorkbook.FinSpecification]).Value = "RAL 9005";
                finishes.Cell(powder, columns[BomWorkbook.FinArea]).Value = 24;
                finishes.Cell(powder, columns[BomWorkbook.FinCoverage]).Value = 60;
                finishes.Cell(powder, columns[BomWorkbook.FinWaste]).Value = 0.15;
                finishes.Cell(powder, columns[BomWorkbook.FinUnitCost]).Value = 6.5;

                // Stencil: each, 0.1 per unit -> exactly 1 can for 10 units.
                finishes.Cell(powder + 1, columns[BomWorkbook.FinEach]).Value = 0.1;

                // Stickers is left as it came - nothing filled in, so nothing to buy.

                workbook.Save();
            }

            var group = SheetGroup("Sheet 11GA HR");
            group.SheetCount = 1;
            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest> { group }, new List<TNest>());
            Assert.Empty(writer.Errors);

            var lines = ReadPurchase(path);
            Assert.Equal(new[] { "Sheet 11GA HR", "Powder coat - RAL 9005", "Stencil" },
                lines.Select(l => l.Description.Split(" (")[0]).ToArray());

            var powderLine = lines[1];
            Assert.Equal("lb", powderLine.Unit);
            Assert.Equal("Finishes!$J$5", powderLine.QuantityFormula.Replace("'", ""));
            Assert.Equal(4.6, powderLine.Quantity, 6);
            Assert.Equal(6.5, powderLine.UnitCost);

            var stencilLine = lines[2];
            Assert.StartsWith("ROUNDUP(", stencilLine.QuantityFormula, StringComparison.Ordinal);
            Assert.Equal(1, stencilLine.Quantity, 6);
            Assert.Null(stencilLine.UnitCost);
        }

        // A job with nothing to buy still gets a footer.
        [Fact]
        public void AJobWithNothingToBuyStillGetsAFooter()
        {
            string path = WriteEmptyBom("empty-job.xlsx");

            var writer = new FileWriter();
            writer.SendToFile(path, new List<PNest>(), new List<TNest>());

            Assert.Empty(writer.Errors);
            Assert.True(ReadTotalCostRow(path).Row > 0);
        }

        // Errors describe one export, so a retry that succeeds must not still look broken.
        [Fact]
        public void ErrorsAreClearedBetweenExports()
        {
            string bad = Path.Combine(directory, "bad.xlsx");
            File.WriteAllText(bad, "junk");
            string good = WriteEmptyBom("good.xlsx");

            var writer = new FileWriter();

            writer.SendToFile(bad, new List<PNest>(), new List<TNest>());
            Assert.NotEmpty(writer.Errors);

            writer.SendToFile(good, new List<PNest>(), new List<TNest>());
            Assert.Empty(writer.Errors);
        }
    }
}
