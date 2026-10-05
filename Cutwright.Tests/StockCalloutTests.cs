using Xunit;

namespace Cutwright.Tests
{
    // The stock callout grammar, as settled by going through descriptions in the styles bills of
    // materials use and asking about each place two forms of the same thing appeared.
    //
    // These are the rules, and they are here as tests because the whole point is consistency: the
    // grammar had drifted ten ways in real BOMs - "Rect Tube" against "Rec Tube", "11 GA" against
    // "11GA", "1 1/2" against "1-1/2", a flat bar written width-first among a dozen written
    // thickness-first. A callout that comes out any other way now fails the build.
    public class StockCalloutTests
    {
        // Representative descriptions, in the styles drawings use.
        [Theory]
        // Tube. Square or rectangular is decided by the section, not by the word HSS.
        [InlineData("HSS 2\" x 2\" x .125 x 22 1/4\"lg", "SQ Tube 2 x 2 x 11 GA HR")]
        [InlineData("HSS 2\" x 2\" x .188 x 51\"lg", "SQ Tube 2 x 2 x 7 GA HR")]
        [InlineData("HSS 2\" x 1\" x .125 x 38\"lg", "Rect Tube 2 x 1 x 11 GA HR")]
        [InlineData("HSS 8\" X 3\" X .125 X 47 7/8\"lg- c/w (4) Cutouts", "Rect Tube 8 x 3 x 11 GA HR")]
        [InlineData("HSS 3\" X 2\" X .125 X 10 3/16\"lg", "Rect Tube 3 x 2 x 11 GA HR")]

        // Flat product. The thickness decides sheet or plate on its own - the cutoff is 7 GA, above
        // which it is a fraction and called plate.
        [InlineData("BASE PLATE A 3/16 x 3 x 61-1/8 A-36", "Sheet 7 GA HR")]
        [InlineData("RIB PLATE 3/8 x 3 x 4-3/4", "Plate 3/8 HR")]
        [InlineData("CAP PLATE A 3/4 x 2 x 3-1/8", "Plate 3/4 HR")]
        [InlineData("MOUNT PLATE 1/4 x 4 x 5-1/8 A-36", "Plate 1/4 HR")]
        [InlineData("GUSSET 1/4 x 6 x 8-1/8 A-36", "Plate 1/4 HR")]
        [InlineData("127.750 x 2.000 x 0.375", "Plate 3/8 HR")]
        [InlineData("2.000 x 2.000 x 0.125", "Sheet 11 GA HR")]

        // A decimal off a drawing is snapped onto what is actually bought.
        [InlineData("4.000 x 4.000 x 0.130", "Sheet 11 GA HR")]
        [InlineData("2.000 x 2.000 x .188", "Sheet 7 GA HR")]

        // A gauge on the drawing travels through the same ladder.
        [InlineData("HRS 11ga x 1 7/8\" x 2 3/8\"lg - Shear Only", "Sheet 11 GA HR")]

        [InlineData("3/4 #9 STD EXP METAL", "Exp. Metal 3/4 x #9 Raised")]
        public void ARealDescriptionBecomesItsCallout(string customer, string expected)
        {
            Assert.Equal(expected, CalloutTranslator.Translate(customer));
        }

        // The ten places the grammar had drifted, each settled one way.
        [Fact]
        public void GaugeCarriesASpace()
        {
            Assert.Contains("11 GA", CalloutTranslator.Translate("Sheet .125 x 48 x 96"));
            Assert.DoesNotContain("11GA", CalloutTranslator.Translate("Sheet .125 x 48 x 96"));
        }

        [Fact]
        public void MixedNumbersAreHyphenated()
        {
            string callout = CalloutTranslator.Translate("HSS 1 1/2\" x 1 1/2\" x .125 x 40\"lg");

            Assert.Equal("SQ Tube 1-1/2 x 1-1/2 x 11 GA HR", callout);
        }

        [Fact]
        public void PlateCarriesNoInchMark()
        {
            Assert.Equal("Plate 1/2 HR", CalloutTranslator.Translate("PLATE 1/2 x 12 x 24"));
        }

        [Fact]
        public void FlatBarIsThicknessThenWidth()
        {
            // Written both ways round on drawings; the callout is always thickness first.
            Assert.Equal("FB 1/4 x 2 HR", CalloutTranslator.Translate("FLAT BAR 1/4 x 2"));
            Assert.Equal("FB 1/4 x 2 HR", CalloutTranslator.Translate("FLAT BAR 2 x 1/4"));
        }

        [Fact]
        public void RectBarIsRecognisedAsFlatBar()
        {
            // A drawing's own term for the same stock -
            // "FLAT BAR"/"FLATBAR"/"FB" were recognised, "RECT BAR" was not.
            Assert.Equal("FB 3/8 x 2 HR", CalloutTranslator.Translate("RECT BAR 2 x 3/8"));
        }

        [Fact]
        public void EveryStockCalloutNamesItsGrade()
        {
            Assert.EndsWith("HR", CalloutTranslator.Translate("SQ TUBE 2 x 2 x .125"));
            Assert.EndsWith("HR", CalloutTranslator.Translate("L ANGLE 2 x 2 x 1/4"));
            Assert.EndsWith("HR", CalloutTranslator.Translate("SQ BAR STOCK 1 x 1"));
        }

        [Theory]
        [InlineData("SHEET .125 x 48 x 96 STAINLESS", "Stainless")]
        [InlineData("SHEET .125 x 48 x 96 ALUM", "Alum")]
        public void ANamedMaterialReplacesTheDefault(string customer, string grade)
        {
            Assert.EndsWith(grade, CalloutTranslator.Translate(customer));
        }

        // The material column is read as well as the description, which is what a Material role on
        // an imported column is for.
        [Fact]
        public void TheMaterialColumnCanSupplyTheGrade()
        {
            Assert.EndsWith("Stainless",
                CalloutTranslator.Translate("PLATE 1/4 x 12 x 24", material: "304 STAINLESS"));
        }

        // A grade column naming something that is not mild steel is carried through as written.
        // One drawing style calls liner stock S-7, which is tool steel; defaulting that to HR would quote a
        // hardened part as hot-rolled.
        [Theory]
        [InlineData("S-7", "Plate 1 S-7")]
        [InlineData("CST", "Plate 1 CST")]
        [InlineData("A-36", "Plate 1 HR")]
        [InlineData("STD", "Plate 1 HR")]
        [InlineData("-", "Plate 1 HR")]
        [InlineData("", "Plate 1 HR")]
        public void AnUnfamiliarGradeIsCarriedThroughRatherThanBuried(string material, string expected)
        {
            Assert.Equal(expected, CalloutTranslator.Translate("LINER D 1 x 2 x 4-1/8", material));
        }

        [Fact]
        public void RoundTubeAndPipeNameWhatTheirNumbersAre()
        {
            Assert.Equal("Round Tube 3/4\" OD x 11 GA wall HR",
                CalloutTranslator.Translate("ROUND TUBE 3/4 x .125"));

            // Pipe is called by its nominal size and its schedule, with no OD.
            Assert.Equal("Pipe 3/4\" Sch. 40", CalloutTranslator.Translate("PIPE 3/4 SCH 40"));
            Assert.Equal("PVC Pipe 1-1/4\" Sch. 40", CalloutTranslator.Translate("PVC PIPE 1-1/4 SCH 40"));
        }

        // StockCallout.Format's own Round Tube shape - "2" OD x 11 GA wall HR" - separates its two
        // numbers with "OD x ... wall" instead of joining them directly, which is what the
        // generic x-joined number reader requires everywhere else. Found by round-tripping a
        // synthetic BOM through BomLineOrder, which relies on Read() to sort a Round Tube line
        // into Tubes - every Round Tube line was silently falling into Purchased instead.
        [Fact]
        public void RoundTubesOwnCalloutRoundTrips()
        {
            string callout = CalloutTranslator.Translate("ROUND TUBE 2 x .125");

            Assert.True(CalloutTranslator.CanTranslate(callout));
            Assert.Equal(callout, CalloutTranslator.Translate(callout));
        }

        [Fact]
        public void AiscSectionsKeepTheirDesignation()
        {
            Assert.Equal("C Channel C3 x 4.1", CalloutTranslator.Translate("C CHANNEL C3 x 4.1"));
        }

        // Runs of whitespace collapse. Two spaces where every sibling has one is invisible in a
        // spreadsheet but makes a different string - so its own nesting group and its own purchase
        // line. "FB  1/4 x 1/2" was exactly that.
        [Fact]
        public void WhitespaceIsNormalised()
        {
            string callout = StockCallout.Format(new MaterialSpec
            {
                Form = StockForm.FlatBar,
                Section = new[] { 0.5f },
                Thickness = 0.25f
            });

            Assert.Equal("FB 1/4 x 1/2 HR", callout);
            Assert.DoesNotContain("  ", callout);
        }

        // What it cannot read comes back unchanged, so the estimator corrects it on the sheet. A
        // guess that reads like a real callout would be worse than an obvious passthrough.
        [Theory]
        [InlineData("CHECKERD_PLATE_1")]
        [InlineData("CB_PLATE_4_LINES_31")]
        [InlineData("9 LB X-LINK FOAM")]
        [InlineData("FOAM-2.00x4.00x24.00")]
        [InlineData("7.000 X 2.375 X 2.125")]
        [InlineData("F.S.H.C.S. 1/4-20 x 1-1/4")]
        [InlineData("NYLOCK HEX NUT 1/2-13")]
        [InlineData("50mm NYLON STRAP WITH HOOK")]
        public void WhatItCannotReadPassesThrough(string customer)
        {
            Assert.Equal(customer, CalloutTranslator.Translate(customer));
            Assert.False(CalloutTranslator.CanTranslate(customer));
        }

        // Part names carry digits. A plate callout needs dimensions, not just the word.
        [Fact]
        public void APartNameIsNotAMaterial()
        {
            Assert.False(CalloutTranslator.CanTranslate("CHECKERD_PLATE_1"));
            Assert.False(CalloutTranslator.CanTranslate("CB_PLATE_4_LINES_31"));
        }

        // Words that read as hardware also appear in part names - a MOUNT PLATE is a plate.
        [Fact]
        public void AHardwareWordDoesNotOverrideANamedStockForm()
        {
            Assert.True(CalloutTranslator.CanTranslate("MOUNT PLATE 1/4 x 4 x 5-1/8 A-36"));
            Assert.False(CalloutTranslator.CanTranslate("3/8\"-UNC HHCS x 1\"lg - Gr.5"));
        }

        // "Triangle" contains "angle", and "LINER C 1 x 2" looks like an AISC designation.
        [Fact]
        public void LooseWordMatchesDoNotInventForms()
        {
            Assert.Equal("Sheet 7 GA HR",
                CalloutTranslator.Translate("Laser HRS Triangle 3/16\" x 2\" x 2\" - c/w (1) Chamfer"));

            Assert.Equal("Plate 1 HR", CalloutTranslator.Translate("LINER C 1 x 2 x 3-1/8"));
        }

        [Theory]
        [InlineData(0.1196f, "11 GA")]
        [InlineData(0.13f, "11 GA")]
        [InlineData(0.125f, "11 GA")]
        [InlineData(0.1793f, "7 GA")]
        [InlineData(0.1875f, "7 GA")]
        [InlineData(0.25f, "1/4")]
        [InlineData(0.375f, "3/8")]
        [InlineData(0.5f, "1/2")]
        public void ThicknessLandsOnSomethingStocked(float inches, string expected)
        {
            Assert.Equal(expected, StockThickness.Text(inches));
        }

        [Theory]
        [InlineData(2f, "2")]
        [InlineData(1.5f, "1-1/2")]
        [InlineData(0.75f, "3/4")]
        [InlineData(2.625f, "2-5/8")]
        [InlineData(0.1875f, "3/16")]
        public void MeasuresAreWrittenAsWholesOrHyphenatedMixedNumbers(float inches, string expected)
        {
            Assert.Equal(expected, StockCallout.Measure(inches));
        }

        // One assertion per StockForm value, so a future new form forces a conscious call here
        // instead of silently defaulting - the same "two lists cannot drift apart" reasoning as
        // CsvImportTests.EveryOfferedLabelMapsBackToARole. FileParser's Width>0 TNest/PNest split
        // depends on every stick form landing on true and everything else on false. A single Fact
        // rather than a Theory, since StockForm is internal and a public Theory parameter of an
        // internal type is inconsistent accessibility even under InternalsVisibleTo.
        [Fact]
        public void StockFormsClassifiesEveryFormAsStickOrFlat()
        {
            Assert.True(StockForms.IsStick(StockForm.SquareTube));
            Assert.True(StockForms.IsStick(StockForm.RectangularTube));
            Assert.True(StockForms.IsStick(StockForm.RoundTube));
            Assert.True(StockForms.IsStick(StockForm.Angle));
            Assert.True(StockForms.IsStick(StockForm.FlatBar));
            Assert.True(StockForms.IsStick(StockForm.SquareBar));
            Assert.True(StockForms.IsStick(StockForm.Rod));
            Assert.True(StockForms.IsStick(StockForm.Pipe));
            Assert.True(StockForms.IsStick(StockForm.PvcPipe));

            Assert.False(StockForms.IsStick(StockForm.Sheet));
            Assert.False(StockForms.IsStick(StockForm.Plate));
            Assert.False(StockForms.IsStick(StockForm.ExpandedMetal));
            Assert.False(StockForms.IsStick(StockForm.AiscSection));
            Assert.False(StockForms.IsStick(StockForm.Unknown));
        }
    }
}
