using System;
using System.IO;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // The settings file must never stop the program starting: missing, unreadable or malformed all
    // read as "nothing set", which the features that need a value then report by name.
    public sealed class CutwrightSettingsTests : IDisposable
    {
        private readonly string directory;

        public CutwrightSettingsTests()
        {
            directory = Path.Combine(Path.GetTempPath(), "Cutwright.Tests.Settings", Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void AMissingFileReadsAsAllBlank()
        {
            var settings = CutwrightSettings.Load(Path.Combine(directory, "nope.json"));

            Assert.Equal(string.Empty, settings[CutwrightSettings.TemplateFolder]);
            Assert.Equal(string.Empty, settings[CutwrightSettings.MaterialLibrary]);
        }

        [Fact]
        public void ValuesAreReadByName()
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ \"TemplateFolder\": \"D:\\\\Templates\", \"BendTable\": \"D:\\\\bend.xls\" }");

            var settings = CutwrightSettings.Load(path);

            Assert.Equal(@"D:\Templates", settings[CutwrightSettings.TemplateFolder]);
            Assert.Equal(@"D:\bend.xls", settings[CutwrightSettings.BendTable]);
            Assert.Equal(string.Empty, settings[CutwrightSettings.ShopProfileFolder]);
        }

        [Theory]
        [InlineData("this is not json")]
        [InlineData("[1, 2, 3]")]
        [InlineData("")]
        [InlineData("{ \"TemplateFolder\": 42 }")]
        public void AnUnusableFileReadsAsAllBlankRatherThanThrowing(string content)
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, content);

            var settings = CutwrightSettings.Load(path);

            Assert.Equal(string.Empty, settings[CutwrightSettings.TemplateFolder]);
        }

        [Fact]
        public void CommentsAndTrailingCommasAreTolerated()
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ // my templates\n \"TemplateFolder\": \"D:\\\\T\", }");

            Assert.Equal(@"D:\T", CutwrightSettings.Load(path)[CutwrightSettings.TemplateFolder]);
        }

        [Fact]
        public void EnsureFileWritesABlankFileToEditAndNeverOverwrites()
        {
            string path = Path.Combine(directory, "sub", "settings.json");

            Assert.True(CutwrightSettings.EnsureFile(path));
            Assert.True(File.Exists(path));
            Assert.Equal(string.Empty, CutwrightSettings.Load(path)[CutwrightSettings.TemplateFolder]);

            File.WriteAllText(path, "{ \"TemplateFolder\": \"D:\\\\Mine\" }");
            Assert.True(CutwrightSettings.EnsureFile(path));

            Assert.Equal(@"D:\Mine", CutwrightSettings.Load(path)[CutwrightSettings.TemplateFolder]);
        }
    }
}
