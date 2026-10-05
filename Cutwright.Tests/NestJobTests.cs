using System.IO;
using Xunit;

namespace Cutwright.Tests
{
    public class NestJobTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cutwright-nestjob-" + Guid.NewGuid().ToString("N"));

        public NestJobTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void WriteThenRun_NestsSheetPartsAndWritesBuySheetAndDxf()
        {
            string bom = Path.Combine(_dir, "Job.xlsx");
            var errors = NestJob.WriteBom(bom, new BomJobInfo { Customer = "Test", Units = 2 },
                new[] { new BomLineInput("P-1", "Sheet 14GA HR", 3, 20, 10) });
            Assert.Empty(errors);

            string buy = Path.Combine(_dir, "Job Buy Sheet.xlsx");
            var result = NestJob.Run(bom, buy, Path.Combine(_dir, "Nested"), new NestJobOptions());

            Assert.True(result.Succeeded, string.Join("; ", result.Errors));
            var group = Assert.Single(result.SheetGroups);
            Assert.Equal(1, group.SheetCount);
            Assert.Equal(6, group.PartCount);          // 3 per unit x 2 units
            Assert.Equal(0, group.UnnestedPartCount);
            Assert.True(File.Exists(buy));
            Assert.Contains(result.Purchase, p => p.Description.Contains("14GA", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Files, f => f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) && File.Exists(f));
        }

        [Fact]
        public void Run_ReportsAnUnreadableBomInsteadOfThrowing()
        {
            string notABom = Path.Combine(_dir, "nothing.xlsx");
            var result = NestJob.Run(notABom, Path.Combine(_dir, "b.xlsx"), _dir, new NestJobOptions());
            Assert.False(result.Succeeded);
        }
    }
}
