using Xunit;

namespace Cutwright.Tests
{
    // CalloutTranslator.ReadFromColumns/TranslateFromColumns - the sibling of Read()/Translate()
    // for a drawing whose own BOM table keeps dimensions in columns of their own (Thickness,
    // Width, Height) rather than embedded in the description text. Every case here is either a
    // line from a drawing that surfaced this gap, or its
    // matching negative case.
    public class CalloutTranslatorColumnDimensionsTests
    {
        private static CalloutTranslator.ColumnDimensions Dims(
            float? thickness = null, float? width = null, float? height = null) =>
            new(thickness, width, height);

        [Fact]
        public void AngleReadsHeightAndWidthAsItsTwoLegs()
        {
            // Example: Thickness 1/4", Height 2", Width 2", Length 2 15/16".
            Assert.Equal("L Angle 2 x 2 x 1/4 HR",
                CalloutTranslator.TranslateFromColumns("ANGLE", null, Dims(thickness: 0.25f, width: 2f, height: 2f)));
        }

        [Fact]
        public void SquareTubeIsDecidedByEqualLegs()
        {
            Assert.Equal("SQ Tube 2 x 2 x 11 GA HR",
                CalloutTranslator.TranslateFromColumns("SQ TUBE (LASER)", null,
                    Dims(thickness: 0.1196f, width: 2f, height: 2f)));
        }

        [Fact]
        public void RectangularTubeIsDecidedByUnequalLegs()
        {
            Assert.Equal("Rect Tube 3 x 2 x 11 GA HR",
                CalloutTranslator.TranslateFromColumns("RECT TUBE", null,
                    Dims(thickness: 0.1196f, width: 3f, height: 2f)));
        }

        [Fact]
        public void FlatBarReadsThicknessAndEitherFaceDimension()
        {
            // Example: Thickness 3/8", Width 2", Length 85 5/16" - "Rect Bar" is this
            // drawing's own term for the same stock the shop calls Flat Bar.
            Assert.Equal("FB 3/8 x 2 HR",
                CalloutTranslator.TranslateFromColumns("RECT BAR", null, Dims(thickness: 0.375f, width: 2f)));

            // A drawing that fills Height instead of Width for the same dimension reads the same.
            Assert.Equal("FB 3/8 x 2 HR",
                CalloutTranslator.TranslateFromColumns("FLAT BAR", null, Dims(thickness: 0.375f, height: 2f)));
        }

        [Fact]
        public void SquareBarReadsWidthAndHeightPositionally()
        {
            Assert.Equal("SQ Bar Stock 1 x 1 HR",
                CalloutTranslator.TranslateFromColumns("SQ BAR", null, Dims(width: 1f, height: 1f)));
        }

        [Fact]
        public void RoundTubeReadsWidthAsDiameterAndThicknessAsWall()
        {
            Assert.Equal("Round Tube 2\" OD x 11 GA wall HR",
                CalloutTranslator.TranslateFromColumns("ROUND TUBE", null, Dims(thickness: 0.1196f, width: 2f)));
        }

        [Fact]
        public void PipeReadsWidthAsItsOneSize()
        {
            // Example: "2" SCH 40 BLACK PIPE", Width (OD-adjacent column) not what is used -
            // the drawing's own callout size is what the estimator maps to Width.
            Assert.Equal("Pipe 2\" Sch. 40",
                CalloutTranslator.TranslateFromColumns("PIPE", null, Dims(width: 2f)));
        }

        [Fact]
        public void PlateReadsOnlyItsThicknessColumnNotWidthOrLength()
        {
            // Example: Thickness 1/4", Width 2 7/8", Length 7" - Width/Length are the
            // nesting footprint here, not part of the callout, unlike every stick form above.
            Assert.Equal("Plate 1/4 HR",
                CalloutTranslator.TranslateFromColumns("SHEET STEEL (LASER)", null,
                    Dims(thickness: 0.25f, width: 2.875f)));
        }

        [Fact]
        public void SheetIsDecidedByTheGaugeBoundaryLikeRead()
        {
            Assert.Equal("Sheet 11 GA HR",
                CalloutTranslator.TranslateFromColumns("SHEET STEEL (LASER)", null,
                    Dims(thickness: 0.1196f, width: 5f)));
        }

        [Theory]
        [InlineData("ANGLE")] // missing Height and Thickness
        [InlineData("SQ TUBE")] // missing Height and Thickness
        [InlineData("RECT BAR")] // missing Thickness
        [InlineData("SQ BAR")] // missing Height
        [InlineData("ROUND TUBE")] // missing Thickness
        [InlineData("PLATE")] // missing Thickness
        public void AMissingRequiredDimensionDeclinesRatherThanGuessing(string text)
        {
            Assert.Null(CalloutTranslator.TranslateFromColumns(text, null, Dims(width: 2f)));
        }

        [Fact]
        public void PipeWithNoDimensionColumnAtAllDeclines()
        {
            // Pipe/Rod need only Width, so they can't join the shared Dims(width: 2f) case above -
            // a missing Width is the only way either can fail to build a spec.
            Assert.Null(CalloutTranslator.TranslateFromColumns("PIPE", null, Dims()));
        }

        [Theory]
        [InlineData("C CHANNEL")]
        [InlineData("EXPANDED METAL")]
        [InlineData("WELD ASSY BRACKET")]
        public void AFormReadFromColumnsCannotBuildAlwaysDeclines(string text)
        {
            // AISC sections, expanded metal, and anything NamedForm does not recognise at all stay
            // on the legacy text-only path regardless of what dimension columns are supplied.
            Assert.Null(CalloutTranslator.TranslateFromColumns(text, null,
                Dims(thickness: 0.25f, width: 2f, height: 2f)));
        }

        [Fact]
        public void AnEmptyDescriptionDeclines()
        {
            Assert.Null(CalloutTranslator.TranslateFromColumns("", null, Dims(width: 2f)));
            Assert.Null(CalloutTranslator.TranslateFromColumns(null, null, Dims(width: 2f)));
        }
    }
}
