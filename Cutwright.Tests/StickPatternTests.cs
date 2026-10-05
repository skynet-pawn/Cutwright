using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Cutwright.Tests
{
    // The grouping behind the 1D drawing's "x12" multipliers.
    //
    // The claim that has to hold is narrow but important: the multipliers must sum to the stick
    // count the estimate is built on. A drawing that says nine sticks while the grid says ten is
    // worse than no drawing at all.
    public sealed class StickPatternTests
    {
        private readonly ITestOutputHelper output;

        public StickPatternTests(ITestOutputHelper output) => this.output = output;

        private static TNest Nest((float Length, int Qty)[] cut)
        {
            var nest = new TNest { StickLength = 240f };

            int line = 1;
            foreach (var (length, qty) in cut)
                nest.Parts.Add(new Part(line++, qty, "TUBE TEST", $"P{line}", 1f, length, length));

            nest.Nest();
            return nest;
        }

        public static TheoryData<string, float[], int[]> Cases => new()
        {
            { "mixed mid lengths", new[] { 96f, 72f, 60f, 48f, 36f, 24f }, new[] { 8, 10, 12, 14, 10, 8 } },
            { "uniform 70in", new[] { 70f }, new[] { 24 } },
            { "tight thirds", new[] { 78f }, new[] { 27 } },
            { "just over half stick", new[] { 125f, 118f, 110f }, new[] { 6, 6, 8 } },
            { "long plus fillers", new[] { 118f, 96f, 12f, 8f, 6f }, new[] { 8, 6, 30, 24, 20 } },
            { "small parts only", new[] { 18f, 12f }, new[] { 60, 40 } },
            { "single stick", new[] { 60f }, new[] { 3 } },
            { "all oversize", new[] { 300f }, new[] { 4 } },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void MultipliersSumToTheStickCount(string name, float[] lengths, int[] quantities)
        {
            var nest = Nest(lengths.Zip(quantities, (l, q) => (l, q)).ToArray());
            var patterns = StickPatterns.GroupIdenticalSticks(nest.Sticks);

            output.WriteLine($"{name}: {nest.Sticks.Count} sticks in {patterns.Count} row(s) - " +
                             string.Join(" ", patterns.Select(p => $"x{p.Count}")));

            Assert.Equal(nest.Sticks.Count, patterns.Sum(p => p.Count));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void EveryStickBelongsToExactlyOneRow(string name, float[] lengths, int[] quantities)
        {
            var nest = Nest(lengths.Zip(quantities, (l, q) => (l, q)).ToArray());
            var patterns = StickPatterns.GroupIdenticalSticks(nest.Sticks);

            foreach (var stick in nest.Sticks)
            {
                int matches = patterns.Count(p => StickPatterns.SameArrangement(p.Representative, stick));
                Assert.True(matches == 1,
                    $"{name}: a stick matches {matches} rows, expected exactly 1");
            }
        }

        // Two rows that are the same arrangement should have been one row with a bigger multiplier.
        [Theory]
        [MemberData(nameof(Cases))]
        public void NoTwoRowsAreTheSameArrangement(string name, float[] lengths, int[] quantities)
        {
            var nest = Nest(lengths.Zip(quantities, (l, q) => (l, q)).ToArray());
            var patterns = StickPatterns.GroupIdenticalSticks(nest.Sticks);

            for (int i = 0; i < patterns.Count; i++)
            {
                for (int j = i + 1; j < patterns.Count; j++)
                {
                    Assert.False(StickPatterns.SameArrangement(patterns[i].Representative, patterns[j].Representative),
                        $"{name}: rows {i} and {j} are identical and should have been merged");
                }
            }
        }

        // The case the feature exists for: a group of identical sticks should collapse to one row.
        [Fact]
        public void IdenticalSticksCollapseToASingleRow()
        {
            var nest = Nest(new[] { (70f, 24) });
            var patterns = StickPatterns.GroupIdenticalSticks(nest.Sticks);

            Assert.Single(patterns);
            Assert.Equal(nest.Sticks.Count, patterns[0].Count);
        }

        [Fact]
        public void SticksWithDifferentContentsStaySeparate()
        {
            var nest = Nest(new[] { (200f, 1), (100f, 2) });
            var patterns = StickPatterns.GroupIdenticalSticks(nest.Sticks);

            Assert.True(patterns.Count > 1, "a 200in stick and a 2x100in stick are not the same arrangement");
        }

        [Fact]
        public void NoSticksMeansNoRows()
        {
            var nest = Nest(new[] { (300f, 4) });

            Assert.Empty(StickPatterns.GroupIdenticalSticks(nest.Sticks));
        }
    }
}
