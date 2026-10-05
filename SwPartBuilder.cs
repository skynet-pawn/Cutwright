using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Cutwright
{
    // What happened to one part. Built is false when nothing was saved; Message says why, or where
    // it went.
    internal sealed record SwBuildResult(bool Built, string Message, IReadOnlyList<string> Notes);

    // Builds one plain, featureless part from an SwPartJob and saves it. Call only on the thread
    // SolidWorksSession.Run provides.
    //
    //   Sheet part  - a sheet metal base flange: a Length x Width rectangle on the Top plane at the
    //                 part's thickness, bends from the shop's bend table.
    //   Stick part  - a weldment: one structural member along a line of the part's length, from the
    //                 shop or ANSI profile SwProfileMatcher chose. Or, when neither library has the
    //                 size, the cross-section sketched from the callout and extruded to length.
    //
    // Every part gets the custom properties Description and LENGTH, and sheet parts
    // WIDTH too. Material comes from the shop template the part starts from, or for HDPE and UHMW
    // from the shop material library.
    internal sealed class SwPartBuilder
    {
        private const double Meters = 0.0254;

        // Used when the shop bend table is not reachable, or does not cover a part's thickness.
        public const double FallbackKFactor = 0.42;
        public const double BendRadiusInches = 0.125;

        // The inside fillet of a sketched angle, as a multiple of its thickness - the sharp-heel,
        // radiused-root shape of hot-rolled angle, close enough for a plain part.
        private const double AngleRootRadius = 1.0;

        // How much short of the full thickness a sketched angle's toe round-over stops, in inches -
        // 3/16 angle rounds over at 0.1874.
        private const double ToeRoundOverShort = 0.0001;

        private readonly ISldWorks app;

        // The part being built right now, so a failure can close it.
        private ModelDoc2? current;

        public SwPartBuilder(ISldWorks app) => this.app = app;

        public SwBuildResult Build(SwPartJob job, string outputFolder)
        {
            if (!job.CanBuild)
                return new SwBuildResult(false, job.Problem!, Array.Empty<string>());

            if (!SolidWorksPaths.TemplatesConfigured)
                return new SwBuildResult(false,
                    $"no part template folder is set (TemplateFolder in {CutwrightSettings.DefaultFilePath})", Array.Empty<string>());

            string template = SolidWorksPaths.Template(job.Material!.Value);
            if (!File.Exists(template))
                return new SwBuildResult(false, $"part template not found: {template}", Array.Empty<string>());

            string path = Path.Combine(outputFolder, job.FileName + ".SLDPRT");
            var notes = new List<string>();

            try
            {
                if (job.Kind == SwPartKind.Sheet)
                    return BuildSheet(job, template, path, notes);

                return job.Profile!.Source == SwProfileSource.Sketch
                    ? BuildSketchedStick(job, template, path, notes)
                    : BuildWeldment(job, template, path, notes);
            }
            // One bad part must not end the batch - it is reported on its own row and the rest carry
            // on. Whatever document it left open is closed so it does not pile up.
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                          or InvalidOperationException or NullReferenceException or InvalidCastException)
            {
                CloseStray(path);
                return new SwBuildResult(false, $"SolidWorks error: {ex.Message}", notes);
            }
        }

        // --- Sheet parts ------------------------------------------------------------------------

        private SwBuildResult BuildSheet(SwPartJob job, string template, string path, List<string> notes)
        {
            bool tableAvailable = File.Exists(SolidWorksPaths.BendTable);
            if (!tableAvailable)
                notes.Add($"bend table not found - used K-factor {FallbackKFactor}");

            if (tableAvailable)
            {
                ModelDoc2 doc = NewPart(template);
                if (TryBaseFlange(doc, job, useBendTable: true))
                    return Finish(doc, job, path, notes);

                Discard(doc);
                notes.Add($"bend table does not cover {job.Thickness:0.####} thick - used K-factor {FallbackKFactor}");
            }

            ModelDoc2 retry = NewPart(template);
            if (TryBaseFlange(retry, job, useBendTable: false))
                return Finish(retry, job, path, notes);

            Discard(retry);
            return new SwBuildResult(false, "SolidWorks did not create the sheet metal base flange", notes);
        }

        private bool TryBaseFlange(ModelDoc2 doc, SwPartJob job, bool useBendTable)
        {
            Feature sketch = Sketch(doc, Plane(doc, 1), sm =>
            {
                object[] lines = (object[])sm.CreateCornerRectangle(0, 0, 0, job.Length * Meters, job.Width * Meters, 0);
                DimensionRectangle(doc, lines);
            });

            sketch.Select2(false, 0);

            FeatureManager fm = doc.FeatureManager;
            CustomBendAllowance allowance = fm.CreateCustomBendAllowance();
            if (useBendTable)
            {
                allowance.Type = (int)swBendAllowanceTypes_e.swBendAllowanceBendTable;
                allowance.BendTableFile = SolidWorksPaths.BendTable;
            }
            else
            {
                allowance.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
                allowance.KFactor = FallbackKFactor;
            }

            // The same definition an earlier recorded macro proved out on this SolidWorks.
            var data = (BaseFlangeFeatureData)fm.CreateDefinition((int)swFeatureNameID_e.swFmBaseFlange);
            data.Initialize(false, true, allowance, true, 1, true, 0.5, 0.0001, 0.0001);
            data.BendRadius = BendRadiusInches * Meters;
            data.Thickness = job.Thickness!.Value * Meters;
            data.ReverseDirection = false;
            data.ReverseThickness = false;

            Feature? flange = fm.CreateFeature(data) as Feature;
            doc.ClearSelection2(true);

            return flange is not null && !HasError(flange) && HasSolidBody(doc);
        }

        // --- Stick parts from a library profile -------------------------------------------------

        private SwBuildResult BuildWeldment(SwPartJob job, string template, string path, List<string> notes)
        {
            ModelDoc2 doc = NewPart(template);
            SketchSegment? line = null;

            Feature sketch = Sketch(doc, Plane(doc, 0), sm =>
            {
                line = sm.CreateLine(0, 0, 0, job.Length * Meters, 0, 0);
                DimensionSegment(doc, line, job.Length * Meters / 2, -0.02);
            });

            FeatureManager fm = doc.FeatureManager;
            fm.InsertWeldmentFeature();

            // Both arrays must be DispatchWrapper[], not object[]. Given a plain object[] SolidWorks
            // quietly returns no member at all - no error, no exception - which is the same wall the
            // earlier Python version hit and routed around through a VBA macro.
            StructuralMemberGroup group = fm.CreateStructuralMemberGroup();
            group.Segments = new DispatchWrapper[] { new(line!) };
            group.ApplyCornerTreatment = false;
            group.GapWithinGroup = 0;
            group.GapForOtherGroups = 0;
            group.Angle = 0;

            SwProfile profile = job.Profile!;
            Feature? member = fm.InsertStructuralWeldment5(profile.Path, 1, true, new DispatchWrapper[] { new(group) },
                profile.Configuration ?? string.Empty) as Feature;
            doc.ClearSelection2(true);

            if (member is null || HasError(member) || !HasSolidBody(doc))
            {
                Discard(doc);
                return new SwBuildResult(false, $"SolidWorks did not create the structural member ({profile.Describe()})", notes);
            }

            if (profile.Wall is double wall)
                notes.Add($"ANSI wall {wall:0.####} used for the {job.Thickness:0.####} called out");

            return Finish(doc, job, path, notes);
        }

        // --- Stick parts sketched from the callout ----------------------------------------------

        private SwBuildResult BuildSketchedStick(SwPartJob job, string template, string path, List<string> notes)
        {
            MaterialSpec shape = job.Shape!;
            double[] s = shape.Section.Select(v => (double)v * Meters).OrderByDescending(v => v).ToArray();
            double t = (job.Thickness ?? 0) * Meters;

            ModelDoc2 doc = NewPart(template);

            // The Right plane's normal is the model X axis, so the extrusion runs the same way a
            // weldment member's line does.
            Feature sketch = Sketch(doc, Plane(doc, 2), sm =>
            {
                switch (shape.Form)
                {
                    case StockForm.SquareTube or StockForm.RectangularTube:
                    {
                        double a = s[0] / 2, b = (s.Length > 1 ? s[1] : s[0]) / 2;
                        double outer = Math.Min(2 * t, Math.Min(a, b));
                        RoundedRectangle(sm, a, b, outer);
                        RoundedRectangle(sm, a - t, b - t, Math.Max(outer - t, 0));
                        break;
                    }
                    case StockForm.RoundTube:
                        sm.CreateCircleByRadius(0, 0, 0, s[0] / 2);
                        sm.CreateCircleByRadius(0, 0, 0, s[0] / 2 - t);
                        break;
                    case StockForm.Angle:
                        Angle(sm, s[0], s.Length > 1 ? s[1] : s[0], t);
                        break;
                    case StockForm.FlatBar:
                        sm.CreateCenterRectangle(0, 0, 0, s[0] / 2, t / 2, 0);
                        break;
                    case StockForm.SquareBar:
                        sm.CreateCenterRectangle(0, 0, 0, s[0] / 2, (s.Length > 1 ? s[1] : s[0]) / 2, 0);
                        break;
                    case StockForm.Rod:
                        sm.CreateCircleByRadius(0, 0, 0, s[0] / 2);
                        break;
                }
            });

            sketch.Select2(false, 0);
            Feature? extrude = doc.FeatureManager.FeatureExtrusion3(true, false, false,
                (int)swEndConditions_e.swEndCondBlind, 0, job.Length * Meters, 0,
                false, false, false, false, 0, 0, false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            doc.ClearSelection2(true);

            if (extrude is null || HasError(extrude) || !HasSolidBody(doc))
            {
                Discard(doc);
                return new SwBuildResult(false, "SolidWorks did not extrude the sketched cross-section", notes);
            }

            notes.Add("cross-section sketched - size is in neither profile library");
            return Finish(doc, job, path, notes);
        }

        // A rectangle with rounded corners, centered on the origin, a and b its half-width and
        // half-height. Corners are left square when radius is zero.
        private static void RoundedRectangle(SketchManager sm, double a, double b, double radius)
        {
            if (radius <= 0)
            {
                sm.CreateCenterRectangle(0, 0, 0, a, b, 0);
                return;
            }

            double r = radius;
            sm.CreateLine(-a + r, -b, 0, a - r, -b, 0);
            sm.CreateArc(a - r, -b + r, 0, a - r, -b, 0, a, -b + r, 0, 1);
            sm.CreateLine(a, -b + r, 0, a, b - r, 0);
            sm.CreateArc(a - r, b - r, 0, a, b - r, 0, a - r, b, 0, 1);
            sm.CreateLine(a - r, b, 0, -a + r, b, 0);
            sm.CreateArc(-a + r, b - r, 0, -a + r, b, 0, -a, b - r, 0, 1);
            sm.CreateLine(-a, b - r, 0, -a, -b + r, 0);
            sm.CreateArc(-a + r, -b + r, 0, -a, -b + r, 0, -a + r, -b, 0, 1);
        }

        // An L with its heel at the origin: leg a along X, leg b along Y, thickness t. A sharp
        // outside corner, an inside fillet of AngleRootRadius x t at the root, and the inside corner
        // at the end of each leg rounded over.
        //
        // The toe round-over is a hair under the thickness rather than all of it - rounding the
        // full thickness would leave a zero-length end face, which SolidWorks rejects. The end
        // face is left ToeRoundOverShort tall instead.
        private static void Angle(SketchManager sm, double a, double b, double t)
        {
            double r = Math.Min(AngleRootRadius * t, Math.Min(a, b) - t);
            double c = t + r;

            // Only where the leg is long enough to hold both the root fillet and the round-over.
            double toe = t - ToeRoundOverShort * Meters;
            double toeA = a - toe >= c ? toe : 0;
            double toeB = b - toe >= c ? toe : 0;

            sm.CreateLine(0, 0, 0, a, 0, 0);
            sm.CreateLine(a, 0, 0, a, t - toeA, 0);
            if (toeA > 0)
                sm.CreateArc(a - toeA, t - toeA, 0, a, t - toeA, 0, a - toeA, t, 0, 1);
            sm.CreateLine(a - toeA, t, 0, c, t, 0);
            sm.CreateArc(c, c, 0, c, t, 0, t, c, 0, -1);
            sm.CreateLine(t, c, 0, t, b - toeB, 0);
            if (toeB > 0)
                sm.CreateArc(t - toeB, b - toeB, 0, t, b - toeB, 0, t - toeB, b, 0, 1);
            sm.CreateLine(t - toeB, b, 0, 0, b, 0);
            sm.CreateLine(0, b, 0, 0, 0, 0);
        }

        // --- Shared steps -----------------------------------------------------------------------

        private ModelDoc2 NewPart(string template)
        {
            current = app.NewDocument(template, 0, 0, 0) as ModelDoc2
                ?? throw new InvalidOperationException($"SolidWorks could not start a part from {template}");
            return current;
        }

        // The template's reference planes in feature-tree order - Front, Top, Right - found by type
        // rather than by name, so a template that renames them still works.
        private static Feature Plane(ModelDoc2 doc, int index)
        {
            var planes = new List<Feature>();
            for (var f = doc.FirstFeature() as Feature; f is not null && planes.Count < 3; f = f.GetNextFeature() as Feature)
            {
                if (f.GetTypeName2() == "RefPlane")
                    planes.Add(f);
            }

            return planes.Count > index
                ? planes[index]
                : throw new InvalidOperationException("the part template has fewer than three reference planes");
        }

        // Opens a sketch on a plane, draws into it, closes it, and returns it as a feature.
        //
        // Entities go straight into the sketch (AddToDB) with no snapping or inferencing. Every
        // endpoint is placed exactly on the next one's start, which is all a closed profile needs -
        // letting SolidWorks infer instead added a stray point to a rounded rectangle and left it
        // open, so it would not extrude.
        private static Feature Sketch(ModelDoc2 doc, Feature plane, Action<SketchManager> draw)
        {
            doc.ClearSelection2(true);
            plane.Select2(false, 0);

            SketchManager sm = doc.SketchManager;
            sm.InsertSketch(true);
            sm.AddToDB = true;
            sm.DisplayWhenAdded = false;

            try
            {
                draw(sm);
            }
            finally
            {
                sm.AddToDB = false;
                sm.DisplayWhenAdded = true;
            }

            var sketch = (Feature)sm.ActiveSketch;
            sm.InsertSketch(true);
            doc.ClearSelection2(true);
            return sketch;
        }

        // Dimensions a corner rectangle's bottom and right edges, so the part can be resized by
        // editing a number. Cosmetic - a failure here does not stop the build.
        private static void DimensionRectangle(ModelDoc2 doc, object[] lines)
        {
            foreach (SketchLine line in lines.OfType<SketchLine>())
            {
                var start = (SketchPoint)line.GetStartPoint2();
                var end = (SketchPoint)line.GetEndPoint2();
                bool horizontal = Math.Abs(start.Y - end.Y) < 1e-9;

                if (horizontal && Math.Abs(start.Y) < 1e-9)
                    DimensionSegment(doc, (SketchSegment)line, (start.X + end.X) / 2, -0.02);
                else if (!horizontal && Math.Abs(start.X) > 1e-9)
                    DimensionSegment(doc, (SketchSegment)line, start.X + 0.02, (start.Y + end.Y) / 2);
            }
        }

        private static void DimensionSegment(ModelDoc2 doc, SketchSegment segment, double x, double y)
        {
            doc.ClearSelection2(true);
            if (segment.Select4(false, null))
                doc.AddDimension2(x, y, 0);
            doc.ClearSelection2(true);
        }

        private static bool HasError(Feature feature) =>
            feature.GetErrorCode2(out bool isWarning) != 0 && !isWarning;

        private static bool HasSolidBody(ModelDoc2 doc) =>
            doc is PartDoc part
            && part.GetBodies2((int)swBodyType_e.swSolidBody, true) is object[] { Length: > 0 };

        private SwBuildResult Finish(ModelDoc2 doc, SwPartJob job, string path, List<string> notes)
        {
            SetMaterial(doc, job, notes);
            SetProperties(doc, job);
            doc.ForceRebuild3(false);

            int errors = 0, warnings = 0;
            bool saved = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);

            Discard(doc);

            return saved
                ? new SwBuildResult(true, path, notes)
                : new SwBuildResult(false, $"could not save {path} (SolidWorks error {errors}) - is it open in SolidWorks?", notes);
        }

        // Swaps the template's material for the shop library's, for the materials no template
        // carries (HDPE, UHMW). Checked afterwards, since SolidWorks quietly keeps the old material
        // when it cannot find the new one.
        private static void SetMaterial(ModelDoc2 doc, SwPartJob job, List<string> notes)
        {
            if (SwMaterials.LibraryMaterial(job.Material!.Value) is not string name || doc is not PartDoc part)
                return;

            if (!File.Exists(SolidWorksPaths.MaterialLibrary))
            {
                notes.Add($"material library not found - left as {part.GetMaterialPropertyName2("", out _)}");
                return;
            }

            part.SetMaterialPropertyName2("", SolidWorksPaths.MaterialLibrary, name);

            string applied = part.GetMaterialPropertyName2("", out _);
            if (!string.Equals(applied, name, StringComparison.OrdinalIgnoreCase))
                notes.Add($"could not set material {name} - left as {applied}");
        }

        // Written to the file and to its configuration both. The shop's property sheet edits the
        // configuration's copy, and drawings and BOM tables read whichever one they were set up
        // for.
        private static void SetProperties(ModelDoc2 doc, SwPartJob job)
        {
            var values = new List<(string Name, string Value)>
            {
                ("Description", job.Description),
                ("LENGTH", Inches(job.Length))
            };

            if (job.Kind == SwPartKind.Sheet)
                values.Add(("WIDTH", Inches(job.Width)));

            string configuration = doc.ConfigurationManager.ActiveConfiguration?.Name ?? string.Empty;

            foreach (string scope in new[] { string.Empty, configuration }.Distinct())
            {
                CustomPropertyManager properties = doc.Extension.get_CustomPropertyManager(scope);
                foreach (var (name, value) in values)
                {
                    properties.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value,
                        (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                }
            }
        }

        private static string Inches(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        private void Discard(ModelDoc2 doc)
        {
            string name = doc.GetPathName();
            app.CloseDoc(string.IsNullOrEmpty(name) ? doc.GetTitle() : name);

            if (ReferenceEquals(doc, current))
                current = null;
        }

        // After a failure partway through a part, closes whatever it left open.
        private void CloseStray(string path)
        {
            try
            {
                if (current is not null)
                    Discard(current);
                else if (File.Exists(path))
                    app.CloseDoc(path);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Already gone.
            }

            current = null;
        }
    }
}
