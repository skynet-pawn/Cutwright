using System.IO;
using Xunit;

namespace Cutwright.Tests
{
    public class StickNestPreviewTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cutwright-sticks-" + Guid.NewGuid().ToString("N"));

        public StickNestPreviewTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private const string Tube = "SQ Tube 2 x 2 x 1/4 HR";

        private static StickPartInput Piece(string number, int quantity, double length, bool miterStart = false, bool miterEnd = false) =>
            new(number, Tube, quantity, length, miterStart, miterEnd);

        [Fact]
        public void IdenticalSticksShareOneLayout()
        {
            var result = StickNestPreview.Nest(new[] { Piece("A", 10, 100) }, new StickNestSetup());

            Assert.Equal(5, result.StickCount);                   // two 100 in pieces and their kerf per 240 in stick
            var layout = Assert.Single(result.Layouts);
            Assert.Equal(5, layout.Count);
            Assert.Equal(2, layout.Cuts.Count);
            Assert.Equal(240 - 2 * (100 + TNest.DefaultKerf), layout.RemainingInches, 3);
            Assert.InRange(result.UtilizationPercent, 80, 85);
            Assert.Equal(0, result.UnnestedPieceCount);
        }

        [Fact]
        public void MixedPiecesMakeSeveralLayouts_LongestFirst()
        {
            var result = StickNestPreview.Nest(new[] { Piece("Long", 3, 150), Piece("Short", 3, 60) }, new StickNestSetup());

            Assert.Equal(3, result.StickCount);   // each 150 gets a stick and a 60 beside it
            Assert.Equal(3, result.Layouts.Sum(l => l.Count));
            Assert.All(result.Layouts, l => Assert.Equal("Long", l.Cuts[0].PartNumber));
        }

        [Fact]
        public void APieceLongerThanTheStockIsReportedNotNested()
        {
            var result = StickNestPreview.Nest(new[] { Piece("Huge", 2, 300) }, new StickNestSetup());

            Assert.Equal(2, result.UnnestedPieceCount);
            Assert.Contains("Huge", result.UnnestedPartNumbers);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void SmallestDropShortensTheStick()
        {
            var fixedLength = StickNestPreview.Nest(new[] { Piece("A", 2, 50) }, new StickNestSetup());
            var smallest = StickNestPreview.Nest(new[] { Piece("A", 2, 50) }, new StickNestSetup { SmallestDrop = true });

            Assert.True(smallest.StickLengthInches < fixedLength.StickLengthInches);
            Assert.Equal(1, smallest.StickCount);
        }

        [Fact]
        public void TheTubeLaserKeepsPlainEndsOutOfItsClampZone()
        {
            // Two 118 in pieces and kerf fit a 240 in stick on the saw; the laser sets 4.5 in aside for its clamp.
            var piece = new[] { Piece("A", 2, 118.9) };
            var saw = StickNestPreview.Nest(piece, new StickNestSetup { CutOnSaw = true });
            var laser = StickNestPreview.Nest(piece, new StickNestSetup { CutOnSaw = false });

            Assert.Equal(1, saw.StickCount);
            Assert.Equal(2, laser.StickCount);
        }

        [Fact]
        public void MiteredEndsAreCarriedToTheLayout()
        {
            var result = StickNestPreview.Nest(new[] { Piece("A", 1, 100, miterStart: true, miterEnd: false) }, new StickNestSetup());

            var cut = Assert.Single(Assert.Single(result.Layouts).Cuts);
            Assert.True(cut.MiterStart);
            Assert.False(cut.MiterEnd);
        }

        [Fact]
        public void AJobWithSticksUsesTheGroupsOwnSettings()
        {
            string bom = Path.Combine(_dir, "Job.xlsx");
            Assert.Empty(NestJob.WriteBom(bom, new BomJobInfo { Units = 1 },
                new[] { new BomLineInput("A", Tube, 3, 100, 0, MiterStart: true) }));

            var standard = NestJob.Run(bom, Path.Combine(_dir, "a.xlsx"), Path.Combine(_dir, "n1"), new NestJobOptions());
            Assert.True(standard.Succeeded, string.Join("; ", standard.Errors));
            var group = Assert.Single(standard.StickGroups);
            Assert.Equal(2, group.StickCount);                    // two pieces on one 240 in stick, one on the next
            Assert.Equal(3, group.PartCount);

            var own = NestJob.Run(bom, Path.Combine(_dir, "b.xlsx"), Path.Combine(_dir, "n2"), new NestJobOptions
            {
                StickGroups = new Dictionary<string, StickNestSetup> { [Tube] = new StickNestSetup { StickLengthInches = 120 } },
            });
            Assert.True(own.Succeeded, string.Join("; ", own.Errors));
            Assert.Equal(3, Assert.Single(own.StickGroups).StickCount);   // one per piece on 120 in stock
            Assert.Contains(own.Purchase, p => p.Description.Contains("SQ Tube", StringComparison.OrdinalIgnoreCase));
        }
    }
}
