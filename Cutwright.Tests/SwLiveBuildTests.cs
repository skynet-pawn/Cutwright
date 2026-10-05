using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Cutwright;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // Builds real parts in a real SolidWorks and measures what was saved. Skipped unless
    // CUTWRIGHT_SOLIDWORKS_LIVE=1, since it needs SolidWorks, the O: drive, and a few minutes - run it
    // by hand after changing SwPartBuilder:
    //
    //   set CUTWRIGHT_SOLIDWORKS_LIVE=1
    //   dotnet test Cutwright.Tests --filter SwLiveBuildTests
    //
    // Parts are left in CUTWRIGHT_SOLIDWORKS_OUT (default: %TEMP%\Cutwright-sw-live) to look at.
    public sealed class SwLiveBuildTests
    {
        private const double Meters = 0.0254;
        private readonly ITestOutputHelper output;

        public SwLiveBuildTests(ITestOutputHelper output) => this.output = output;

        private static Part Row(int det, string description, string partNumber, float length, float width = 0f) =>
            new(det, 1, description, partNumber, width, length, width * length);

        // Expected bounding box, largest dimension first, in inches.
        private sealed record Expect(Part Part, SwProfileSource? Source, params double[] Box);

        private static IEnumerable<Expect> Cases() => new[]
        {
            // The sample rack as an earlier standalone tool built it, plus the angle it skipped.
            new Expect(Row(1, "SQ Tube 2 x 2 x 11 GA HR", "9", 67), SwProfileSource.Shop, 67, 2, 2),
            new Expect(Row(3, "Rec Tube 8 x 3 x 11 GA HR", "3", 71), SwProfileSource.Shop, 71, 8, 3),
            new Expect(Row(6, "Rec Tube 3 x 2 x 11 GA HR", "6", 16), SwProfileSource.Shop, 16, 3, 2),
            new Expect(Row(8, "Rec Tube 2 x 1 x 11 GA", "8", 67), SwProfileSource.Shop, 67, 2, 1),
            new Expect(Row(10, "L-Angle 1-1/2 x 1-1/2 x 1/8 HR", "15", 9.75f), SwProfileSource.Sketch, 9.75, 1.5, 1.5),
            new Expect(Row(11, "Sheet 7 GA HR", "11", 67, 10), null, 67, 10, 0.1793),
            new Expect(Row(12, "Sheet 11 GA HR", "14a", 68, 24), null, 68, 24, 0.1196),
            new Expect(Row(14, "Sheet 14 GA HR", "12", 14, 8), null, 14, 8, 0.0747),

            // One of everything else the builder can make.
            new Expect(Row(20, "Rec Tube 4 x 2 x 7 GA HR", "X-ANSI-TUBE", 30), SwProfileSource.Ansi, 30, 4, 2),
            new Expect(Row(21, "Rec Tube 4 x 2 x 11 GA HR", "X-SKETCH-TUBE", 30), SwProfileSource.Sketch, 30, 4, 2),
            new Expect(Row(22, "Round Tube 1-3/4 x 11 GA HR", "X-SKETCH-ROUND", 20), SwProfileSource.Sketch, 20, 1.75, 1.75),
            new Expect(Row(23, "L Angle 2 x 2 x 1/4 HR", "X-ANSI-ANGLE", 12), SwProfileSource.Ansi, 12, 2, 2),
            new Expect(Row(30, "L Angle 2-1/4 x 1-3/4 x 3/16 HR", "X-SKETCH-ANGLE", 12), SwProfileSource.Sketch, 12, 2.25, 1.75),
            new Expect(Row(24, "FB 1/4 x 2 HR", "X-FLATBAR", 18), SwProfileSource.Sketch, 18, 2, 0.25),
            new Expect(Row(25, "Rod 3/4\" HR", "X-ROD", 10), SwProfileSource.Sketch, 10, 0.75, 0.75),
            new Expect(Row(26, "Pipe 1\" Sch. 40", "X-PIPE", 24), SwProfileSource.Ansi, 24, 1.315, 1.315),
            new Expect(Row(27, "C Channel C3 x 4.1 HR", "X-CHANNEL", 24), SwProfileSource.Ansi, 24, 3, 1.41),
            new Expect(Row(28, "Sheet 11 GA Alum", "X-ALUM-SHEET", 20, 10), null, 20, 10, 0.0907),
            new Expect(Row(29, "Plate 1/4\" SS", "X-SS-PLATE", 12, 6), null, 12, 6, 0.25),
            new Expect(Row(31, "Sheet 1/2 HDPE", "X-HDPE-SHEET", 24, 12), null, 24, 12, 0.5),
            new Expect(Row(32, "FB 1/2 x 2 UHMW", "X-UHMW-BAR", 18), SwProfileSource.Sketch, 18, 2, 0.5),
            new Expect(Row(33, "Rod 1\" HDPE", "X-HDPE-ROD", 10), SwProfileSource.Sketch, 10, 1, 1),
        };

        [SkippableFact]
        public void BuildsAndMeasuresEveryKindOfPart()
        {
            Skip.IfNot(System.Environment.GetEnvironmentVariable("CUTWRIGHT_SOLIDWORKS_LIVE") == "1",
                "Set CUTWRIGHT_SOLIDWORKS_LIVE=1 to drive a real SolidWorks.");
            Skip.IfNot(SolidWorksSession.IsInstalled, "SolidWorks is not installed.");

            string folder = System.Environment.GetEnvironmentVariable("CUTWRIGHT_SOLIDWORKS_OUT")
                ?? Path.Combine(Path.GetTempPath(), "Cutwright-sw-live");
            Directory.CreateDirectory(folder);
            output.WriteLine($"Output: {folder}");

            var cases = Cases().ToList();
            var failures = new List<string>();

            SolidWorksSession.Run(() =>
            {
                using var session = SolidWorksSession.Connect();
                output.WriteLine($"SolidWorks {session.Revision}");

                var jobs = SwBuildPlan.Create(cases.Select(c => c.Part));
                SwProfileMatcher.Resolve(jobs, SwProfileLibrary.LoadShop(SolidWorksPaths.ShopProfileFolder),
                    session.ReadAnsiProfiles());

                var builder = new SwPartBuilder(session.App);

                foreach (var expect in cases)
                {
                    SwPartJob job = jobs.Single(j => ReferenceEquals(j.Source, expect.Part));
                    string label = $"{job.FileName} ({job.Description})";

                    if (expect.Source is SwProfileSource source && job.Profile?.Source != source)
                        failures.Add($"{label}: expected {source}, planned {job.Profile?.Describe() ?? job.Problem}");

                    var started = DateTime.Now;
                    SwBuildResult result = builder.Build(job, folder);
                    output.WriteLine($"{label}: {(result.Built ? "built" : "FAILED")} in {(DateTime.Now - started).TotalSeconds:0.0}s " +
                        $"- {job.Profile?.Describe() ?? "sheet metal"} - {result.Message}" +
                        (result.Notes.Count > 0 ? " | " + string.Join("; ", result.Notes) : ""));

                    if (!result.Built)
                    {
                        failures.Add($"{label}: {result.Message}");
                        continue;
                    }

                    Measure(session.App, result.Message, expect, label, failures);
                }
            }).GetAwaiter().GetResult();

            Assert.True(failures.Count == 0, string.Join(System.Environment.NewLine, failures));
        }

        // Opens the real review window on the sample rack parts and waits for it to fill in its Source
        // column - the bindings, the shop library read and the ANSI lookup all together.
        [SkippableFact]
        public void ReviewWindowFillsInEveryRow()
        {
            Skip.IfNot(System.Environment.GetEnvironmentVariable("CUTWRIGHT_SOLIDWORKS_LIVE") == "1",
                "Set CUTWRIGHT_SOLIDWORKS_LIVE=1 to drive a real SolidWorks.");

            var rows = new List<string>();
            Exception? failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var window = new SolidWorksPartsWindow(Cases().Select(c => c.Part), @"C:\Jobs\Sample BOM.xlsx");
                    window.Show();

                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var deadline = DateTime.Now.AddMinutes(3);
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    timer.Tick += (_, _) =>
                    {
                        if (window.BuildButton.IsEnabled || DateTime.Now > deadline)
                        {
                            timer.Stop();
                            frame.Continue = false;
                        }
                    };
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);

                    Assert.True(window.BuildButton.IsEnabled, "profile matching never finished");
                    Assert.Equal(@"C:\Jobs\SolidWorks Parts", window.FolderTextBox.Text);

                    foreach (SwPartRow row in window.PartsGrid.Items)
                        rows.Add($"{row.FileName} | {row.Kind} | {row.Thickness} | {row.Source} | {row.Status}");

                    output.WriteLine(window.SummaryText.Text);
                    window.Close();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            foreach (string row in rows)
                output.WriteLine(row);

            if (failure is not null)
                throw new Xunit.Sdk.XunitException(failure.ToString());

            Assert.Equal(Cases().Count(), rows.Count);
            Assert.DoesNotContain(rows, r => r.Contains("| ... |"));
        }

        // Cross-section area in square inches from the callout, or null for the shapes whose area
        // lives in a table rather than the description (pipe, channel).
        private static double? ExpectedArea(Part part)
        {
            var job = SwBuildPlan.Create(new[] { part }).Single();
            double t = job.Thickness ?? 0;

            if (job.Kind == SwPartKind.Sheet)
                return job.Width * t;

            double[] s = job.Shape!.Section.Select(v => (double)v).OrderByDescending(v => v).ToArray();
            return job.Shape.Form switch
            {
                StockForm.SquareTube or StockForm.RectangularTube => 2 * t * (s[0] + (s.Length > 1 ? s[1] : s[0]) - 2 * t),
                StockForm.RoundTube => Math.PI * (Math.Pow(s[0] / 2, 2) - Math.Pow(s[0] / 2 - t, 2)),
                StockForm.Angle => t * (s[0] + (s.Length > 1 ? s[1] : s[0]) - t),
                StockForm.FlatBar => s[0] * t,
                StockForm.Rod => Math.PI * Math.Pow(s[0] / 2, 2),
                _ => null
            };
        }

        // Reopens a saved part and checks its bounding box, volume and custom properties.
        private void Measure(ISldWorks app, string path, Expect expect, string label, List<string> failures)
        {
            int errors = 0, warnings = 0;
            var doc = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);

            if (doc is null)
            {
                failures.Add($"{label}: saved file would not reopen (error {errors})");
                return;
            }

            try
            {
                var box = (double[])((PartDoc)doc).GetPartBox(true);
                double[] size = new[] { box[3] - box[0], box[4] - box[1], box[5] - box[2] }
                    .Select(v => Math.Abs(v) / Meters).OrderByDescending(v => v).ToArray();

                output.WriteLine($"    box {string.Join(" x ", size.Select(v => v.ToString("0.####")))}");

                for (int i = 0; i < expect.Box.Length; i++)
                {
                    // A few thou of slack for SolidWorks' own box padding and ANSI's nominal sizes.
                    if (Math.Abs(size[i] - expect.Box[i]) > 0.02)
                        failures.Add($"{label}: box {string.Join(" x ", size.Select(v => v.ToString("0.####")))}, " +
                            $"expected {string.Join(" x ", expect.Box)}");
                }

                // A bounding box cannot tell a hollow tube from a solid bar, so the volume is checked
                // against the cross-section the callout describes - sharp-cornered, which the
                // rounded corners of a tube come in a few percent under.
                // A sketched angle is 6 lines and 3 arcs round its outline - the root fillet and a
                // round-over at each toe - so 9 side faces and 2 ends. Square toes would make 9.
                if (expect.Source == SwProfileSource.Sketch && expect.Part.Description.Contains("Angle"))
                {
                    var body = (Body2)((object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true))[0];
                    int faces = body.GetFaceCount();
                    output.WriteLine($"    faces {faces}");
                    if (faces != 11)
                        failures.Add($"{label}: {faces} faces, expected 11 (root fillet and both toe round-overs)");
                }

                if (ExpectedArea(expect.Part) is double area)
                {
                    var mass = (MassProperty)doc.Extension.CreateMassProperty();
                    double volume = mass.Volume / Math.Pow(Meters, 3);
                    double expected = area * expect.Part.length;
                    output.WriteLine($"    volume {volume:0.###} in^3, callout {expected:0.###}");
                    if (Math.Abs(volume - expected) > 0.08 * expected)
                        failures.Add($"{label}: volume {volume:0.###} in^3, expected about {expected:0.###}");
                }

                CustomPropertyManager props = doc.Extension.get_CustomPropertyManager("");
                props.Get6("LENGTH", false, out string _, out string length, out bool _, out bool _);
                props.Get6("Description", false, out string _, out string description, out bool _, out bool _);
                if (length != expect.Part.length.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                    failures.Add($"{label}: LENGTH property is '{length}'");
                if (description != expect.Part.Description)
                    failures.Add($"{label}: Description property is '{description}'");

                string material = ((PartDoc)doc).GetMaterialPropertyName2("", out string _);
                output.WriteLine($"    material '{material}'");

                // The plastics have no template of their own - their material is set from the shop
                // library, and SolidWorks keeps the template's STEEL without complaint if that fails.
                if (SwMaterials.FromDescription(expect.Part.Description) is SwMaterial m
                    && SwMaterials.LibraryMaterial(m) is string wanted && material != wanted)
                    failures.Add($"{label}: material '{material}', expected '{wanted}'");
            }
            finally
            {
                app.CloseDoc(path);
            }
        }
    }
}
