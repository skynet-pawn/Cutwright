using System;
using System.IO;
using netDxf;
using netDxf.Entities;

namespace Cutwright.Tests.Support
{
    // Writes the test outlines out as real DXF files and reads them back through the production
    // DxfOutlineReader, so the tests exercise the same path a job does rather than hand-built
    // PartGeometry objects. Outlines go out as loose lines, which is what a real flat-pattern
    // export looks like, so the reader's endpoint chaining is on the hook too.
    //
    // One temp directory per test class, cleaned up afterwards.
    internal sealed class TestShapes : IDisposable
    {
        private readonly string directory;

        public TestShapes()
        {
            directory = Path.Combine(Path.GetTempPath(), "Cutwright.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        // 12 x 12 with a 2in leg. The textbook interleave: a 180-degree copy slides in along one
        // axis and the pair fits in about 14in rather than 24in.
        public static readonly (double X, double Y)[] LBracket =
        {
            (0, 0), (12, 0), (12, 2), (2, 2), (2, 12), (0, 12)
        };

        // 12 x 8 with 2in walls. Its walls are on the outside, so no flush slide interleaves it -
        // this is the shape that needs the off-axis sweep.
        public static readonly (double X, double Y)[] UChannel =
        {
            (0, 0), (12, 0), (12, 8), (10, 8), (10, 2), (2, 2), (2, 8), (0, 8)
        };

        // Right trapezoid, parallel sides 12 and 6. Mates with a 180-degree copy along the slant.
        public static readonly (double X, double Y)[] Trapezoid =
        {
            (0, 0), (12, 0), (6, 8), (0, 8)
        };

        // Two offset steps rather than one notch, so the flush search has to prove itself against
        // an asymmetric silhouette instead of the L's single simple corner.
        public static readonly (double X, double Y)[] ZShape =
        {
            (0, 0), (8, 0), (8, 4), (14, 4), (14, 12), (6, 12), (6, 8), (0, 8)
        };

        // A stem with two notches on the same side, unlike the L's one notch or the U's two facing
        // walls - a different concave family for the search to prove itself against.
        public static readonly (double X, double Y)[] TShape =
        {
            (0, 0), (14, 0), (14, 4), (10, 4), (10, 12), (4, 12), (4, 4), (0, 4)
        };

        // An L-bracket with the leg width as a parameter, so a Theory can sweep concavity - how
        // big a bite the notch takes out of the bounding box - instead of trusting the single
        // hand-picked ratio LBracket above to stand in for every real L.
        public static (double X, double Y)[] LBracketWithLeg(double size, double leg) => new[]
        {
            (0, 0), (size, 0), (size, leg), (leg, leg), (leg, size), (0, size)
        };

        // Control: must never be treated as a candidate for interleaving.
        public static readonly (double X, double Y)[] Rectangle =
        {
            (0, 0), (12, 0), (12, 8), (0, 8)
        };

        // Control: handled by the exact gusset pairing, not by search.
        public static readonly (double X, double Y)[] RightTriangle =
        {
            (0, 0), (12, 0), (0, 8)
        };

        public string Write(string name, (double X, double Y)[] outline)
        {
            string path = Path.Combine(directory, name + ".dxf");

            var doc = new DxfDocument();
            doc.DrawingVariables.InsUnits = netDxf.Units.DrawingUnits.Inches;

            for (int i = 0; i < outline.Length; i++)
            {
                var p1 = outline[i];
                var p2 = outline[(i + 1) % outline.Length];
                doc.Entities.Add(new Line(new Vector2(p1.X, p1.Y), new Vector2(p2.X, p2.Y)));
            }

            doc.Save(path);
            return path;
        }

        public PartGeometry Geometry(string name, (double X, double Y)[] outline) =>
            PartGeometry.FromDxf(Write(name, outline));

        // Writes an outer loop plus one or more interior loops (holes), all as loose lines the way
        // a real flat-pattern export looks - the outer/hole split is by area, not by draw order, so
        // the outer just needs to be the largest.
        public string WriteWithHoles(string name, (double X, double Y)[] outer, params (double X, double Y)[][] holes)
        {
            string path = Path.Combine(directory, name + ".dxf");

            var doc = new DxfDocument();
            doc.DrawingVariables.InsUnits = netDxf.Units.DrawingUnits.Inches;

            void AddLoop((double X, double Y)[] loop)
            {
                for (int i = 0; i < loop.Length; i++)
                {
                    var p1 = loop[i];
                    var p2 = loop[(i + 1) % loop.Length];
                    doc.Entities.Add(new Line(new Vector2(p1.X, p1.Y), new Vector2(p2.X, p2.Y)));
                }
            }

            AddLoop(outer);
            foreach (var hole in holes)
                AddLoop(hole);

            doc.Save(path);
            return path;
        }

        public Part DxfPart(int line, int quantity, string partNumber, string path, float width, float length)
        {
            var part = new Part(line, quantity, "TEST PLATE", partNumber, width, length, width * length);
            part.hasDXF = true;
            part.DXFPath = path;
            return part;
        }

        public static Part PlainPart(int line, int quantity, string partNumber, float width, float length) =>
            new Part(line, quantity, "TEST PLATE", partNumber, width, length, width * length);

        public string NewSubdirectory(string name)
        {
            string path = Path.Combine(directory, name);
            Directory.CreateDirectory(path);
            return path;
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
                // A leftover temp directory is not worth failing a test run over.
            }
        }
    }
}
