using System.Collections.Generic;
using Xunit;

namespace Cutwright.Tests
{
    // Spacing the estimator typed. The fault these guard is silence: an unusable figure used to
    // leave the previous value in place with nothing said, so the box showed one spacing while the
    // nest was built to another.
    public class SpacingInputTests
    {
        [Theory]
        [InlineData("0.25", 0.25f)]
        [InlineData("1", 1f)]
        [InlineData(" 0.375 ", 0.375f)]
        public void AUsableFigureIsTakenAsTyped(string text, float expected)
        {
            var notes = new List<string>();

            Assert.Equal(expected, SpacingInput.Read(text, "Part Spacing", notes));
            Assert.Empty(notes);
        }

        // An empty box is not a mistake - it means take the default - so it must not produce a
        // warning the estimator has to dismiss on every load.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void AnEmptyBoxTakesTheDefaultSilently(string? text)
        {
            var notes = new List<string>();

            Assert.Equal(SpacingInput.Default, SpacingInput.Read(text, "Part Spacing", notes));
            Assert.Empty(notes);
        }

        // The cases that used to pass through unnoticed. Each must fall back to the default AND
        // say so - falling back quietly is the original bug.
        [Theory]
        [InlineData("0.125\"")]
        [InlineData("1/8")]
        [InlineData("abc")]
        [InlineData("-0.25")]
        [InlineData("0")]
        public void AnUnusableFigureFallsBackAndSaysSo(string text)
        {
            var notes = new List<string>();

            Assert.Equal(SpacingInput.Default, SpacingInput.Read(text, "Part Spacing", notes));
            Assert.Single(notes);
            Assert.Contains("Part Spacing", notes[0]);
        }

        [Fact]
        public void TheNoteNamesWhichBoxWasWrong()
        {
            var notes = new List<string>();

            SpacingInput.Read("abc", "Sheet Spacing", notes);
            SpacingInput.Read("-1", "Part Spacing", notes);

            Assert.Equal(2, notes.Count);
            Assert.Contains("Sheet Spacing", notes[0]);
            Assert.Contains("Part Spacing", notes[1]);
        }

        // Zero is rejected deliberately: shearing really does consume nothing, but that belongs to
        // the material policy, which applies it per material. Typed here it is far more likely a
        // slip, and parts touching by accident is not a nest anyone asked for.
        [Fact]
        public void ZeroIsRejectedRatherThanTakenLiterally()
        {
            var notes = new List<string>();

            Assert.Equal(SpacingInput.Default, SpacingInput.Read("0", "Part Spacing", notes));
            Assert.Contains("greater than zero", notes[0]);
        }

        [Fact]
        public void ADifferentDefaultIsUsedForABlankOrUnusableBox()
        {
            var notes = new List<string>();

            Assert.Equal(4.5f, SpacingInput.Read("", "Min cut length", notes, 4.5f, allowZero: true));
            Assert.Equal(4.5f, SpacingInput.Read("abc", "Min cut length", notes, 4.5f, allowZero: true));
            Assert.Single(notes);
        }

        [Fact]
        public void ZeroIsAcceptedOnlyWhenTheFigureAllowsIt()
        {
            var allowed = new List<string>();
            var refused = new List<string>();

            Assert.Equal(0f, SpacingInput.Read("0", "Min cut length", allowed, 4.5f, allowZero: true));
            Assert.Empty(allowed);

            Assert.Equal(0.125f, SpacingInput.Read("0", "Kerf", refused, 0.125f));
            Assert.Single(refused);
        }

        [Fact]
        public void ANegativeIsRefusedEvenWhereZeroIsAllowed()
        {
            var notes = new List<string>();

            Assert.Equal(4.5f, SpacingInput.Read("-1", "Min cut length", notes, 4.5f, allowZero: true));
            Assert.Single(notes);
        }
    }
}
