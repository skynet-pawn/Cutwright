using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The per-end model behind the End Features tab and its sheet. The engine still reads only
    // TubeEndState, so the first group guards the mapping between the two: an end choice that
    // mapped to the wrong state would quietly change what nests where.
    public sealed class EndFeaturesTests
    {
        private static Part Stick(int line, float length = 60f, string description = "SQ Tube 2 x 2 x 11GA") =>
            new(line, 1, description, "P-" + line, 0f, length, 0f);

        [Theory]
        [InlineData(TubeEndFeature.Unreviewed, TubeEndFeature.Unreviewed, "Unreviewed")]
        [InlineData(TubeEndFeature.HasFeatures, TubeEndFeature.HasFeatures, "Unreviewed")]
        [InlineData(TubeEndFeature.Clear, TubeEndFeature.Clear, "Clean")]
        [InlineData(TubeEndFeature.Clear, TubeEndFeature.HasFeatures, "Clean")]
        [InlineData(TubeEndFeature.HasFeatures, TubeEndFeature.Clear, "Clean")]
        [InlineData(TubeEndFeature.Miter, TubeEndFeature.Unreviewed, "SingleMiter")]
        [InlineData(TubeEndFeature.Unreviewed, TubeEndFeature.Miter, "SingleMiter")]
        [InlineData(TubeEndFeature.Miter, TubeEndFeature.HasFeatures, "SingleMiter")]
        [InlineData(TubeEndFeature.Miter, TubeEndFeature.Clear, "SingleMiterClean")]
        [InlineData(TubeEndFeature.Clear, TubeEndFeature.Miter, "SingleMiterClean")]
        [InlineData(TubeEndFeature.Miter, TubeEndFeature.Miter, "DoubleMiterFacing")]
        public void EndsMapToTheStateTheEngineReads(TubeEndFeature a, TubeEndFeature b, string expected)
        {
            Assert.Equal(expected, TubeEndStateText.FromEnds(a, b).ToString());
        }

        // Facing (a taper) and opposed (a parallelogram) both need two miters; either end being
        // marked opposed is what says the second one runs the other way.
        [Theory]
        [InlineData(TubeEndFeature.Miter, TubeEndFeature.MiterOpposed, "DoubleMiterOpposite")]
        [InlineData(TubeEndFeature.MiterOpposed, TubeEndFeature.Miter, "DoubleMiterOpposite")]
        [InlineData(TubeEndFeature.MiterOpposed, TubeEndFeature.MiterOpposed, "DoubleMiterOpposite")]
        [InlineData(TubeEndFeature.Miter, TubeEndFeature.Miter, "DoubleMiterFacing")]
        public void OpposedMitersAreToldApartFromFacingOnes(TubeEndFeature a, TubeEndFeature b, string expected)
        {
            Assert.Equal(expected, TubeEndStateText.FromEnds(a, b).ToString());
        }

        // "Opposed" only means something beside another miter; on its own it is one miter, and must
        // nest as one.
        [Theory]
        [InlineData(TubeEndFeature.MiterOpposed, TubeEndFeature.Unreviewed, "SingleMiter")]
        [InlineData(TubeEndFeature.MiterOpposed, TubeEndFeature.Clear, "SingleMiterClean")]
        public void ALoneOpposedMiterIsJustAMiter(TubeEndFeature a, TubeEndFeature b, string expected)
        {
            Assert.Equal(expected, TubeEndStateText.FromEnds(a, b).ToString());
        }

        [Fact]
        public void AnOpposedPairKeepsItsMeaningThroughASaveAndAReload()
        {
            var part = Stick(1);
            part.SetEnds(TubeEndFeature.Miter, TubeEndFeature.MiterOpposed);

            using var workbook = WriteSheet((1, part));
            var reread = Stick(1);
            EndFeaturesSheet.Read(workbook, new[] { reread }, new List<string>());

            Assert.Equal(TubeEndFeature.MiterOpposed, reread.EndB);
            Assert.Equal(TubeEndState.DoubleMiterOpposite, reread.EndState);
        }

        // A part read from the old TUBE END column must show ends that map straight back to the
        // same state, so opening and editing a legacy BOM cannot change how it nests.
        [Theory]
        [InlineData("Unreviewed")]
        [InlineData("Clean")]
        [InlineData("SingleMiter")]
        [InlineData("SingleMiterClean")]
        [InlineData("DoubleMiterFacing")]
        [InlineData("DoubleMiterOpposite")]
        public void EveryLegacyStateRoundTripsThroughItsEnds(string stateName)
        {
            var state = Enum.Parse<TubeEndState>(stateName);
            var (a, b) = TubeEndStateText.ToEnds(state);

            Assert.Equal(state, TubeEndStateText.FromEnds(a, b));
        }

        [Fact]
        public void SettingTheEndsUpdatesTheStateTheNestReads()
        {
            var part = Stick(1);

            part.SetEnds(TubeEndFeature.Miter, TubeEndFeature.Clear);

            Assert.Equal(TubeEndState.SingleMiterClean, part.EndState);
            Assert.Equal(TubeEndFeature.Miter, part.EndA);
            Assert.Equal(TubeEndFeature.Clear, part.EndB);
        }

        [Fact]
        public void SettingTheStateSetsTheCanonicalEnds()
        {
            var part = Stick(1);

            part.EndState = TubeEndState.SingleMiter;

            Assert.Equal(TubeEndFeature.Miter, part.EndA);
            Assert.Equal(TubeEndFeature.Unreviewed, part.EndB);
        }

        // The point of "Has features" as its own choice: it says the part was looked at, but it
        // nests exactly like a part nobody has looked at.
        [Fact]
        public void HasFeaturesNestsLikeUnreviewed()
        {
            var unreviewed = new TNest { StickLength = 240f };
            unreviewed.Parts.Add(Stick(1, length: 239f));
            unreviewed.Nest();

            var features = new TNest { StickLength = 240f };
            var part = Stick(1, length: 239f);
            part.SetEnds(TubeEndFeature.HasFeatures, TubeEndFeature.HasFeatures);
            features.Parts.Add(part);
            features.Nest();

            Assert.Equal(unreviewed.UnnestedList.Count, features.UnnestedList.Count);
            Assert.Single(features.UnnestedList);
        }

        // One clear end is enough: it goes toward the clamp, and the end with features sits beside
        // the next part instead.
        [Fact]
        public void OneClearEndLetsAPartUseTheClampZoneEvenWithFeaturesAtTheOther()
        {
            var nest = new TNest { StickLength = 240f };
            var part = Stick(1, length: 239f);
            part.SetEnds(TubeEndFeature.Clear, TubeEndFeature.HasFeatures);
            nest.Parts.Add(part);

            nest.Nest();

            Assert.Empty(nest.UnnestedList);
            Assert.Equal(1, nest.StickCount);
        }

        [Theory]
        [InlineData("Miter 45", TubeEndFeature.Miter)]
        [InlineData("45", TubeEndFeature.Miter)]
        [InlineData("miter", TubeEndFeature.Miter)]
        [InlineData("Miter 45 opposed", TubeEndFeature.MiterOpposed)]
        [InlineData("45x45", TubeEndFeature.MiterOpposed)]
        [InlineData("Clear", TubeEndFeature.Clear)]
        [InlineData("TRUE", TubeEndFeature.Clear)]
        [InlineData("Has features", TubeEndFeature.HasFeatures)]
        [InlineData("", TubeEndFeature.Unreviewed)]
        [InlineData("Unreviewed", TubeEndFeature.Unreviewed)]
        public void TheSheetsValuesAreReadTolerantly(string text, TubeEndFeature expected)
        {
            Assert.Equal(expected, TubeEndFeatureText.Parse(text));
        }

        [Fact]
        public void EveryDropdownOptionParsesBackToItself()
        {
            foreach (TubeEndFeature feature in Enum.GetValues<TubeEndFeature>())
                Assert.Equal(feature, TubeEndFeatureText.Parse(TubeEndFeatureText.Format(feature)));
        }

        // ---- the sheet

        private static XLWorkbook WriteSheet(params (int Item, Part Part)[] parts)
        {
            var workbook = new XLWorkbook();
            workbook.AddWorksheet("BOM");
            EndFeaturesSheet.Write(workbook, parts);
            return workbook;
        }

        [Fact]
        public void EndsSurviveAWriteAndARead()
        {
            var written = Stick(3);
            written.SetEnds(TubeEndFeature.Miter, TubeEndFeature.HasFeatures);
            var untouched = Stick(4, length: 30f);

            using var workbook = WriteSheet((3, written), (4, untouched));

            var reread = Stick(3);
            var warnings = new List<string>();
            EndFeaturesSheet.Read(workbook, new[] { reread }, warnings);

            Assert.Empty(warnings);
            Assert.Equal(TubeEndFeature.Miter, reread.EndA);
            Assert.Equal(TubeEndFeature.HasFeatures, reread.EndB);
            Assert.Equal(TubeEndState.SingleMiter, reread.EndState);
        }

        // Nothing recorded means nothing written: an untouched BOM stays as it was.
        [Fact]
        public void NoSheetIsWrittenWhenNothingIsRecorded()
        {
            using var workbook = WriteSheet((1, Stick(1)), (2, Stick(2)));

            Assert.False(workbook.TryGetWorksheet(EndFeaturesSheet.SheetName, out _));
        }

        // Putting everything back to Unreviewed and saving removes the sheet rather than leaving
        // the old ends behind to be read next time.
        [Fact]
        public void AnOldSheetIsRemovedWhenEverythingIsUnreviewedAgain()
        {
            var part = Stick(1);
            part.SetEnds(TubeEndFeature.Clear, TubeEndFeature.Clear);
            using var workbook = WriteSheet((1, part));
            Assert.True(workbook.TryGetWorksheet(EndFeaturesSheet.SheetName, out _));

            part.SetEnds(TubeEndFeature.Unreviewed, TubeEndFeature.Unreviewed);
            EndFeaturesSheet.Write(workbook, new[] { (1, part) });

            Assert.False(workbook.TryGetWorksheet(EndFeaturesSheet.SheetName, out _));
        }

        [Fact]
        public void SheetPartsAreNotWrittenToTheSheet()
        {
            var stick = Stick(1);
            stick.SetEnds(TubeEndFeature.Clear, TubeEndFeature.Clear);
            var sheetPart = new Part(2, 1, "Sheet 11GA", "S-1", 24f, 48f, 0f);
            sheetPart.SetEnds(TubeEndFeature.Miter, TubeEndFeature.Miter);

            using var workbook = WriteSheet((1, stick), (2, sheetPart));

            var ws = workbook.Worksheet(EndFeaturesSheet.SheetName);
            Assert.Equal(2, ws.LastRowUsed()!.RowNumber());
        }

        // The BOM was edited after the sheet was written: the row no longer describes the same
        // part, so it must not be applied to it.
        [Fact]
        public void ARowForAChangedPartIsIgnoredWithAWarning()
        {
            var written = Stick(1, length: 60f);
            written.SetEnds(TubeEndFeature.Miter, TubeEndFeature.Miter);
            using var workbook = WriteSheet((1, written));

            var changed = Stick(1, length: 72f);
            var warnings = new List<string>();
            EndFeaturesSheet.Read(workbook, new[] { changed }, warnings);

            Assert.Single(warnings);
            Assert.Contains("item(s) 1", warnings[0], StringComparison.Ordinal);
            Assert.Equal(TubeEndFeature.Unreviewed, changed.EndA);
        }

        [Fact]
        public void ABomWithNoSheetLeavesTheLegacyColumnsValuesAlone()
        {
            using var workbook = new XLWorkbook();
            workbook.AddWorksheet("BOM");

            var part = Stick(1);
            part.EndState = TubeEndState.SingleMiterClean;
            var warnings = new List<string>();

            EndFeaturesSheet.Read(workbook, new[] { part }, warnings);

            Assert.Empty(warnings);
            Assert.Equal(TubeEndState.SingleMiterClean, part.EndState);
            Assert.Equal(TubeEndFeature.Miter, part.EndA);
            Assert.Equal(TubeEndFeature.Clear, part.EndB);
        }

        [Fact]
        public void AnUnrecognizedValueIsReadAsUnreviewedAndSaid()
        {
            using var workbook = new XLWorkbook();
            workbook.AddWorksheet("BOM");
            var written = Stick(1);
            written.SetEnds(TubeEndFeature.Miter, TubeEndFeature.Clear);
            EndFeaturesSheet.Write(workbook, new[] { (1, written) });
            workbook.Worksheet(EndFeaturesSheet.SheetName).Cell(2, 5).SetValue("wibble");

            var part = Stick(1);
            var warnings = new List<string>();
            EndFeaturesSheet.Read(workbook, new[] { part }, warnings);

            Assert.Single(warnings);
            Assert.Contains("wibble", warnings[0], StringComparison.Ordinal);
            Assert.Equal(TubeEndFeature.Unreviewed, part.EndA);
            Assert.Equal(TubeEndFeature.Clear, part.EndB);
        }

        [Fact]
        public void TheEffectTextDistinguishesTheCasesThatNestDifferently()
        {
            var texts = new[]
            {
                TubeEndStateText.Effect(TubeEndFeature.Unreviewed, TubeEndFeature.Unreviewed),
                TubeEndStateText.Effect(TubeEndFeature.HasFeatures, TubeEndFeature.Unreviewed),
                TubeEndStateText.Effect(TubeEndFeature.Clear, TubeEndFeature.Unreviewed),
                TubeEndStateText.Effect(TubeEndFeature.Miter, TubeEndFeature.Unreviewed),
                TubeEndStateText.Effect(TubeEndFeature.Miter, TubeEndFeature.Clear),
                TubeEndStateText.Effect(TubeEndFeature.Miter, TubeEndFeature.Miter),
                TubeEndStateText.Effect(TubeEndFeature.Miter, TubeEndFeature.MiterOpposed),
            };

            Assert.Equal(texts.Length, texts.Distinct().Count());
        }
    }
}
