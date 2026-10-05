using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Cutwright
{
    // One size a weldment profile library can build. Section is largest first; Wall is null for a
    // shape named by designation or schedule instead (pipe, AISC). Configuration is null for a shop
    // profile, which is one size per file.
    internal sealed record SwProfileEntry(
        StockForm Form,
        double[] Section,
        double? Wall,
        string? Designation,
        int? Schedule,
        bool Aluminum,
        string Path,
        string? Configuration);

    // Reads the two profile libraries into SwProfileEntry lists. Pure except for listing the shop
    // folder - the ANSI configuration names have to be read through SolidWorks, so they come in as
    // plain strings and are parsed here.
    internal static class SwProfileLibrary
    {
        // --- Shop library -----------------------------------------------------------------------

        // Subfolder -> the forms its files hold. Square and rectangular tube are matched as one
        // family (see SwProfileMatcher), so either folder serves both.
        private static readonly (string Folder, StockForm Form, int Dims)[] ShopFolders =
        {
            ("SQ Tube", StockForm.SquareTube, 2),
            ("Rect Tube", StockForm.RectangularTube, 2),
            ("Round Tube", StockForm.RoundTube, 1)
        };

        public static List<SwProfileEntry> LoadShop(string root)
        {
            var entries = new List<SwProfileEntry>();

            foreach (var (folder, form, dims) in ShopFolders)
            {
                string dir = System.IO.Path.Combine(root, folder);
                if (!Directory.Exists(dir))
                    continue;

                foreach (string file in Directory.EnumerateFiles(dir, "*.sldlfp"))
                {
                    // Office-style lock files left by an open profile - "~$0.75 x 14GA.sldlfp".
                    if (System.IO.Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal))
                        continue;

                    if (ParseShopName(System.IO.Path.GetFileNameWithoutExtension(file), form, dims) is var (section, wall))
                        entries.Add(new SwProfileEntry(form, section, wall, null, null, false, file, null));
                }
            }

            return entries;
        }

        // "2 x 2 x 11GA", "3 x 2 x 0.1875", "1.5 x 11GA". A gauge here is steel gauge - the shop
        // library is steel stock.
        internal static (double[] Section, double Wall)? ParseShopName(string name, StockForm form, int dims)
        {
            string[] parts = name.Split('x', 'X').Select(p => p.Trim()).ToArray();
            if (parts.Length != dims + 1)
                return null;

            var section = new double[dims];
            for (int i = 0; i < dims; i++)
            {
                if (!TryNumber(parts[i], out section[i]))
                    return null;
            }

            double wall;
            var gauge = Regex.Match(parts[^1], @"^(\d+)\s*GA$", RegexOptions.IgnoreCase);
            if (gauge.Success)
            {
                if (SheetGauge.Inches(int.Parse(gauge.Groups[1].Value, CultureInfo.InvariantCulture), SwMaterial.Steel) is not double inches)
                    return null;
                wall = inches;
            }
            else if (!TryNumber(parts[^1], out wall))
            {
                return null;
            }

            return (section.OrderByDescending(v => v).ToArray(), wall);
        }

        // --- ANSI library -----------------------------------------------------------------------

        // The ANSI files worth reading, and how each one names its sizes. Left out on purpose:
        // "square hss.sldlfp", which despite its name holds aluminum channels, and the shapes no
        // BOM callout reads as (T, Z, I-beam, ST/WT/MT sections).
        private sealed record AnsiFile(string FileName, StockForm Form, bool Aluminum, Regex Pattern);

        private const string N = @"(\d*\.?\d+)";

        private static readonly AnsiFile[] AnsiFiles =
        {
            new("tube square.sldlfp", StockForm.SquareTube, false, new($@"^TS{N}x{N}x{N}$")),
            new("tube rectangular.sldlfp", StockForm.RectangularTube, false, new($@"^TR{N}x{N}x{N}$")),
            new("l angle.sldlfp", StockForm.Angle, false, new($@"^L{N}x{N}x{N}$")),
            new("pipe standard s40.sldlfp", StockForm.Pipe, false, new($@"^PIPE {N} SCH (?<sch>40)$")),
            new("pipe x strong s80.sldlfp", StockForm.Pipe, false, new($@"^PIPE XS {N} SCH (?<sch>80)$")),
            new("c channel.sldlfp", StockForm.AiscSection, false, new(@"^(?<des>C[\d.]+x[\d.]+)$")),
            new("w section.sldlfp", StockForm.AiscSection, false, new(@"^(?<des>W[\d.]+x[\d.]+)$")),
            new("s section.sldlfp", StockForm.AiscSection, false, new(@"^(?<des>S[\d.]+x[\d.]+)$")),
            new("m section.sldlfp", StockForm.AiscSection, false, new(@"^(?<des>M[\d.]+x[\d.]+)$")),

            new("al tube square.sldlfp", StockForm.SquareTube, true,
                new($@"^Al TUBE {N} SQR x {N} WALL$", RegexOptions.IgnoreCase)),
            new("al tube rectangular.sldlfp", StockForm.RectangularTube, true,
                new($@"^AL TUBE {N} x {N} RECT x {N} WALL$", RegexOptions.IgnoreCase)),
            new("al round tubing.sldlfp", StockForm.RoundTube, true,
                new($@"^Al RND TUBE {N} OD x {N} Wall$", RegexOptions.IgnoreCase)),
            new("al ls angle squared ends.sldlfp", StockForm.Angle, true,
                new($@"^Al LS ANGLE SQR END {N}\s*x\s*{N}\s*x\s*{N}$", RegexOptions.IgnoreCase)),
            new("al l angle rounded ends.sldlfp", StockForm.Angle, true,
                new($@"^Al L ANGLE RND END {N}\s*x\s*{N}\s*x\s*{N}$", RegexOptions.IgnoreCase)),
            new("al pipe structural.sldlfp", StockForm.Pipe, true,
                new($@"^Al PIPE STRUCTURAL {N}\s+S(?<sch>\d+)$", RegexOptions.IgnoreCase)),
            new("al cs.sldlfp", StockForm.AiscSection, true,
                new(@"^Al CHANNEL (?<des>C[\d.]+x[\d.]+)$", RegexOptions.IgnoreCase))
        };

        // The ANSI files SolidWorks has to be asked about, full paths, in the order they are tried.
        public static IEnumerable<string> AnsiFilesToRead(string root) =>
            AnsiFiles.Select(f => System.IO.Path.Combine(root, f.FileName));

        // Turns one ANSI file's configuration names into entries. A name that does not fit the
        // file's pattern (a "Default" configuration, say) is skipped.
        public static List<SwProfileEntry> ParseAnsi(string path, IEnumerable<string> configurations)
        {
            var entries = new List<SwProfileEntry>();
            string fileName = System.IO.Path.GetFileName(path);
            AnsiFile? file = AnsiFiles.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                return entries;

            foreach (string raw in configurations)
            {
                string config = raw.Trim();
                Match m = file.Pattern.Match(config);
                if (!m.Success)
                    continue;

                if (m.Groups["des"].Success)
                {
                    entries.Add(new SwProfileEntry(file.Form, Array.Empty<double>(), null,
                        NormalizeDesignation(m.Groups["des"].Value), null, file.Aluminum, path, raw));
                    continue;
                }

                var numbers = m.Groups.Cast<Group>().Skip(1)
                    .Where(g => g.Success && char.IsDigit(g.Name[0]))
                    .Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture))
                    .ToList();

                if (m.Groups["sch"].Success)
                {
                    entries.Add(new SwProfileEntry(file.Form, new[] { numbers[0] }, null, null,
                        int.Parse(m.Groups["sch"].Value, CultureInfo.InvariantCulture), file.Aluminum, path, raw));
                    continue;
                }

                // The last number is the wall; the rest are the section. A square tube names its
                // side once, so it is doubled to match how every other tube carries two.
                double wall = numbers[^1];
                var section = numbers.Take(numbers.Count - 1).ToList();
                if (file.Form == StockForm.SquareTube && section.Count == 1)
                    section.Add(section[0]);

                entries.Add(new SwProfileEntry(file.Form, section.OrderByDescending(v => v).ToArray(), wall,
                    null, null, file.Aluminum, path, raw));
            }

            return entries;
        }

        // "C Channel C3 x 4.1", "C3x4.1" -> "C3X4.1", so a callout and a configuration compare equal.
        internal static string NormalizeDesignation(string designation)
        {
            var m = Regex.Match(designation, @"([A-Za-z]+[\d.]+)\s*[xX×]\s*([\d.]+)\s*$");
            string text = m.Success ? m.Groups[1].Value + "x" + m.Groups[2].Value : designation;
            return Regex.Replace(text, @"\s+", string.Empty).ToUpperInvariant();
        }

        private static bool TryNumber(string text, out double value)
        {
            value = CalloutTranslator.Value(text);
            return value > 0;
        }
    }

    // Picks each stick part's cross-section: the shop library on an exact size, then ANSI on the
    // same outside size with the nearest wall within 10 thou, then a sketch. Pipe and AISC shapes
    // have nothing to sketch from - their dimensions live in tables, not the callout - so without a
    // library match they cannot be built.
    internal static class SwProfileMatcher
    {
        // "10 thou" - how far an ANSI wall may sit from the callout's and still stand in for it.
        public const double AnsiWallTolerance = 0.010;

        // Outside dimensions and shop walls have to agree to within rounding.
        private const double Exact = 0.001;

        public static void Resolve(IEnumerable<SwPartJob> jobs, IReadOnlyList<SwProfileEntry> shop,
            IReadOnlyList<SwProfileEntry> ansi)
        {
            foreach (SwPartJob job in jobs)
            {
                if (job.Kind != SwPartKind.Stick || job.Shape is null || job.Problem is not null)
                    continue;

                job.Profile = Choose(job, shop, ansi);

                if (job.Profile is null)
                {
                    job.Problem = job.Shape.Form == StockForm.Pipe
                        ? "no pipe profile for this size and schedule"
                        : "no library profile for this shape";
                    job.Include = false;
                }
            }
        }

        internal static SwProfile? Choose(SwPartJob job, IReadOnlyList<SwProfileEntry> shop,
            IReadOnlyList<SwProfileEntry> ansi)
        {
            MaterialSpec shape = job.Shape!;
            bool aluminum = job.Material == SwMaterial.Aluminum;

            // Both libraries are metal stock. A plastic rod, bar or tube is always drawn from its
            // own callout.
            if (job.Material is SwMaterial m && SwMaterials.IsPlastic(m))
                return CanSketch(shape.Form, job.Thickness) ? new SwProfile(SwProfileSource.Sketch, null, null, null) : null;
            double[] section = shape.Section.Select(v => (double)v).OrderByDescending(v => v).ToArray();

            // A square tube's callout names its side once; carry two like every library entry.
            if (shape.Form == StockForm.SquareTube && section.Length == 1)
                section = new[] { section[0], section[0] };

            if (shape.Form is StockForm.Pipe)
            {
                var pipe = ansi.FirstOrDefault(e => e.Form == StockForm.Pipe && e.Aluminum == aluminum
                    && e.Schedule == shape.Schedule && SameSection(e.Section, section));
                return pipe is null ? null : new SwProfile(SwProfileSource.Ansi, pipe.Path, pipe.Configuration, null);
            }

            if (shape.Form is StockForm.AiscSection)
            {
                string wanted = SwProfileLibrary.NormalizeDesignation(shape.Designation);
                var aisc = ansi.FirstOrDefault(e => e.Form == StockForm.AiscSection && e.Aluminum == aluminum
                    && e.Designation == wanted);
                return aisc is null ? null : new SwProfile(SwProfileSource.Ansi, aisc.Path, aisc.Configuration, null);
            }

            if (job.Thickness is double wall && Family(shape.Form) is int family)
            {
                var exact = shop.FirstOrDefault(e => Family(e.Form) == family && SameSection(e.Section, section)
                    && e.Wall is double w && Math.Abs(w - wall) <= Exact);
                if (exact is not null)
                    return new SwProfile(SwProfileSource.Shop, exact.Path, null, null);

                var nearest = ansi
                    .Where(e => Family(e.Form) == family && e.Aluminum == aluminum && SameSection(e.Section, section)
                        && e.Wall is double w && Math.Abs(w - wall) <= AnsiWallTolerance + 1e-9)
                    .OrderBy(e => Math.Abs(e.Wall!.Value - wall))
                    .FirstOrDefault();
                if (nearest is not null)
                {
                    double? usedWall = Math.Abs(nearest.Wall!.Value - wall) > Exact ? nearest.Wall : null;
                    return new SwProfile(SwProfileSource.Ansi, nearest.Path, nearest.Configuration, usedWall);
                }
            }

            return CanSketch(shape.Form, job.Thickness) ? new SwProfile(SwProfileSource.Sketch, null, null, null) : null;
        }

        // Square and rectangular tube are one family: a square is a rectangle with equal sides, and
        // CalloutTranslator decides which word to use from the numbers anyway.
        private static int? Family(StockForm form) => form switch
        {
            StockForm.SquareTube or StockForm.RectangularTube => 1,
            StockForm.RoundTube => 2,
            StockForm.Angle => 3,
            _ => null
        };

        private static bool CanSketch(StockForm form, double? wall) => form switch
        {
            StockForm.SquareTube or StockForm.RectangularTube or StockForm.RoundTube or StockForm.Angle => wall > 0,
            StockForm.FlatBar => wall > 0,
            StockForm.SquareBar or StockForm.Rod => true,
            _ => false
        };

        private static bool SameSection(double[] a, double[] b) =>
            a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= Exact);
    }
}
