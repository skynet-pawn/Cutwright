using Xunit;

namespace Cutwright.Tests
{
    // Reading a size out of a material callout. Every string here is taken verbatim from one of the
    // four real Tabula exports in the repository - this parser exists because two of those four
    // carry no dimension columns at all, and inventing test input would only prove the parser
    // handles callouts we imagined.
    public class CalloutDimensionTests
    {
        // Three dimensions, thickness found by size rather than by position - customers disagree
        // about where it goes.
        [Theory]
        [InlineData("2.000 x 2.000 x 0.125", 2.0f, 2.0f)]
        [InlineData("24.000 x 5.000 x 0.500", 5.0f, 24.0f)]
        [InlineData("127.750 x 2.000 x 0.375", 2.0f, 127.75f)]
        [InlineData("2.000 x 2.000 x .188", 2.0f, 2.0f)]
        [InlineData("CAP PLATE A 3/4 x 2 x 3-1/8", 2.0f, 3.125f)]
        [InlineData("END PLATE 3/8 x 5 x 36-1/8", 5.0f, 36.125f)]
        [InlineData("TIE BAR 1/4 x 2 x 2", 2.0f, 2.0f)]
        [InlineData(".25 X 48 X 96 PLATE, HR", 48.0f, 96.0f)]
        [InlineData("Laser HRS Triangle 3/16\" x 2 5/8\" x 1 3/4\"", 1.75f, 2.625f)]
        public void ThreeDimensionsGiveTheTwoLargest(string callout, float width, float length)
        {
            var reading = CalloutDimensions.Read(callout);

            Assert.True(reading.Found, $"'{callout}' was not read: {reading.Reason}");
            Assert.Equal(width, reading.Width, 3);
            Assert.Equal(length, reading.Length, 3);
        }

        // A gauge is the thickness, so two dimensions plus a gauge is a complete callout. The gauge
        // number is deliberately not converted to inches - gauge tables differ by material, and a
        // wrong thickness would be worse than none.
        [Theory]
        [InlineData("HRS 11ga x 1 7/8\" x 2 3/8\"lg - Shear Only", 1.875f, 2.375f)]
        [InlineData("Laser HRS 11ga x 8 1/2\" x 14\"lg", 8.5f, 14.0f)]
        [InlineData("HRS 11ga x 7/8\" x 1 7/8\"lg - Shear Only", 0.875f, 1.875f)]
        public void AGaugeCountsAsTheThickness(string callout, float width, float length)
        {
            var reading = CalloutDimensions.Read(callout);

            Assert.True(reading.Found, $"'{callout}' was not read: {reading.Reason}");
            Assert.Equal(width, reading.Width, 3);
            Assert.Equal(length, reading.Length, 3);
        }

        // 1/4-20 is a thread. A fraction, a hyphen, then a whole number appears only in fasteners
        // across all four samples - where a whole number then a fraction, 36-1/8, is a real size.
        [Theory]
        [InlineData("F.S.H.C.S. 1/4-20 x 1-1/4")]
        [InlineData("NYLOCK HEX NUT 1/2-13")]
        [InlineData("H.H.C.S (GRADE 8) 1/2-13 x 1-1/2")]
        [InlineData("1/4-20 X 3.00 LG. - GRADE 5 HEX HEAD BOLT SCREW")]
        [InlineData("3/16 LARGE FLANGE BLIND RIVET X  GRIP TO SUIT")]
        [InlineData("3/8\"-UNC HHCS x 1\"lg - Gr.5 - Zinc Plated")]
        [InlineData("FLAT WASHER 1/2\" NOM.")]
        public void HardwareIsNotReadAsASize(string callout)
        {
            var reading = CalloutDimensions.Read(callout);

            Assert.False(reading.Found, $"'{callout}' was read as {reading.Width} x {reading.Length}");
            Assert.NotEmpty(reading.Reason);
        }

        // Part names and material codes contain digits. Requiring an x between numbers, and
        // refusing a number glued to a letter, digit or period, is what keeps them out.
        [Theory]
        [InlineData("8815N11_STACKING_CAP")]
        [InlineData("CB_PLATE_4_LINES_31")]
        [InlineData("CB_HDPE_4_LINES_35")]
        [InlineData("FS6.00X0.19")]
        [InlineData(">FS6.00X0.19")]
        [InlineData("9 LB X-LINK FOAM")]
        [InlineData("3/4 #9 STD EXP METAL")]
        [InlineData("50mm NYLON STRAP WITH HOOK")]
        public void NamesAndCodesAreNotReadAsSizes(string text)
        {
            var reading = CalloutDimensions.Read(text);

            Assert.False(reading.Found, $"'{text}' was read as {reading.Width} x {reading.Length}");
        }

        // Four or more dimensions is a formed part or linear stock. The flat size cannot be
        // recovered from folded dimensions, so it declines rather than guessing at one.
        [Theory]
        [InlineData("TIE BAR 1/4 x 2 x 2 x 22-3/8")]
        [InlineData("MAIN CHANNEL 3/16 x 3-1/2 x 10 x 12-1/8 A-36")]
        [InlineData("Formed Laser HRS 1/4\" x 1 1/2\" x 7 1/16\" x 1 1/2\" x 2 3/16\" lg")]
        [InlineData("HSS 8\" X 3\" X .125 X 47 7/8\"lg")]
        public void FormedAndLinearStockDecline(string callout)
        {
            var reading = CalloutDimensions.Read(callout);

            Assert.False(reading.Found, $"'{callout}' was read as {reading.Width} x {reading.Length}");
            Assert.NotEmpty(reading.Reason);
        }

        // Two numbers with no thickness anywhere is not a plate callout - it is a note or a
        // purchased item. "Cover 2x1" inside a longer string used to produce a 2 by 1 part.
        [Theory]
        [InlineData("SG1700 x 43 1/4\" - Cover 2x1, Opening 2\" Side")]
        [InlineData("Plastic Tube Plug 2 1/2\" x 1\" - RC-1025")]
        public void TwoDimensionsWithNoThicknessDecline(string callout)
        {
            Assert.False(CalloutDimensions.Read(callout).Found);
        }

        // Counts and notes in brackets are not dimensions.
        [Fact]
        public void BracketedCountsAreIgnored()
        {
            var reading = CalloutDimensions.Read("POST (WT:3/16) 2 x 2 x 14-1/8");

            Assert.True(reading.Found);
            Assert.Equal(2.0f, reading.Width, 3);
            Assert.Equal(14.125f, reading.Length, 3);
        }

        [Fact]
        public void TrailingNotesDoNotChangeTheSize()
        {
            var withNote = CalloutDimensions.Read(
                "Laser HRS 11ga x 9 17/32\" x 3 5/8\"lg - c/w Profile 79, (6) Radii");

            Assert.True(withNote.Found);
            Assert.Equal(3.625f, withNote.Width, 3);
            Assert.Equal(9.53125f, withNote.Length, 4);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void NothingToReadIsReported(string? callout)
        {
            var reading = CalloutDimensions.Read(callout);

            Assert.False(reading.Found);
            Assert.NotEmpty(reading.Reason);
        }

        // Width is always the smaller of the two, so a nest is not sensitive to which way round the
        // callout happened to be written.
        [Fact]
        public void WidthIsAlwaysTheSmallerFaceDimension()
        {
            var a = CalloutDimensions.Read("0.25 x 12 x 48");
            var b = CalloutDimensions.Read("48 x 12 x 0.25");

            Assert.True(a.Found && b.Found);
            Assert.Equal(a.Width, b.Width, 3);
            Assert.Equal(a.Length, b.Length, 3);
            Assert.True(a.Width <= a.Length);
        }
    }
}
