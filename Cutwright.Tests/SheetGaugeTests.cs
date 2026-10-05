using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The gauge tables the part builder reads thicknesses from. A gauge is an index into a table,
    // and each material has its own.
    public sealed class SheetGaugeTests
    {
        [Theory]
        [InlineData("Steel", 14, 0.0747)]
        [InlineData("Aluminum", 14, 0.0641)]
        [InlineData("Stainless", 11, 0.1250)]
        [InlineData("Stainless", 14, 0.0781)]
        [InlineData("Stainless", 16, 0.0625)]
        public void AGaugeIsItsMaterialsThickness(string material, int gauge, double inches)
        {
            Assert.Equal(inches, SheetGauge.Inches(gauge, Enum.Parse<SwMaterial>(material)));
        }

        // Stainless 7 to 14 gauge are whole 64ths of an inch (US Standard Gauge). 13 gauge is 6/64 =
        // 0.09375, which is 0.0938 to four places; it was entered as 0.0940.
        [Fact]
        public void StainlessSevenToFourteenAreWholeSixtyFourths()
        {
            foreach (int gauge in new[] { 7, 8, 9, 10, 11, 12, 13, 14 })
            {
                double sixtyFourths = SheetGauge.Inches(gauge, SwMaterial.Stainless)!.Value * 64;
                Assert.True(Math.Abs(sixtyFourths - Math.Round(sixtyFourths)) < 0.01,
                    $"{gauge} GA stainless is {sixtyFourths}/64");
            }
        }
    }
}
