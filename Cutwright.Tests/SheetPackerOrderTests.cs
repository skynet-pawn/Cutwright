using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // Measures whether trying several input orderings and keeping the best is worth its cost.
    //
    // Benchmarking against NestFab showed we spend about one extra sheet on jobs made of a few
    // large awkward plates. Input order was the cheapest candidate explanation - a constructive
    // packer is entirely at the mercy of which part claims space first - so PackBest tries a fixed
    // set of orderings. This is the experiment that says whether that actually buys anything.
    //
    // It leans on the one asymmetry we have over a search-based nester: a pack costs microseconds,
    // so a question that would take a commercial nester days of runtime is answered here in a
    // second, over hundreds of randomised jobs rather than a handful of hand-picked ones.
    public sealed class SheetPackerOrderTests
    {
        private const double SheetShort = 48.0;
        private const double SheetLong = 96.0;
        private const double Spacing = 0.25;

        private readonly ITestOutputHelper output;

        public SheetPackerOrderTests(ITestOutputHelper output) => this.output = output;

        private sealed record Job(string Name, List<PackItem> Items);

        [Fact]
        public void TryingEveryOrderingIsNeverWorseThanTheDefault()
        {
            var orders = Enum.GetValues<PackOrder>();
            var wins = new Dictionary<PackOrder, int>();
            foreach (var order in orders)
                wins[order] = 0;

            int jobs = 0, improved = 0, baselineTotal = 0, bestTotal = 0;
            var improvedBySize = new Dictionary<string, int>();
            var jobsBySize = new Dictionary<string, int>();

            foreach (var job in BuildJobs())
            {
                jobs++;
                jobsBySize[job.Name] = jobsBySize.GetValueOrDefault(job.Name) + 1;

                int baseline = SheetPacker
                    .Pack(job.Items, SheetShort, SheetLong, Spacing, CutMethod.FreePlacement,
                        PackOrder.MaxSideThenArea)
                    .SheetCount;

                var best = SheetPacker.PackBest(job.Items, SheetShort, SheetLong, Spacing,
                    CutMethod.FreePlacement);

                baselineTotal += baseline;
                bestTotal += best.SheetCount;

                // PackBest includes the default strategy, so this can only ever tie or improve.
                // Asserting it guards against a future strategy being added that somehow loses.
                Assert.True(best.SheetCount <= baseline,
                    $"{job.Name}: PackBest returned {best.SheetCount} sheets against the " +
                    $"default strategy's {baseline}, which should be impossible.");
                Assert.Empty(best.Unplaced);

                if (best.SheetCount < baseline)
                {
                    improved++;
                    improvedBySize[job.Name] = improvedBySize.GetValueOrDefault(job.Name) + 1;
                }

                // Which single strategy would have produced the winning count, counted so a
                // strategy that never wins anything can be dropped rather than carried.
                foreach (var order in orders)
                {
                    int count = SheetPacker
                        .Pack(job.Items, SheetShort, SheetLong, Spacing, CutMethod.FreePlacement, order)
                        .SheetCount;
                    if (count == best.SheetCount)
                        wins[order]++;
                }
            }

            output.WriteLine($"{jobs} randomised jobs");
            output.WriteLine($"sheets  default {baselineTotal} -> best-of-all {bestTotal} " +
                             $"({(baselineTotal > 0 ? (bestTotal - baselineTotal) * 100.0 / baselineTotal : 0):0.00}%)");
            output.WriteLine($"jobs improved: {improved} of {jobs} ({improved * 100.0 / jobs:0.0}%)");
            output.WriteLine("");

            output.WriteLine("improvement by job shape:");
            foreach (var shape in jobsBySize.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                output.WriteLine($"  {shape,-14} {improvedBySize.GetValueOrDefault(shape),4} of " +
                                 $"{jobsBySize[shape],4} improved");
            }

            output.WriteLine("");
            output.WriteLine("times each strategy matched the best result:");
            foreach (var order in orders)
                output.WriteLine($"  {order,-20} {wins[order],5} of {jobs}");
        }

        // Job shapes chosen to span the axis the NestFab benchmark implicated: how large the parts
        // are relative to the sheet. "few-large" is the shape we lost a sheet on.
        private static IEnumerable<Job> BuildJobs()
        {
            // Explicit seed bases rather than anything derived from the name: string.GetHashCode is
            // randomised per process in .NET, so seeding from it gives a different job set on every
            // run and the totals below would not be reproducible.
            var shapes = new (string Name, int SeedBase, int Types, double Min, double Max, int QtyMax)[]
            {
                ("few-large",   10_000,  6,  16, 46,  4),
                ("some-medium", 20_000, 12,   8, 34,  8),
                ("many-small",  30_000, 20,   1, 12, 14),
                ("mixed",       40_000, 16,   2, 44, 10),
                ("long-thin",   50_000, 10,   1, 88,  9)
            };

            foreach (var shape in shapes)
            {
                for (int seed = 0; seed < 80; seed++)
                {
                    var random = new Random(shape.SeedBase + seed);
                    var items = new List<PackItem>();

                    for (int t = 0; t < shape.Types; t++)
                    {
                        double width = Quarter(shape.Min + random.NextDouble() * (shape.Max - shape.Min));
                        double length = Quarter(shape.Min + random.NextDouble() * (shape.Max - shape.Min));

                        width = Math.Clamp(width, 0.5, SheetShort - Spacing * 2);
                        length = Math.Clamp(length, 0.5, SheetLong - Spacing * 2);

                        int quantity = random.Next(1, shape.QtyMax + 1);
                        for (int q = 0; q < quantity; q++)
                            items.Add(new PackItem { Width = width, Length = length });
                    }

                    yield return new Job(shape.Name, items);
                }
            }
        }

        private static double Quarter(double value) =>
            Math.Round(value * 4, MidpointRounding.AwayFromZero) / 4.0;
    }
}
