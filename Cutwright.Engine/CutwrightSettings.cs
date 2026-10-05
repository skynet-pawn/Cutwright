using System.IO;
using System.Text.Json;

namespace Cutwright
{
    // Machine-specific settings: the folders the SolidWorks parts export reads its templates,
    // profile libraries and bend table from. They used to be compiled in, pointing at one shop's
    // network drive, which made the program unusable anywhere else and carried a company's folder
    // layout around in the source.
    //
    // A small JSON file under the user's own profile, edited by hand:
    //   %LOCALAPPDATA%\Cutwright\settings.json
    // Every value is optional. A blank one means "not set up", and the feature that needs it says
    // so and names this file rather than failing on a path that was never real.
    //
    // Nothing here may throw. A settings file that is missing, unreadable or badly formed reads as
    // all-blank - the program must still start.
    internal static class CutwrightSettings
    {
        public const string TemplateFolder = nameof(TemplateFolder);
        public const string ShopProfileFolder = nameof(ShopProfileFolder);
        public const string AnsiProfileFolder = nameof(AnsiProfileFolder);
        public const string BendTable = nameof(BendTable);
        public const string MaterialLibrary = nameof(MaterialLibrary);

        // Starting figures for the Finishes sheet of a new bill of materials, as plain numbers:
        // square feet one lb of powder covers, square feet one gal of wet paint covers, and the
        // overspray/rework allowance in percent. Coverage rates depend on the product and the
        // shop, so Cutwright has none built in; a blank one leaves the cell empty for the estimator.
        public const string PowderCoverageSqFtPerLb = nameof(PowderCoverageSqFtPerLb);
        public const string WetPaintCoverageSqFtPerGal = nameof(WetPaintCoverageSqFtPerGal);
        public const string FinishWastePercent = nameof(FinishWastePercent);

        private static readonly string[] Keys =
            { TemplateFolder, ShopProfileFolder, AnsiProfileFolder, BendTable, MaterialLibrary,
              PowderCoverageSqFtPerLb, WetPaintCoverageSqFtPerGal, FinishWastePercent };

        // Keys whose value is a number, which a settings file may write bare (60) as well as quoted.
        private static readonly HashSet<string> NumericKeys = new(StringComparer.OrdinalIgnoreCase)
            { PowderCoverageSqFtPerLb, WetPaintCoverageSqFtPerGal, FinishWastePercent };

        public static string DefaultFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cutwright", "settings.json");

        private static Dictionary<string, string>? _current;

        // Read once per run; edits to the file are picked up the next time the program starts.
        public static string Get(string key)
        {
            _current ??= Load(DefaultFilePath);
            return _current.TryGetValue(key, out string? value) ? value : string.Empty;
        }

        // Every key present, blank when not set, so a caller never has to test for a missing one.
        public static Dictionary<string, string> Load(string path)
        {
            var settings = Keys.ToDictionary(k => k, _ => string.Empty, StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(path))
                    return settings;

                using var document = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return settings;

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        settings[property.Name] = property.Value.GetString() ?? string.Empty;
                    else if (property.Value.ValueKind == JsonValueKind.Number && NumericKeys.Contains(property.Name))
                        settings[property.Name] = property.Value.GetRawText();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warn($"Could not read the settings file '{path}': {ex.Message}");
            }

            return settings;
        }

        // Writes a settings file with every key blank, if there is not one yet, so there is
        // something to open and fill in. Never overwrites, and says whether a file is now there.
        public static bool EnsureFile(string? path = null)
        {
            path ??= DefaultFilePath;

            try
            {
                if (File.Exists(path))
                    return true;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var blank = Keys.ToDictionary(k => k, _ => string.Empty);
                File.WriteAllText(path, JsonSerializer.Serialize(blank, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not create the settings file '{path}': {ex.Message}");
                return false;
            }
        }

        // For tests: forget what was read so the next Get reads again.
        internal static void Reset() => _current = null;
    }
}
