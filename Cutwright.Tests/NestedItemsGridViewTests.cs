using System;
using System.Collections.Generic;
using System.Linq;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The row model behind the Nested Parts List. Its selection callbacks decide whether a pick
    // re-nests, so the two failure modes worth guarding are a seeded value looking like a user
    // pick (re-nesting on load, for every group) and a real pick not firing at all (the grid shows
    // the new choice while the nest keeps the old rules - a wrong number presented as right).
    public sealed class NestedItemsGridViewTests
    {
        private static NestedItemsGridView Row() => new()
        {
            Description = "Sheet 11GA HR",
            StockOptions = new[] { "48 x 96", "48 x 120" },
            MaterialOptions = MaterialPolicy.All.Select(p => p.Name).ToList()
        };

        [Fact]
        public void SeedingTheMaterialBeforeWiringDoesNotCountAsAPick()
        {
            var row = Row();
            int fired = 0;

            row.SelectedMaterial = "Sheet / Plate";
            row.MaterialChanged = _ => fired++;

            Assert.Equal(0, fired);
            Assert.Equal("Sheet / Plate", row.SelectedMaterial);
        }

        [Fact]
        public void PickingADifferentMaterialFires()
        {
            var row = Row();
            var picks = new List<string>();

            row.SelectedMaterial = "Sheet / Plate";
            row.MaterialChanged = choice => picks.Add(choice);

            row.ApplyMaterialSelection("Expanded Metal");

            Assert.Equal(new[] { "Expanded Metal" }, picks);
            Assert.Equal("Expanded Metal", row.SelectedMaterial);
        }

        // A virtualized row being re-realized re-selects the value it already has. That must not
        // read as a fresh pick, or scrolling the grid would re-nest every group it passes.
        [Fact]
        public void ReselectingTheSameMaterialIsIgnored()
        {
            var row = Row();
            int fired = 0;

            row.SelectedMaterial = "Foam";
            row.MaterialChanged = _ => fired++;

            row.ApplyMaterialSelection("Foam");
            row.ApplyMaterialSelection("Foam");

            Assert.Equal(0, fired);
        }

        [Fact]
        public void ANullSelectionIsIgnored()
        {
            var row = Row();
            int fired = 0;

            row.SelectedMaterial = "Wire Mesh";
            row.MaterialChanged = _ => fired++;

            row.ApplyMaterialSelection(null);

            Assert.Equal(0, fired);
            Assert.Equal("Wire Mesh", row.SelectedMaterial);
        }

        // Every offered option has to resolve back to a real policy, since the handler looks the
        // choice up by Name. If the list and MaterialPolicy.All ever drift apart, a pick silently
        // does nothing - which is exactly the class of bug this column was added to fix.
        [Fact]
        public void EveryOfferedMaterialResolvesToAPolicy()
        {
            var row = Row();

            Assert.NotEmpty(row.MaterialOptions);

            foreach (string option in row.MaterialOptions)
            {
                var policy = MaterialPolicy.All.FirstOrDefault(p => p.Name == option);
                Assert.NotNull(policy);
            }
        }

        [Fact]
        public void StockAndMaterialSelectionsAreIndependent()
        {
            var row = Row();
            int stockPicks = 0, materialPicks = 0;

            row.SelectedStock = "48 x 96";
            row.SelectedMaterial = "Sheet / Plate";
            row.StockChanged = _ => stockPicks++;
            row.MaterialChanged = _ => materialPicks++;

            row.ApplyMaterialSelection("Foam");

            Assert.Equal(0, stockPicks);
            Assert.Equal(1, materialPicks);
            Assert.Equal("48 x 96", row.SelectedStock);
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("0", false)]
        [InlineData("3", true)]
        public void HasUnnestedFollowsTheUnnestedCount(string count, bool expected)
        {
            var row = Row();
            var changed = new List<string?>();
            row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            row.UnnestedCount = count;

            Assert.Equal(expected, row.HasUnnested);

            // The tint is bound to HasUnnested, so it has to be told when the count moves.
            if (count != "")
                Assert.Contains(nameof(NestedItemsGridView.HasUnnested), changed);
        }
    }
}
