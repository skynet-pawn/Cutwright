using System;
using System.IO;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    // That the log actually reaches disk.
    //
    // Worth a test despite being a handful of lines, because Log is written to never throw: if it
    // could not create its folder or open its file it would fail silently, which is exactly the
    // behaviour wanted in production and exactly the behaviour that would leave everything that
    // depends on it quietly useless. The whole point of adding it was that support questions had
    // nothing to look at.
    //
    // Writes to the real log location, which is where the application's own entries go, so this
    // adds a couple of lines to today's file rather than needing somewhere special.
    public sealed class LogTests
    {
        [Fact]
        public void WhatIsLoggedEndsUpInAFileThatCanBeFound()
        {
            string marker = $"LogTests probe {Guid.NewGuid():N}";

            Log.Info(marker);

            Assert.NotNull(Log.CurrentFile);
            Assert.True(File.Exists(Log.CurrentFile),
                $"Log reported writing to '{Log.CurrentFile}' but no such file exists.");

            // Opened share-friendly: the application may well have the same file open.
            using var stream = new FileStream(Log.CurrentFile!, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            Assert.Contains(marker, reader.ReadToEnd(), StringComparison.Ordinal);
        }

        [Fact]
        public void AnExceptionIsRecordedWithItsStackNotJustItsMessage()
        {
            string marker = $"LogTests failure {Guid.NewGuid():N}";

            Exception captured;
            try
            {
                throw new InvalidOperationException(marker);
            }
            catch (Exception e)
            {
                captured = e;
            }

            Log.Error("Probing", captured);

            using var stream = new FileStream(Log.CurrentFile!, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string contents = reader.ReadToEnd();

            Assert.Contains(marker, contents, StringComparison.Ordinal);

            // The stack is the part a dialog could never carry and the reason this exists.
            Assert.Contains(nameof(AnExceptionIsRecordedWithItsStackNotJustItsMessage), contents,
                StringComparison.Ordinal);
        }
    }
}
