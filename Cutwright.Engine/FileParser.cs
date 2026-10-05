using ClosedXML.Excel;
using System.IO;

/*
 * Parses excel file separating parts by Description(Material Type)
 * Holds lists of Nest1D and Nest2D parts containing lists of parts.
 * Builds flat lists so if the quantity is greater than 1 for any item
 * the parser will add that repeated instances of that item to ease
 * the nesting/sorting process.
 */

/*
 * Each line of the BOM is implied to be a different
 * part, so we should only have a number of parts at maximum
 * that correspond to the number of unique parts on the BOM.
 * We don't need to list every single part, just each part
 * with a quantity.
 */

namespace Cutwright
{
    internal class FileParser
    {
        public FileParser()
        {
            this.PNestList = new List<PNest>();
            this.TNestList = new List<TNest>();
            this.SheetParts = new List<Part> { };

        }

        public FileParser(string Path)
        {
            this.PNestList = new List<PNest>();
            this.TNestList = new List<TNest>();
            this.SheetParts = new List<Part>();

        }

        // Why a BOM failed to parse, for the caller to show. Distinct from
        // SheetNestEngine.Warnings, which are notes about a nest that did happen - anything in
        // here means no parts came out, so the lists are empty rather than incomplete.
        //
        // The parser used to raise these as MessageBox calls itself, which made it unusable
        // outside the UI: a batch run or a test hit a modal dialog and blocked forever with
        // nothing logged. Reporting is the caller's job.
        public List<string> Errors { get; } = new();

        // Things worth telling the estimator about a BOM that did parse: parts that found no DXF
        // and so nest as their bounding box, and part numbers that matched more than one DXF.
        // Neither stops a nest, but both change the sheet count, so neither should be silent.
        public List<string> Warnings { get; } = new();

        // The file this was actually loaded from, so FileWriter can copy it as the starting point
        // for a "Save As" to a filename that does not exist yet - there is nowhere else to get the
        // DET section and title block a brand new file would otherwise be missing.
        public string SourcePath { get; private set; } = "";

        public void Parse(string Path)
        {
            List<Part> tmpTubeList = new List<Part>();
            List<Part> tmpSheetList = new List<Part>();

            this.SourcePath = Path;

            Errors.Clear();
            Warnings.Clear();

            XLWorkbook wBook;
            if (Path == "")
                return;
            try
            {
                wBook = new XLWorkbook(Path);
            }
            catch (Exception e)
            {
                Errors.Add($"Could not open '{System.IO.Path.GetFileName(Path)}': {e.Message}");
                return;
            }

            using (wBook)
            {
                // Cutwright reads only its own format, found by name (see BomWorkbook). Anything else is
                // refused with a pointer to the template rather than guessed at.
                string? refusal = BomWorkbook.Refusal(wBook, Path);
                if (refusal is not null)
                {
                    Errors.Add(refusal);
                    return;
                }

                ReadWorkbook(wBook, Path);
            }
        }

        private void ReadWorkbook(XLWorkbook wBook, string Path)
        {
            List<Part> tmpTubeList = new List<Part>();
            List<Part> tmpSheetList = new List<Part>();

            // The job block: one set of named cells per file, not carried forward per row. Units is
            // what a later change to the unit count scales each part's per-unit quantity by.
            this.Units = BomWorkbook.ReadUnits(wBook);
            this.CustomerName = BomWorkbook.ReadJob(wBook, BomWorkbook.JobCustomer);
            this.JobEst = BomWorkbook.ReadJob(wBook, BomWorkbook.JobEst);
            this.PrevJobEst = BomWorkbook.ReadJob(wBook, BomWorkbook.JobPrevEst);
            this.EngName = BomWorkbook.ReadJob(wBook, BomWorkbook.JobBy);
            this.DrawingNo = BomWorkbook.ReadJob(wBook, BomWorkbook.JobDrawing);
            this.CheckedBy = BomWorkbook.ReadJob(wBook, BomWorkbook.JobCheckedBy);
            this.Description = BomWorkbook.ReadJob(wBook, BomWorkbook.JobDescription);
            this.Date = BomWorkbook.ReadJob(wBook, BomWorkbook.JobDate);
            this.RevisionNo = BomWorkbook.ReadJob(wBook, BomWorkbook.JobRevision);

            //Check each description creating a part list and loading
            //it into a Nest1D or 2D object depending on whether the
            //width field is empty.
            try
            {
                List<BomWorkbook.PartRow> rows = BomWorkbook.ReadParts(wBook, Errors);
                if (Errors.Count > 0)
                    return;

                // Only rows that carry a length are parts to nest; a row without one is a purchased
                // item, which the purchase list picks up from the file itself (see FileWriter).
                foreach (BomWorkbook.PartRow row in rows.Where(r => r.Length > 0))
                {
                    int quantity = (int)System.Math.Round(row.Total);
                    int perUnitQuantity = (int)System.Math.Round(row.PerUnit);
                    float length = (float)row.Length;
                    float width = (float)row.Width;

                    if (width <= 0)
                    {
                        tmpTubeList.Add(new Part(row.Item, quantity, row.Description, row.PartNumber, width, length, width * length)
                            { PerUnitQuantity = perUnitQuantity });
                    }
                    else
                    {
                        tmpSheetList.Add(new Part(row.Item, quantity, row.Description, row.PartNumber, width, length, width * length)
                            { PerUnitQuantity = perUnitQuantity });
                    }
                }

                // Grouped by exact Description text, but NOT by adjacency - a real BOM (this
                // app's own round-trip through BillOfMaterials.WriteToFile aside) is laid out by
                // detail/drawing order, not by material, so the same material's rows routinely
                // land apart from each other. Tracking only "did the description just change"
                // used to split one material into several groups whenever something else was
                // interleaved between its rows, and each group then nested independently -
                // costing more stock than the combined cut list actually needed. A lookup keyed
                // on Description finds the existing group regardless of where its earlier rows
                // were, while still adding a new group the first time a description is seen, so
                // the group order matches the BOM's own first-occurrence order same as before.
                // Per-end features from the End Features sheet, when the BOM has one.
                EndFeaturesSheet.Read(wBook, tmpTubeList, Warnings);

                var tnestByDescription = new Dictionary<string, TNest>();

                foreach (var part in tmpTubeList)
                {
                    if (!tnestByDescription.TryGetValue(part.Description, out TNest? tnest))
                    {
                        tnest = new TNest() { Description = part.Description };
                        tnestByDescription.Add(part.Description, tnest);
                        this.TNestList.Add(tnest);
                    }

                    tnest.Parts.Add(part);
                }

                var pnestByDescription = new Dictionary<string, PNest>();

                foreach (var part in tmpSheetList)
                {
                    if (!pnestByDescription.TryGetValue(part.Description, out PNest? pnest))
                    {
                        pnest = new PNest()
                        {
                            Description = part.Description,
                            Policy = MaterialPolicy.FromDescription(part.Description)
                        };
                        pnestByDescription.Add(part.Description, pnest);
                        this.PNestList.Add(pnest);
                    }

                    pnest.Parts.Add(part);
                }

                AssociateDxfFiles(Path);


            }
            catch (Exception e)
            {
                // Row-level failure partway through, so PNestList/TNestList hold whatever was
                // read before it. Named as partial so a caller does not mistake a truncated
                // part list for a complete one.
                Errors.Add($"Stopped reading '{System.IO.Path.GetFileName(Path)}' partway through: {e.Message}");
            }
            //catch for last list

            System.Console.WriteLine("Parse Finished");

        }

        // Looks for a "DXF" subfolder next to the BOM and associates each sheet part with a
        // matching DXF file by part number, so it nests as its true outline instead of a
        // rectangle. Matching is per-part - a job can mix
        // DXF-sourced and rectangle-fallback parts freely.
        private void AssociateDxfFiles(string bomPath)
        {
            string? bomDir = System.IO.Path.GetDirectoryName(bomPath);
            if (string.IsNullOrEmpty(bomDir))
                return;

            // Every subfolder of the BOM's own folder, and never above it. Only a "DXF" folder
            // beside the BOM used to be searched, but jobs file their flat patterns wherever suits
            // - Programs, Programs/DXF, Programs/Router, per-thickness subfolders - and a job that
            // did anything else silently nested every part as a rectangle from its BOM dimensions.
            // Staying inside the job folder keeps one job's geometry out of another's.
            FileInfo[] dxfFiles;
            try
            {
                dxfFiles = new DirectoryInfo(bomDir).GetFiles("*.dxf", SearchOption.AllDirectories);
            }
            catch (Exception e)
            {
                Warnings.Add($"Could not search '{bomDir}' for DXF files, so every part will nest " +
                             $"as a rectangle from its BOM dimensions: {e.Message}");
                return;
            }

            if (dxfFiles.Length == 0)
                return;

            int withoutDxf = 0;
            var ambiguous = new List<string>();

            foreach (var list in this.PNestList)
            {
                foreach (var part in list.Parts)
                {
                    if (string.IsNullOrEmpty(part.PartNumber))
                        continue;

                    var matches = dxfFiles
                        .Where(f => f.Name.Contains(part.PartNumber, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matches.Count == 0)
                    {
                        withoutDxf++;
                        continue;
                    }

                    // Part numbers are matched as substrings, so "8" also matches "Item 85".
                    // Searching the whole job folder instead of one subfolder makes that more
                    // likely, so prefer a filename where the part number stands on its own rather
                    // than running into another number.
                    var delimited = matches
                        .Where(f => StandsAlone(f.Name, part.PartNumber))
                        .ToList();

                    var candidates = delimited.Count > 0 ? delimited : matches;

                    // A flat pattern and a cut program of the same name, or the same part under two
                    // thickness folders, both match. Prefer a file under a folder actually called
                    // DXF, then the shallowest, then alphabetical - so that whatever gets picked,
                    // the same file gets picked every time. A nest that changed between runs on
                    // directory ordering would be worse than one that is occasionally wrong.
                    var chosen = candidates
                        .OrderByDescending(f => IsUnderDxfFolder(f, bomDir))
                        .ThenBy(f => DepthBelow(f, bomDir))
                        .ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                        .First();

                    // Only worth raising when the candidates are actually different drawings. The
                    // same filename appearing in two places is a copy of one part - jobs routinely
                    // keep a set under DXF and another under Programs - and the choice between
                    // identical names is not a choice. Warning about those buries the case that
                    // matters, which is one part number matching two differently named outlines.
                    int distinctNames = candidates
                        .Select(f => f.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    if (distinctNames > 1)
                    {
                        ambiguous.Add($"{part.PartNumber} ({distinctNames} different files, used " +
                                      $"{System.IO.Path.GetRelativePath(bomDir, chosen.FullName)})");
                    }

                    part.hasDXF = true;
                    part.DXFPath = chosen.FullName;
                }
            }

            // Nesting a part as its bounding box rather than its outline is a legitimate fallback,
            // but it changes the sheet count, so it should not be invisible.
            if (withoutDxf > 0)
            {
                Warnings.Add($"{withoutDxf} BOM line(s) had no matching DXF and will nest as " +
                             "rectangles from their BOM dimensions.");
            }

            if (ambiguous.Count > 0)
            {
                string listed = string.Join(", ", ambiguous.Take(3));
                string more = ambiguous.Count > 3 ? $", and {ambiguous.Count - 3} more" : "";
                Warnings.Add($"{ambiguous.Count} part number(s) matched more than one DXF: " +
                             $"{listed}{more}. Check these are the intended outlines.");
            }
        }

        // True when the part number appears in the filename without another letter or digit run
        // into either end of it, so "Item 8" matches part 8 but "Item 85" does not.
        private static bool StandsAlone(string fileName, string partNumber)
        {
            int at = fileName.IndexOf(partNumber, StringComparison.OrdinalIgnoreCase);

            while (at >= 0)
            {
                bool leftClear = at == 0 || !char.IsLetterOrDigit(fileName[at - 1]);
                int end = at + partNumber.Length;
                bool rightClear = end >= fileName.Length || !char.IsLetterOrDigit(fileName[end]);

                if (leftClear && rightClear)
                    return true;

                at = fileName.IndexOf(partNumber, at + 1, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool IsUnderDxfFolder(FileInfo file, string bomDir)
        {
            var directory = file.Directory;
            string root = System.IO.Path.GetFullPath(bomDir);

            while (directory != null &&
                   !string.Equals(System.IO.Path.GetFullPath(directory.FullName), root,
                       StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(directory.Name, "DXF", StringComparison.OrdinalIgnoreCase))
                    return true;

                directory = directory.Parent;
            }

            return false;
        }

        private static int DepthBelow(FileInfo file, string bomDir) =>
            System.IO.Path.GetRelativePath(bomDir, file.FullName)
                .Count(c => c == System.IO.Path.DirectorySeparatorChar ||
                            c == System.IO.Path.AltDirectorySeparatorChar);

        public List<PNest> PNestList { get; set; }
        public List<TNest> TNestList { get; set; }
        public List<Part> SheetParts { get; set; }

        // How many units (racks) the loaded BOM's quantities were quoted for. Changing this after
        // load is what re-derives every part's quantity from PerUnitQuantity and renests.
        public int Units { get; set; } = 1;

        // The rest of the title block, read from the same fixed cells BillOfMaterials.WriteToFile
        // writes them into - see the read logic in Parse. Empty ("") for a BOM this app did not
        // write, the same fallback Units already used above.
        public string CustomerName { get; set; } = "";
        public string JobEst { get; set; } = "";
        public string PrevJobEst { get; set; } = "";
        public string EngName { get; set; } = "";
        public string DrawingNo { get; set; } = "";
        public string CheckedBy { get; set; } = "";
        public string Description { get; set; } = "";
        public string Date { get; set; } = "";
        public string RevisionNo { get; set; } = "";

    }
}
