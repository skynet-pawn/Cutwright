using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // A made-up job, run through the actual DET-generation code path (BillOfMaterials.WriteToFile,
    // unmodified) rather than a hand-picked example, so the gap between what it produces and what
    // an estimator would expect (BomLineOrder.ByRule) is visible on real output instead of asserted
    // away. Deliberately includes every BomLineOrder category plus the open gaps flagged in
    // BomLineOrder.cs's own doc comment (an exotic named grade, a bare sub-assembly with no
    // material words, Cotter Pin) so they show up here instead of only in a memory note.
    //
    // Not a pass/fail gate - a report to read. Writes the generated workbook to a fixed path
    // outside the test's own scratch directory so it survives the run and can be opened in Excel.
    public sealed class BomGenerationPressureTest : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string directory;

        public BomGenerationPressureTest(ITestOutputHelper output)
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

        // Save destination for the actual generated workbook, so it survives past the test run and
        // can be opened in Excel - a fixed name under the OS temp dir rather than the per-instance
        // Guid-named `directory` above, so it stays at the same path across runs. Previously a
        // hardcoded path into one machine's own scratch directory, which does not exist on any
        // other machine - CI included - and broke the test there the moment CI started running it.
        private static readonly string SurvivingOutputPath =
            Path.Combine(Path.GetTempPath(), "Cutwright.Tests", "PressureTest-BOM.xlsx");

        private static List<Part> BuildSyntheticJob()
        {
            var parts = new List<Part>();
            int ln = 0;
            int line() => ++ln;

            void Add(string desc, int qty, string partNo, float width = 0f, float length = 0f)
                => parts.Add(new Part(line(), qty, desc, partNo, width, length, width * length));

            // --- Tubes ---
            Add("SQ Tube 2 x 2 x 11GA HR", 4, "P-100", length: 83.9375f);
            Add("SQ Tube 1-1/2 x 1-1/2 x 11GA HR", 8, "P-101", length: 24f);
            Add("Rect Tube 4 x 2 x 11GA HR", 2, "P-102", length: 96f);
            Add("Round Tube 2\" OD x 11GA wall HR", 4, "P-103", length: 40f);

            // --- Pipe ---
            Add("Pipe 1-1/2\" Sch. 40", 4, "P-110", length: 48f);
            Add("Pipe 1\" Sch. 80", 2, "P-111", length: 36f);

            // --- Other Sticks (no fixed family priority, alphabetical) ---
            Add("L Angle 2 x 2 x 1/4 HR", 6, "P-120", length: 18f);
            Add("FB 1/4 x 2 HR", 4, "P-121", length: 30f);
            Add("PVC Pipe 1\" Sch. 40", 2, "P-122", length: 12f);
            Add("Rod 3/4\" HR", 4, "P-123", length: 6f);
            Add("SQ Bar Stock 1 x 1 HR", 2, "P-124", length: 8f);
            Add("C Channel C3 x 4.1", 2, "P-125", length: 72f); // AISC designation, no dims to rank by

            // --- Plate Steel (>= 1/4", mild/stainless/alum all compete here) ---
            Add("Plate 1/4 HR", 6, "P-130", width: 12f, length: 24f);
            Add("Plate 3/8 Stainless", 2, "P-131", width: 10f, length: 10f);
            Add("Plate 1/2 Alum", 1, "P-132", width: 6f, length: 6f);

            // --- Sheet Steel (< 1/4") ---
            Add("Sheet 11GA HR", 4, "P-140", width: 8f, length: 16f);
            Add("Sheet 14GA Stainless", 2, "P-141", width: 6f, length: 6f);
            Add("Sheet 16GA Alum", 3, "P-142", width: 5f, length: 12f);

            // --- Other Sheet Goods (non-metal flat stock, named by gauge/pitch/density) ---
            Add("Sheet 1/2 HDPE Black", 2, "P-150", width: 12f, length: 12f);
            Add("Sheet 1/4 UHMW", 2, "P-151", width: 4f, length: 8f);
            Add("Exp. Metal 3/4 x #9 Raised", 1, "P-152", width: 24f, length: 24f);
            Add("0.120 Wire 1x1 OC Wire Mesh", 1, "P-153", width: 18f, length: 18f);
            Add("6# XPLE Foam 2\" Pad", 4, "P-154", width: 12f, length: 12f);

            // --- Gap probes: not clean stock callouts, the way a real hand-typed or CSV-imported
            // line often is not. Called out individually below rather than left to blend in. ---
            Add("Liner S-7 Tool Steel 1/2 x 4 x 6", 4, "P-160", width: 4f, length: 6f);
            Add("Weld Assy - Corner Bracket (WA-004)", 8, "P-161");
            Add("CHECKERED_PLATE_BRKT_1", 2, "P-162");

            // --- Purchased: other ---
            Add("Caster 4\" Swivel", 4, "P-170");
            Add("Hinge Without Holes 12\"", 6, "P-171");
            Add("Gas Spring 100lb", 2, "P-172");
            Add("Stacking Cap for 2 x 2 SQ Tube", 8, "P-173");

            // --- Purchased: fasteners ---
            Add("Button Head Cap Screw 5/16-18 x 1 Gr. 5 ZP", 24, "P-180");
            Add("Hex Nut 5/16-18", 24, "P-181");
            Add("Flat Washer 5/16", 48, "P-182");
            Add("Rivnut 5/16-18 for 11GA Wall ZP", 12, "P-183");
            Add("3/8-16 Hex Flange Serrated Locknut GR5 ZP", 8, "P-184");
            Add("Cotter Pin 1/8 x 1", 6, "P-185"); // flagged gap: not matched by BomLineOrder.Fastener

            // --- A leftover CSV header row, the way FileParser sometimes sees one - must not
            // survive into the output. ---
            parts.Add(new Part(line(), 0, "QTY", "", 0f, 0f, 0f));

            return parts;
        }

        [Fact]
        public void SurveyASyntheticJobThroughRealDetGeneration()
        {
            var bom = new BillOfMaterials
            {
                CustomerName = "Pressure Test Co.",
                JobEst = "99-999",
                PrevJobEst = "N/A",
                Units = 10,
                Description = "Synthetic Pressure-Test Rack",
                DrawingNo = "PT-1",
                EngName = "Claude",
                CheckedBy = "SD",
                RevisionNo = "0",
                Date = DateTime.Today.ToString("yyyy-MM-dd"),
            };

            bom.parts.AddRange(BuildSyntheticJob());
            int expectedLineCount = bom.parts.Count(p => p.quantity > 0);

            string path = Path.Combine(directory, "pressure-test.xlsx");
            bom.WriteToFile(path);

            Assert.Empty(bom.Errors);
            Assert.True(File.Exists(path));

            // Copy to a stable path so the actual generated workbook survives the test run.
            File.Copy(path, SurvivingOutputPath, overwrite: true);
            output.WriteLine($"generated workbook copied to: {SurvivingOutputPath}");
            output.WriteLine("");

            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet(1);

            var actualRows = Enumerable.Range(10, expectedLineCount)
                .Select(r => sheet.Cell(r, 5).GetValue<string>())
                .ToList();

            // What BomLineOrder.ByRule would produce for the same parts - the ordering the app
            // does NOT yet apply at generation time (WriteToFile still does OrderBy(Description)).
            var byRule = bom.parts.Where(p => p.quantity > 0).OrderBy(p => p, BomLineOrder.ByRule)
                .Select(p => p.Description).ToList();

            output.WriteLine($"{expectedLineCount} lines written (header row with qty 0 correctly dropped: " +
                              $"{!actualRows.Contains("QTY")})");
            output.WriteLine("");
            output.WriteLine("=== ACTUAL OUTPUT (WriteToFile, OrderBy(Description) - what generation ships today) ===");
            foreach (string d in actualRows)
                output.WriteLine($"  {d}");

            output.WriteLine("");
            output.WriteLine("=== HOW AN ESTIMATOR WOULD LAY IT OUT (BomLineOrder.ByRule - not yet wired into WriteToFile) ===");
            foreach (string d in byRule)
                output.WriteLine($"  {d}");

            output.WriteLine("");
            output.WriteLine("=== CLASSIFICATION CHECK: what CalloutTranslator/BomLineOrder made of each line ===");
            foreach (Part p in bom.parts.Where(p => p.quantity > 0))
            {
                string desc = p.Description ?? "";
                bool translated = CalloutTranslator.CanTranslate(desc);
                string callout = translated ? CalloutTranslator.Translate(desc) : "(unrecognized - passed through)";
                bool fastener = BomLineOrder.IsFastener(desc);

                output.WriteLine($"  {desc,-46} -> {callout,-32} fastener={fastener}");
            }

            // Round-trip: what FileParser makes of the generated workbook, since a generated BOM
            // that cannot be read back in is worse than one that was never sortable.
            output.WriteLine("");
            output.WriteLine("=== ROUND TRIP: FileParser reading the generated workbook back ===");
            var parser = new FileParser();
            parser.Parse(path);

            if (parser.Errors.Count > 0)
                foreach (string e in parser.Errors)
                    output.WriteLine($"  ERROR: {e}");
            else
                output.WriteLine($"  parsed cleanly: {parser.TNestList.Count} tube groups, " +
                                  $"{parser.PNestList.Count} sheet groups");

            foreach (string w in parser.Warnings)
                output.WriteLine($"  NOTE {w}");

            // The survey always passes; it exists for its output.
            Assert.True(actualRows.Count > 0);
        }
    }
}
