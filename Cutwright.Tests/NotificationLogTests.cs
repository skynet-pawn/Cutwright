using System;
using System.Linq;
using Cutwright;
using Xunit;

namespace Cutwright.Tests
{
    public sealed class NotificationLogTests
    {
        [Fact]
        public void AddingCountsAsUnreadAndPutsNewestFirst()
        {
            var log = new NotificationLog();

            log.Add(NotificationLevel.Info, "First", "one");
            log.Add(NotificationLevel.Warning, "Second", "two");

            Assert.Equal(2, log.UnreadCount);
            Assert.Equal("Second", log.Entries[0].Title);
            Assert.Equal(NotificationLevel.Warning, log.WorstUnread);
        }

        [Fact]
        public void MarkingReadClearsTheCountButKeepsTheEntries()
        {
            var log = new NotificationLog();
            log.Add(NotificationLevel.Error, "Bad", "it broke");

            log.MarkAllRead();

            Assert.Equal(0, log.UnreadCount);
            Assert.Equal(NotificationLevel.Info, log.WorstUnread);
            Assert.Single(log.Entries);
            Assert.False(log.Entries[0].IsUnread);
        }

        [Fact]
        public void WorstUnreadIgnoresWhatWasAlreadySeen()
        {
            var log = new NotificationLog();
            log.Add(NotificationLevel.Error, "Old", "seen");
            log.MarkAllRead();

            log.Add(NotificationLevel.Info, "New", "fresh");

            Assert.Equal(1, log.UnreadCount);
            Assert.Equal(NotificationLevel.Info, log.WorstUnread);
        }

        [Fact]
        public void ClearEmptiesTheLog()
        {
            var log = new NotificationLog();
            log.Add(NotificationLevel.Info, "x", "y");

            log.Clear();

            Assert.Empty(log.Entries);
            Assert.Equal(0, log.UnreadCount);
        }

        [Fact]
        public void TheLogIsCappedAndDropsTheOldest()
        {
            var log = new NotificationLog();

            for (int i = 0; i < NotificationLog.MaxEntries + 5; i++)
                log.Add(NotificationLevel.Info, "n" + i, "m");

            Assert.Equal(NotificationLog.MaxEntries, log.Entries.Count);
            Assert.Equal("n" + (NotificationLog.MaxEntries + 4), log.Entries[0].Title);
            Assert.DoesNotContain(log.Entries, e => e.Title == "n0");
        }

        [Fact]
        public void ToTextListsEveryEntry()
        {
            var log = new NotificationLog();
            log.Add(NotificationLevel.Warning, "Spacing", "used the default");

            string text = log.ToText();

            Assert.Contains("Warning: Spacing", text);
            Assert.Contains("used the default", text);
        }
    }
}
