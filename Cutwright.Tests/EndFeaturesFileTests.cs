using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The End Features sheet on real files: written with the BOM, read back by the parser, and
    // carried through a Save. The unit tests cover the sheet in memory; these cover the three
    // places it is wired in.
    public sealed class EndFeaturesFileTests : IDisposable
    {
        private readonly string directory;

        public EndFeaturesFileTests()
        {
            directory = Path.Combine(Path.GetTempPath(), "Cutwright.Tests.Ends", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static BillOfMaterials TwoSticks()
        {
            var bom = new BillOfMaterials { Units = 1, Description = "Test job" };

            var first = new Part(1, 2, "SQ Tube 2 x 2 x 11 GA", "P-1", 0f, 60f, 0f) { PerUnitQuantity = 2 };
            first.SetEnds(TubeEndFeature.Miter, TubeEndFeature.HasFeatures);

            var second = new Part(2, 1, "SQ Tube 2 x 2 x 11 GA", "P-2", 0f, 30f, 0f) { PerUnitQuantity = 1 };

            bom.parts.Add(first);
            bom.parts.Add(second);
            return bom;
        }

        private string WriteBom(BillOfMaterials bom, string name)
        {
            string path = Path.Combine(directory, name);
            bom.WriteToFile(path);
            Assert.Empty(bom.Errors);
            return path;
        }

        [Fact]
        public void EndsWrittenWithTheBomAreReadBackByTheParser()
        {
            string path = WriteBom(TwoSticks(), "ends.xlsx");

            var parser = new FileParser();
            parser.Parse(path);

            Assert.Empty(parser.Errors);
            var parts = parser.TNestList.SelectMany(n => n.Parts).OrderBy(p => p.line).ToList();
            Assert.Equal(2, parts.Count);
            Assert.Equal(TubeEndFeature.Miter, parts[0].EndA);
            Assert.Equal(TubeEndFeature.HasFeatures, parts[0].EndB);
            Assert.Equal(TubeEndState.SingleMiter, parts[0].EndState);
            Assert.Equal(TubeEndFeature.Unreviewed, parts[1].EndA);
        }

        // The tab's edits reach the file through Save.
        [Fact]
        public void EndsEditedAfterLoadingArePersistedBySave()
        {
            string source = WriteBom(TwoSticks(), "source.xlsx");

            var parser = new FileParser();
            parser.Parse(source);

            var second = parser.TNestList.SelectMany(n => n.Parts).Single(p => p.line == 2);
            second.SetEnds(TubeEndFeature.Clear, TubeEndFeature.Miter);
            foreach (var nest in parser.TNestList)
                nest.Nest();

            string dest = Path.Combine(directory, "saved.xlsx");
            var writer = new FileWriter();
            writer.SendToFile(dest, parser.PNestList, parser.TNestList, source);
            Assert.Empty(writer.Errors);

            var reloaded = new FileParser();
            reloaded.Parse(dest);

            var parts = reloaded.TNestList.SelectMany(n => n.Parts).OrderBy(p => p.line).ToList();
            Assert.Equal(TubeEndFeature.Miter, parts[0].EndA);
            Assert.Equal(TubeEndFeature.HasFeatures, parts[0].EndB);
            Assert.Equal(TubeEndFeature.Clear, parts[1].EndA);
            Assert.Equal(TubeEndFeature.Miter, parts[1].EndB);
        }
    }
}
