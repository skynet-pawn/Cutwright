using System.IO;

namespace Cutwright
{
    // Every location the SolidWorks parts export reads from. The shop-specific ones come from the
    // settings file (see CutwrightSettings) and are blank until set; the ANSI profiles ship with
    // SolidWorks itself, so those are found on their own.
    internal static class SolidWorksPaths
    {
        // STEEL/STAINLESS/ALUMINUM.prtdot - see SwMaterials.TemplateFileName.
        public static string TemplateFolder => CutwrightSettings.Get(CutwrightSettings.TemplateFolder);

        // One .SLDLFP per size, named the way the BOMs name the stock ("2 x 2 x 11GA"), in a
        // subfolder per tube type.
        public static string ShopProfileFolder => CutwrightSettings.Get(CutwrightSettings.ShopProfileFolder);

        // The stock SolidWorks ANSI weldment profiles. One file per shape, every size inside it as
        // a configuration. Set in the settings file if SolidWorks is somewhere unusual; otherwise
        // looked for in the standard install folders, newest first.
        public static string AnsiProfileFolder
        {
            get
            {
                string configured = CutwrightSettings.Get(CutwrightSettings.AnsiProfileFolder);
                return configured.Length > 0 ? configured : FindAnsiProfileFolder();
            }
        }

        // The shop's bend allowance table, used for every material. Blank means K-factor is used.
        public static string BendTable => CutwrightSettings.Get(CutwrightSettings.BendTable);

        // The material library HDPE and UHMW come from, since no template carries them. See
        // SwMaterials.LibraryMaterial. Blank means those parts keep the template's material.
        public static string MaterialLibrary => CutwrightSettings.Get(CutwrightSettings.MaterialLibrary);

        // What has to be filled in for the export to build anything at all: the part templates.
        public static bool TemplatesConfigured => TemplateFolder.Length > 0;

        public static string Template(SwMaterial material) =>
            Path.Combine(TemplateFolder, SwMaterials.TemplateFileName(material));

        private const string Ansi = @"data\weldment profiles\ansi";

        private static string FindAnsiProfileFolder()
        {
            var candidates = new List<string>();

            foreach (string vendor in new[] { "SOLIDWORKS Corp", "Dassault Systemes" })
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), vendor);

                try
                {
                    if (Directory.Exists(root))
                        candidates.AddRange(Directory.GetDirectories(root, "SOLIDWORKS*")
                            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                            .Select(d => Path.Combine(d, "SOLIDWORKS", Ansi)));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return candidates.FirstOrDefault(Directory.Exists) ?? string.Empty;
        }
    }
}
