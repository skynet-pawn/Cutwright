using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cutwright
{
    // One row of the End Features grid: one stick part line from the BOM, with a dropdown for each
    // end. Public, like NestedItemsGridView, because WPF binding against a non-public source class
    // is a documented grey area - the constructor stays internal because it takes internal types.
    //
    // The dropdowns route a pick through ApplyEndA/ApplyEndB from the ComboBox's own
    // SelectionChanged rather than trusting a two-way binding to write back, for the same reason
    // the Nested Parts List does: inside a DataGrid template column it may not, and a ComboBox
    // shows the clicked item either way, so a failed write-back looks exactly like success.
    public sealed class EndFeatureRow : INotifyPropertyChanged
    {
        private readonly Action<EndFeatureRow> _changed;

        internal EndFeatureRow(Part part, TNest group, Action<EndFeatureRow> changed)
        {
            Part = part;
            Group = group;
            _changed = changed;
        }

        internal Part Part { get; }

        // The material group this part is nested in, which is what a change to it has to renest.
        internal TNest Group { get; }

        public int Item => Part.line;
        public int Quantity => Part.quantity;
        public string PartNumber => Part.PartNumber ?? string.Empty;
        public string Description => Part.Description ?? string.Empty;
        public string LengthText => Part.length.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);

        public IReadOnlyList<string> Options => TubeEndFeatureText.Options;

        public string EndA => TubeEndFeatureText.Format(Part.EndA);
        public string EndB => TubeEndFeatureText.Format(Part.EndB);

        // What the two ends mean for nesting, in words.
        public string Effect => TubeEndStateText.Effect(Part.EndA, Part.EndB);

        // True when nobody has said anything about either end.
        public bool IsUnreviewed => Part.EndA == TubeEndFeature.Unreviewed
                                    && Part.EndB == TubeEndFeature.Unreviewed;

        // Called from the ComboBox's SelectionChanged. A no-op when the value already matches,
        // which covers the write-back having already landed and a virtualized row being
        // re-realized, where the binding re-selects the existing value and would otherwise look
        // like a fresh pick.
        public void ApplyEndA(string? choice) => ApplyUserPick(choice, isEndA: true);

        public void ApplyEndB(string? choice) => ApplyUserPick(choice, isEndA: false);

        private void ApplyUserPick(string? choice, bool isEndA)
        {
            if (choice == null)
                return;

            var feature = TubeEndFeatureText.Parse(choice);
            bool changed = isEndA ? SetEnds(feature, null) : SetEnds(null, feature);

            if (changed)
                _changed(this);
        }

        // Sets either or both ends without telling the window, so a bulk apply can change many rows
        // and renest each group once. Null leaves that end as it is. Returns whether anything changed.
        internal bool SetEnds(TubeEndFeature? a, TubeEndFeature? b)
        {
            var newA = a ?? Part.EndA;
            var newB = b ?? Part.EndB;

            if (newA == Part.EndA && newB == Part.EndB)
                return false;

            Part.SetEnds(newA, newB);
            Refresh();
            return true;
        }

        // Re-reads everything from the part - after a bulk change, or when Units changed the
        // quantity.
        public void Refresh()
        {
            OnPropertyChanged(nameof(Quantity));
            OnPropertyChanged(nameof(EndA));
            OnPropertyChanged(nameof(EndB));
            OnPropertyChanged(nameof(Effect));
            OnPropertyChanged(nameof(IsUnreviewed));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
