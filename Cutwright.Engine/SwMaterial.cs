namespace Cutwright
{
    // The materials a generated SolidWorks part can be. The metals each have a part template of
    // their own (which carries the SolidWorks material and the shop's custom property sheet) and a
    // gauge table. The plastics have neither: they start from the steel template, for its property
    // sheet, and have their material swapped for the shop material library's.
    internal enum SwMaterial
    {
        Steel,
        Stainless,
        Aluminum,
        HDPE,
        UHMW
    }

    internal static class SwMaterials
    {
        // Read off the description the same way the rest of Cutwright reads a grade: "Aluminum" or
        // "Alum" anywhere means aluminum, "Stainless" or a whole-word "SS" means stainless, "HDPE"
        // and "UHMW" mean those plastics, and plain mild steel ("HR", or nothing at all) means
        // steel. CalloutTranslator.Grade already encodes exactly that rule (plus a few synonyms -
        // 304/316, AL/6061/5052), so this reuses it rather than keeping a second copy that could
        // drift.
        //
        // null for anything else - Delrin, say - which has nothing to build it from.
        public static SwMaterial? FromDescription(string description) =>
            CalloutTranslator.Grade(description, null) switch
            {
                "HR" => SwMaterial.Steel,
                "Stainless" => SwMaterial.Stainless,
                "Alum" => SwMaterial.Aluminum,
                "HDPE" => SwMaterial.HDPE,
                "UHMW" => SwMaterial.UHMW,
                _ => null
            };

        public static bool IsPlastic(SwMaterial material) =>
            material is SwMaterial.HDPE or SwMaterial.UHMW;

        // The shop part template for a material, under SolidWorksPaths.TemplateFolder. Plastics use
        // the steel one - see LibraryMaterial.
        public static string TemplateFileName(SwMaterial material) => material switch
        {
            SwMaterial.Stainless => "STAINLESS.prtdot",
            SwMaterial.Aluminum => "ALUMINUM.prtdot",
            _ => "STEEL.prtdot"
        };

        // The material to set from the shop library (SolidWorksPaths.MaterialLibrary) in place of
        // the template's own, by its name there. null when the template's material is already right.
        public static string? LibraryMaterial(SwMaterial material) => material switch
        {
            SwMaterial.HDPE => "HDPE",
            SwMaterial.UHMW => "UHMW",
            _ => null
        };
    }
}
