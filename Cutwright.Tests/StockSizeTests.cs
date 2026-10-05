using System.Globalization;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // Formatting and parsing of the stock selector's "W x L" text. These are one unit because they
    // have to agree: the grid offers a nest's current size by formatting it and applies a pick by
    // parsing it back, so a format the parser rejects means picking that size silently does
    // nothing while the grid claims otherwise.
    public sealed class StockSizeTests
    {
        [Theory]
        [InlineData(48, 96)]
        [InlineData(48, 120)]
        [InlineData(60, 120)]
        [InlineData(0.5, 1.25)]
        [InlineData(47.75, 95.5)]
        public void AnyFormattedSizeParsesBackToItself(double width, double length)
        {
            string text = StockSize.Format(width, length);

            Assert.True(StockSize.TryParse(text, out float parsedWidth, out float parsedLength),
                $"'{text}' formatted from {width} x {length} did not parse back.");

            Assert.Equal(width, parsedWidth, 2);
            Assert.Equal(length, parsedLength, 2);
        }

        // The preset options are written as literals in MainWindow, so they have to parse too -
        // they are not produced by Format.
        [Theory]
        [InlineData("48 x 96", 48, 96)]
        [InlineData("48 x 120", 48, 120)]
        [InlineData("60 x 120", 60, 120)]
        public void ThePresetOptionsParse(string choice, float width, float length)
        {
            Assert.True(StockSize.TryParse(choice, out float parsedWidth, out float parsedLength));
            Assert.Equal(width, parsedWidth);
            Assert.Equal(length, parsedLength);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("QTY")]
        [InlineData("FT")]
        [InlineData("48")]
        [InlineData("48 x")]
        [InlineData("48 x 96 x 12")]
        [InlineData("wide x long")]
        [InlineData("0 x 96")]
        [InlineData("-48 x 96")]
        public void NonSizesAreRejected(string? choice)
        {
            Assert.False(StockSize.TryParse(choice, out _, out _));
        }

        // Tolerated because a pasted or hand-typed size should not be rejected over whitespace.
        [Theory]
        [InlineData("48x96")]
        [InlineData("  48   x   96  ")]
        public void SpacingAroundTheSeparatorIsTolerated(string choice)
        {
            Assert.True(StockSize.TryParse(choice, out float width, out float length));
            Assert.Equal(48.0f, width);
            Assert.Equal(96.0f, length);
        }

        [Fact]
        public void FormatIsTheShapeTheGridExpects()
        {
            Assert.Equal("48 x 96", StockSize.Format(48.0, 96.0));

            // Trailing zeros are dropped, which is what "0.##" does and what the presets look like.
            Assert.Equal("47.75 x 95.5", StockSize.Format(47.75, 95.50));
        }
    }
}
