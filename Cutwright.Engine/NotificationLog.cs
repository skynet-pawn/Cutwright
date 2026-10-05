using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cutwright
{
    public enum NotificationLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    // One line in the notification log. Public, like NestedItemsGridView, because WPF binding
    // against a non-public source class is a documented grey area.
    public sealed class NotificationEntry : INotifyPropertyChanged
    {
        public NotificationEntry(DateTime time, NotificationLevel level, string title, string message)
        {
            Time = time;
            Level = level;
            Title = title;
            Message = message;
        }

        public DateTime Time { get; }
        public NotificationLevel Level { get; }
        public string Title { get; }
        public string Message { get; }

        public string TimeText => Time.ToString("h:mm:ss tt", System.Globalization.CultureInfo.CurrentCulture);

        // True until the log has been looked at, so the list can set what is new apart from what
        // was already seen.
        private bool _isUnread = true;
        public bool IsUnread
        {
            get => _isUnread;
            internal set
            {
                if (_isUnread == value)
                    return;

                _isUnread = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnread)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString() =>
            $"[{TimeText}] {Level}: {Title}{Environment.NewLine}{Message}";
    }

    // Where messages go instead of a dialog. A message box stops the estimator's work to say
    // something that mostly does not need an answer - a note about the BOM, a finished export - so
    // those are collected here and shown from the bell in the status bar when someone wants them.
    // Failures still get a dialog (work stopped, the user must know) and are recorded here as well,
    // so the log is the complete list of what the program said this session.
    //
    // No WPF dependency, so it can be tested without a window.
    public sealed class NotificationLog : INotifyPropertyChanged
    {
        // Enough for a long session; a runaway source cannot grow the list without bound.
        public const int MaxEntries = 200;

        public ObservableCollection<NotificationEntry> Entries { get; } = new();

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            private set { if (_unreadCount != value) { _unreadCount = value; OnPropertyChanged(); } }
        }

        // The most serious thing not yet seen, which colours the badge. Info when nothing is
        // unread - it is only shown while UnreadCount is above zero.
        private NotificationLevel _worstUnread;
        public NotificationLevel WorstUnread
        {
            get => _worstUnread;
            private set { if (_worstUnread != value) { _worstUnread = value; OnPropertyChanged(); } }
        }

        public NotificationEntry Add(NotificationLevel level, string title, string message)
        {
            var entry = new NotificationEntry(DateTime.Now, level, title, message.Trim());

            // Newest first, so what just happened is at the top without scrolling.
            Entries.Insert(0, entry);

            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(Entries.Count - 1);

            Recount();
            return entry;
        }

        public void MarkAllRead()
        {
            foreach (var entry in Entries)
                entry.IsUnread = false;

            Recount();
        }

        public void Clear()
        {
            Entries.Clear();
            Recount();
        }

        // Everything on the log as plain text, for pasting into an email when someone asks what
        // it said.
        public string ToText() =>
            string.Join(Environment.NewLine + Environment.NewLine, Entries.Select(e => e.ToString()));

        private void Recount()
        {
            var unread = Entries.Where(e => e.IsUnread).ToList();
            UnreadCount = unread.Count;
            WorstUnread = unread.Count == 0 ? NotificationLevel.Info : unread.Max(e => e.Level);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
