using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Cutwright.Tests
{
    // Filename policy for the DXF export. The failure this guards against was not cosmetic: a
    // fraction slash surviving into Path.Combine turned the filename into a path through a
    // directory that was never created, and the export died on any job with a fractional
    // thickness in the group description.
    public class NestFileNamingTests
    {
        // Real descriptions out of the sample BOMs in the repo, which is where the slashes and
        // colons that broke it came from.
        [Theory]
        [InlineData("3/4 #9 STD EXP METAL")]
        [InlineData("BASE PLATE A 3/16 x 3 x 61-1/8 A-36")]
        [InlineData("MOUNT PLATE 1/4 x 4 x 5-1/8 A-36")]
        [InlineData("WELDMENT: FRAME SEE SHEET #5")]
        [InlineData(">3/4 #9 STD EXP METAL")]
        [InlineData("1/4\" PLATE A36")]
        [InlineData(".25 X 48 X 96 PLATE, HR")]
        [InlineData("FOAM-2.00x4.00x24.00")]
        [InlineData("3/8\"-UNC Nylock Nut - Zinc Plated")]
        [InlineData("CON*TROL?CHARS|HERE<>")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("...")]
        [InlineData("///")]
        public void NameIsAlwaysAPlainFilenameComponent(string description)
        {
            string safe = NestFileNaming.SafeFileNamePart(description);

            Assert.NotEmpty(safe);
            Assert.Equal(-1, safe.IndexOfAny(Path.GetInvalidFileNameChars()));

            // The real requirement: combining it must land in the target directory, not in some
            // subdirectory implied by a surviving separator.
            string combined = Path.Combine(@"C:\out", $"job_{safe}_1.dxf");
            Assert.Equal(@"C:\out", Path.GetDirectoryName(combined));
        }

        [Fact]
        public void FractionSlashesBecomeSeparatorsRatherThanDirectories()
        {
            Assert.Equal("3_4_#9_STD_EXP_METAL", NestFileNaming.SafeFileNamePart("3/4 #9 STD EXP METAL"));
        }

        // Periods are legal in a filename and a thickness reads better as ".25 X 48" than
        // "_25_X_48", so they are kept - except at the very end, where Windows rejects them.
        [Fact]
        public void PeriodsAreKeptButNotLeftTrailing()
        {
            Assert.Equal(".25_X_48_X_96_PLATE,_HR", NestFileNaming.SafeFileNamePart(".25 X 48 X 96 PLATE, HR"));
            Assert.Equal("PLATE", NestFileNaming.SafeFileNamePart("PLATE..."));
        }

        [Fact]
        public void RunsOfIllegalCharactersCollapse()
        {
            Assert.Equal("A_B", NestFileNaming.SafeFileNamePart("A  ///  B"));
        }

        [Fact]
        public void DescriptionOfPurePunctuationFallsBackToAName()
        {
            Assert.Equal("Sheet", NestFileNaming.SafeFileNamePart("///"));
            Assert.Equal("Sheet", NestFileNaming.SafeFileNamePart(null));
        }

        // A long description plus a deep save path could otherwise push the whole thing past the
        // maximum path length.
        [Fact]
        public void LongDescriptionsAreCapped()
        {
            string safe = NestFileNaming.SafeFileNamePart(new string('A', 400));

            Assert.Equal(60, safe.Length);
        }

        // Two different descriptions can scrub down to the same name - a slash, a colon and an
        // underscore all end up as the same separator. Without a suffix the second group's files
        // would quietly overwrite the first group's.
        [Fact]
        public void CollidingNamesGetDistinctSuffixes()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string first = NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart("3/4 PLATE"), used);
            string second = NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart("3:4 PLATE"), used);
            string third = NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart("3_4 PLATE"), used);

            Assert.Equal("3_4_PLATE", first);
            Assert.Equal("3_4_PLATE_2", second);
            Assert.Equal("3_4_PLATE_3", third);
        }

        // Two groups that genuinely share a description collide as well, and must not share files.
        [Fact]
        public void RepeatedDescriptionGetsItsOwnName()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string description = "1/4 PLATE A36";

            Assert.Equal("1_4_PLATE_A36",
                NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart(description), used));
            Assert.Equal("1_4_PLATE_A36_2",
                NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart(description), used));
        }

        // Hyphens are legal in a filename, so they survive - which means "3-4 PLATE" is a distinct
        // name from "3/4 PLATE" rather than a collision with it.
        [Fact]
        public void LegalPunctuationSurvives()
        {
            Assert.Equal("3-4_PLATE", NestFileNaming.SafeFileNamePart("3-4 PLATE"));
            Assert.Equal("A36_PLATE_#9", NestFileNaming.SafeFileNamePart("A36 PLATE #9"));
        }

        [Fact]
        public void DistinctNamesAreLeftAlone()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Assert.Equal("A_PLATE", NestFileNaming.UniqueName("A_PLATE", used));
            Assert.Equal("B_PLATE", NestFileNaming.UniqueName("B_PLATE", used));
        }
    }
}
