using System.IO;

namespace Cutwright
{
    // A sheet part has a length and a width; a stick part has only a length. A row with neither is
    // purchased and never reaches here - FileParser only reads DET rows that have a length.
    internal enum SwPartKind
    {
        Sheet,
        Stick
    }

    // Where a stick part's cross-section comes from, in the order they are tried.
    internal enum SwProfileSource
    {
        // The shop's own library - one file per size, named the way our BOMs name the stock.
        Shop,

        // The stock SolidWorks ANSI library - one file per shape, each size a configuration.
        Ansi,

        // Neither library has the size, so the cross-section is sketched from the callout.
        Sketch
    }

    // How a stick part's cross-section will be built. Path and Configuration are null for Sketch.
    // Wall is the library's own wall when it differs from the callout's - an ANSI match within
    // tolerance - so the review grid can show what was actually used.
    internal sealed record SwProfile(SwProfileSource Source, string? Path, string? Configuration, double? Wall)
    {
        public string Describe() => Source switch
        {
            SwProfileSource.Shop => $"Shop: {System.IO.Path.GetFileNameWithoutExtension(Path)}",
            SwProfileSource.Ansi => Wall is double w
                ? $"ANSI: {Configuration} (wall {w:0.####})"
                : $"ANSI: {Configuration}",
            _ => "Sketch"
        };
    }

    // One SolidWorks part to be generated - one per unique part on the BOM.
    internal sealed class SwPartJob
    {
        public required Part Source { get; init; }

        public int Det => Source.line;
        public string PartNumber => (Source.PartNumber ?? string.Empty).Trim();
        public string Description => (Source.Description ?? string.Empty).Trim();

        // Without the .SLDPRT extension. Editable in the review window.
        public string FileName { get; set; } = string.Empty;

        public SwPartKind Kind { get; init; }
        public SwMaterial? Material { get; init; }

        public double Length { get; init; }
        public double Width { get; init; }

        // A sheet part's thickness, or a stick part's wall where its form has one.
        public double? Thickness { get; init; }

        // A stick part's form and section, as CalloutTranslator read it. null for sheet parts.
        public MaterialSpec? Shape { get; init; }

        // Filled in by SwProfileMatcher for stick parts.
        public SwProfile? Profile { get; set; }

        // Why this part cannot be built. null when it can.
        public string? Problem { get; set; }

        // Worth knowing but not blocking - duplicate rows folded into this one, and the like.
        public List<string> Notes { get; } = new();

        public bool Include { get; set; } = true;

        public bool CanBuild => Problem is null;
    }

    // Turns the parts of a loaded BOM into the list of SolidWorks parts to generate. Pure - nothing
    // here touches SolidWorks - so every rule is unit-testable.
    internal static class SwBuildPlan
    {
        public static List<SwPartJob> Create(IEnumerable<Part> parts)
        {
            // DET order, whatever order the nest groups handed them over in.
            var ordered = parts
                .Select((part, index) => (part, index))
                .OrderBy(p => p.part.line <= 0 ? int.MaxValue : p.part.line)
                .ThenBy(p => p.index)
                .Select(p => p.part);

            var jobs = new List<SwPartJob>();
            var byKey = new Dictionary<string, SwPartJob>(StringComparer.OrdinalIgnoreCase);

            foreach (Part part in ordered)
            {
                string key = UniquenessKey(part);

                if (byKey.TryGetValue(key, out SwPartJob? kept))
                {
                    kept.Notes.Add(SameAs(kept, part));
                    continue;
                }

                SwPartJob job = Classify(part);
                byKey[key] = job;
                jobs.Add(job);
            }

            AssignFileNames(jobs);
            CheckFileNames(jobs);

            foreach (SwPartJob job in jobs)
                job.Include = job.CanBuild;

            return jobs;
        }

        // Unique by part number. Rows with no part number are only merged when they are the same
        // description at the same size - telling two of those apart is left to whoever reviews them.
        //
        // The raw part number, not the file-name-safe one: A/B and A:B are two different parts that
        // happen to sanitize to the same file name, which CheckFileNames flags instead.
        private static string UniquenessKey(Part part)
        {
            string partNumber = (part.PartNumber ?? string.Empty).Trim();
            if (partNumber.Length > 0)
                return "PN|" + partNumber;

            return FormattableString.Invariant(
                $"DESC|{(part.Description ?? string.Empty).Trim()}|{part.length}|{part.width}");
        }

        private static string SameAs(SwPartJob kept, Part duplicate)
        {
            string det = duplicate.line > 0 ? $"DET {duplicate.line}" : "Another row";

            bool identical =
                string.Equals((duplicate.Description ?? string.Empty).Trim(), kept.Description, StringComparison.OrdinalIgnoreCase)
                && duplicate.length == kept.Source.length
                && duplicate.width == kept.Source.width;

            return identical
                ? $"{det} is the same part - built once"
                : $"{det} has the same part number but differs ({(duplicate.Description ?? "").Trim()}, " +
                  $"{duplicate.length:0.###} x {duplicate.width:0.###}) - only this row is built";
        }

        private static SwPartJob Classify(Part part)
        {
            string description = (part.Description ?? string.Empty).Trim();
            SwMaterial? material = SwMaterials.FromDescription(description);

            return part.width > 0
                ? ClassifySheet(part, description, material)
                : ClassifyStick(part, description, material);
        }

        private static SwPartJob ClassifySheet(Part part, string description, SwMaterial? material)
        {
            string? problem = null;
            double? thickness = null;

            if (material is null)
            {
                problem = $"no part template for {CalloutTranslator.Grade(description, null)}";
            }
            else if (CalloutTranslator.NamedForm(description) is StockForm named && StockForms.IsStick(named))
            {
                problem = "has a width, but the description is a stick part";
            }
            else
            {
                thickness = SheetGauge.ReadSheetThickness(description, material.Value, out problem);
            }

            if (problem is null && part.length <= 0)
                problem = "no length";

            return new SwPartJob
            {
                Source = part,
                Kind = SwPartKind.Sheet,
                Material = material,
                Length = part.length,
                Width = part.width,
                Thickness = thickness,
                Problem = problem
            };
        }

        private static SwPartJob ClassifyStick(Part part, string description, SwMaterial? material)
        {
            MaterialSpec? shape = CalloutTranslator.Read(description, null);
            double? wall = shape?.Thickness;
            string? problem = null;

            if (material is null)
            {
                problem = $"no part template for {CalloutTranslator.Grade(description, null)}";
            }
            else if (shape?.Form is StockForm.Sheet or StockForm.Plate or StockForm.ExpandedMetal
                     || CalloutTranslator.NamedForm(description) is StockForm.Sheet or StockForm.Plate)
            {
                // Checked by name as well: the shop's own "Sheet 11 GA HR" has one number, which
                // CalloutTranslator.Read does not read as flat stock at all.
                problem = "no width - a sheet part needs both a length and a width";
            }
            else if (shape is null)
            {
                problem = "description is not a recognized stick shape";
            }
            else if (!StockForms.IsStick(shape.Form) && shape.Form != StockForm.AiscSection)
            {
                problem = "description is not a recognized stick shape";
            }
            else if (shape.Form == StockForm.PvcPipe)
            {
                problem = "no part template for PVC";
            }
            else if (SheetGauge.GaugeIn(description) is int gauge && shape.Thickness is not null)
            {
                // CalloutTranslator resolves a gauge through the steel table, and turns a gauge it
                // does not know into 11 GA - right for naming stock, wrong for a model. Re-read the
                // wall against this part's own material.
                wall = SheetGauge.Inches(gauge, material.Value);
                if (wall is null)
                    problem = SheetGauge.GaugeProblem(gauge, material.Value);
            }

            if (problem is null && part.length <= 0)
                problem = "no length";

            return new SwPartJob
            {
                Source = part,
                Kind = SwPartKind.Stick,
                Material = material,
                Length = part.length,
                Thickness = wall,
                Shape = shape,
                Problem = problem
            };
        }

        // The part number when there is one. Otherwise Item#<DET> - unless the rows without a part
        // number are missing DET numbers or repeat them, in which case none of them can be trusted
        // to name a file and they are simply numbered in order instead.
        private static void AssignFileNames(List<SwPartJob> jobs)
        {
            var unnamed = new List<SwPartJob>();

            foreach (SwPartJob job in jobs)
            {
                string name = Sanitize(job.PartNumber);
                if (name.Length > 0)
                    job.FileName = name;
                else
                    unnamed.Add(job);
            }

            bool detsUsable = unnamed.All(j => j.Det > 0)
                && unnamed.Select(j => j.Det).Distinct().Count() == unnamed.Count;

            for (int i = 0; i < unnamed.Count; i++)
                unnamed[i].FileName = detsUsable ? $"Item#{unnamed[i].Det}" : $"Item#{i + 1}";
        }

        // Two parts that would save to the same file would silently overwrite each other within one
        // run - which is not the same thing as overwriting a file left over from an earlier one.
        // Flagged on every part after the first, so the reviewer can rename one. Called again by
        // the review window whenever a name is edited.
        public static void CheckFileNames(IEnumerable<SwPartJob> jobs)
        {
            const string Prefix = "same file name as ";
            var seen = new Dictionary<string, SwPartJob>(StringComparer.OrdinalIgnoreCase);

            foreach (SwPartJob job in jobs)
            {
                if (job.Problem?.StartsWith(Prefix, StringComparison.Ordinal) == true
                    || job.Problem == "no file name")
                    job.Problem = null;

                if (job.Problem is not null)
                    continue;

                string name = job.FileName.Trim();
                if (name.Length == 0)
                {
                    job.Problem = "no file name";
                    continue;
                }

                if (seen.TryGetValue(name, out SwPartJob? first))
                    job.Problem = Prefix + (first.Det > 0 ? $"DET {first.Det}" : first.Description);
                else
                    seen[name] = job;
            }
        }

        // Characters Windows will not allow in a file name become a dash.
        public static string Sanitize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            char[] invalid = Path.GetInvalidFileNameChars();
            string cleaned = new(text.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray());

            return cleaned.Trim().TrimEnd('.');
        }
    }
}
