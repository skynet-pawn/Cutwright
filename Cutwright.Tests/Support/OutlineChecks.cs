using System;
using System.Collections.Generic;
namespace Cutwright.Tests.Support
{
    // Clearance maths written from scratch for the tests, deliberately not reusing
    // PolygonDistance. The whole point of these tests is to confirm that what the nester places
    // really does clear - if they leaned on the same code the nester leans on, a bug in it would
    // confirm itself.
    //
    // Slow and obvious by design: dense sampling and a plain sign test, no cleverness to be wrong
    // about.
    internal static class OutlineChecks
    {
        // Closest approach between two closed outlines.
        public static double MinGap(List<(double X, double Y)> a, List<(double X, double Y)> b)
        {
            double best = double.MaxValue;

            for (int i = 0; i < a.Count; i++)
            {
                var a1 = a[i];
                var a2 = a[(i + 1) % a.Count];

                for (int j = 0; j < b.Count; j++)
                {
                    var b1 = b[j];
                    var b2 = b[(j + 1) % b.Count];

                    best = Math.Min(best, SampledGap(a1, a2, b1, b2));
                }
            }

            return best;
        }

        private static double SampledGap((double X, double Y) a1, (double X, double Y) a2,
            (double X, double Y) b1, (double X, double Y) b2)
        {
            const int steps = 40;
            double best = double.MaxValue;

            for (int i = 0; i <= steps; i++)
            {
                double s = i / (double)steps;
                double ax = a1.X + (a2.X - a1.X) * s;
                double ay = a1.Y + (a2.Y - a1.Y) * s;

                for (int j = 0; j <= steps; j++)
                {
                    double t = j / (double)steps;
                    double bx = b1.X + (b2.X - b1.X) * t;
                    double by = b1.Y + (b2.Y - b1.Y) * t;

                    double dx = ax - bx;
                    double dy = ay - by;
                    best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
                }
            }

            return best;
        }

        // True when an edge of one outline properly crosses an edge of the other. Touching and
        // collinear overlap are not crossings - parts may butt together for a common-line cut,
        // and paired gussets meet exactly along their shared hypotenuse.
        public static bool AnyCrossing(List<(double X, double Y)> a, List<(double X, double Y)> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                var a1 = a[i];
                var a2 = a[(i + 1) % a.Count];

                for (int j = 0; j < b.Count; j++)
                {
                    var b1 = b[j];
                    var b2 = b[(j + 1) % b.Count];

                    double d1 = Side(b1, b2, a1);
                    double d2 = Side(b1, b2, a2);
                    double d3 = Side(a1, a2, b1);
                    double d4 = Side(a1, a2, b2);

                    if (d1 * d2 < 0 && d3 * d4 < 0)
                        return true;
                }
            }

            return false;
        }

        private static double Side((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);

        // Worst pair in a whole layout: how close the closest two outlines come, and whether any
        // two cross.
        public static (double Closest, int Crossings) Worst(IReadOnlyList<List<(double X, double Y)>> outlines)
        {
            double closest = double.MaxValue;
            int crossings = 0;

            for (int i = 0; i < outlines.Count; i++)
            {
                for (int j = i + 1; j < outlines.Count; j++)
                {
                    if (AnyCrossing(outlines[i], outlines[j]))
                        crossings++;

                    closest = Math.Min(closest, MinGap(outlines[i], outlines[j]));
                }
            }

            return (closest, crossings);
        }
    }
}
