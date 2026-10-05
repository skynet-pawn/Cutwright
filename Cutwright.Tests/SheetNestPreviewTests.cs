using Xunit;

namespace Cutwright.Tests
{
    public class SheetNestPreviewTests
    {
        private static SheetPartInput Plate(string number, int quantity, double width, double length) =>
            new(number, "Sheet 14GA HR", quantity, width, length, null);

        [Fact]
        public void PlacesEveryPartInsideTheSheet()
        {
            var result = SheetNestPreview.Nest(new[] { Plate("P-1", 12, 10, 20) }, new SheetNestSetup());

            Assert.Equal(1, result.SheetCount);
            var layout = Assert.Single(result.Layouts);
            Assert.Equal(12, layout.Parts.Count);
            foreach (var part in layout.Parts)
                foreach (var (x, y) in part.Outer)
                {
                    Assert.InRange(x, -1e-6, result.SheetWidthInches + 1e-6);
                    Assert.InRange(y, -1e-6, result.SheetLengthInches + 1e-6);
                }
            Assert.InRange(result.UtilizationPercent, 1, 100);
        }

        [Fact]
        public void MoreAreaNeedsMoreSheets_AndIdenticalSheetsShareALayout()
        {
            var result = SheetNestPreview.Nest(new[] { Plate("Big", 6, 40, 80) }, new SheetNestSetup());

            Assert.True(result.SheetCount > 1);
            Assert.Equal(result.SheetCount, result.Layouts.Sum(l => l.Count));
            Assert.True(result.Layouts.Count < result.SheetCount);
        }

        [Fact]
        public void SmallestDropShrinksTheSheet()
        {
            var fixedSize = SheetNestPreview.Nest(new[] { Plate("P", 2, 10, 20) }, new SheetNestSetup());
            var smallest = SheetNestPreview.Nest(new[] { Plate("P", 2, 10, 20) }, new SheetNestSetup { SmallestDrop = true });

            Assert.True(smallest.SheetWidthInches * smallest.SheetLengthInches
                        < fixedSize.SheetWidthInches * fixedSize.SheetLengthInches);
            Assert.Equal(1, smallest.SheetCount);
        }

        [Fact]
        public void APartLargerThanTheSheetIsReportedNotNested()
        {
            var result = SheetNestPreview.Nest(new[] { Plate("Huge", 1, 100, 200) }, new SheetNestSetup());

            Assert.Contains("Huge", result.UnnestedPartNumbers);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void WiderSpacingNeverUsesFewerSheets()
        {
            var parts = new[] { Plate("P", 40, 10, 10) };
            var tight = SheetNestPreview.Nest(parts, new SheetNestSetup { PartSpacingInches = 0.1 });
            var loose = SheetNestPreview.Nest(parts, new SheetNestSetup { PartSpacingInches = 2 });

            Assert.True(loose.SheetCount >= tight.SheetCount);
            Assert.True(loose.UtilizationPercent <= tight.UtilizationPercent + 1e-9 || loose.SheetCount > tight.SheetCount);
        }
    }
}
