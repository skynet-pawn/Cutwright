using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cutwright
{
    // One row of the Nested Parts List grid. Carries its own stock options and current selection
    // so the selector can live in the row itself: the grid virtualizes rows, so the ComboBox
    // controls come and go as you scroll and can't be tracked in a list the way the old sidebar
    // ones were. StockChanged routes a pick back to whichever nest this row represents.
    // Public because WPF binding against a non-public source class is a documented grey area -
    // reads happen to work, writes are less dependable, and there's nothing gained by hiding it.
    public class NestedItemsGridView : INotifyPropertyChanged
    {
        public string? Description { get; set; }

        // Sheet sizes for 2D groups, stick lengths for 1D - so the options have to be per row
        // rather than shared across the column. Settable (not just init) so a Smallest Drop row
        // can be given its freshly computed size after a re-nest - see SetCurrentStock.
        private IReadOnlyList<string> _stockOptions = Array.Empty<string>();
        public IReadOnlyList<string> StockOptions
        {
            get => _stockOptions;
            set { if (_stockOptions != value) { _stockOptions = value; OnPropertyChanged(); } }
        }

        // Invoked when the user picks a different stock size. Left null while the grid is being
        // built so seeding the initial selection doesn't trigger a re-nest.
        public Action<string>? StockChanged { get; set; }

        private string? _selectedStock;
        public string? SelectedStock
        {
            get => _selectedStock;
            set
            {
                if (_selectedStock == value)
                    return;

                _selectedStock = value;
                OnPropertyChanged();

                if (value != null)
                    StockChanged?.Invoke(value);
            }
        }

        // Updates the stock column to reflect a size the code computed rather than the user
        // picked - a Smallest Drop group's actual dimensions, recomputed on every re-nest. Goes
        // straight at the backing fields rather than through the SelectedStock setter, so
        // refreshing the display never invokes StockChanged: that would feed the shown text back
        // in as if it had been picked from the dropdown, and for a drop whose size does not
        // literally match SmallestDropOption's text, ApplySheetSizeChoice/ApplyStickLengthChoice
        // would read that as an unparseable size and drop the group back to a fixed one.
        public void SetCurrentStock(IReadOnlyList<string> options, string selected)
        {
            StockOptions = options;

            if (_selectedStock == selected)
                return;

            _selectedStock = selected;
            OnPropertyChanged(nameof(SelectedStock));
        }

        // Called from the ComboBox's own SelectionChanged, so a pick takes effect whether or not
        // the SelectedItem binding manages to write back to the source - inside a DataGrid
        // template column it may not, and a ComboBox shows the clicked item either way, so a
        // failed write-back looks exactly like success while nothing downstream happens.
        //
        // A no-op when the value already matches, which covers both the write-back having
        // already landed (so StockChanged has fired once, not twice) and a virtualized row being
        // re-realized, where the binding re-selects the existing value and would otherwise look
        // like a fresh pick.
        public void ApplyUserSelection(string? choice)
        {
            if (choice == null || _selectedStock == choice)
                return;

            SelectedStock = choice;
        }

        private string _stockCount = "";
        public string StockCount
        {
            get => _stockCount;
            set { if (_stockCount != value) { _stockCount = value; OnPropertyChanged(); } }
        }

        private string _efficiency = "";
        public string Efficiency
        {
            get => _efficiency;
            set { if (_efficiency != value) { _efficiency = value; OnPropertyChanged(); } }
        }

        private string _unnestedCount = "";
        public string UnnestedCount
        {
            get => _unnestedCount;
            set
            {
                if (_unnestedCount == value)
                    return;

                _unnestedCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnnested));
            }
        }

        // True when parts in this group did not fit, which the grid shows as a tinted row. Derived
        // from the same text the column shows rather than stored, so the two cannot disagree.
        public bool HasUnnested => int.TryParse(_unnestedCount, out int count) && count > 0;

        // The material a group is nested under. This used to be a read-only text column, which
        // meant the guess made
        // from the BOM's free-text description was final: a description that guessed wrong nested
        // the whole group under the wrong cutting rules, with no way for anyone to correct it. The
        // rules differ enough to change the sheet count - rotation locks, edge allowance, minimum
        // part spacing - so a wrong guess is a wrong quote, not a cosmetic mislabel.
        public IReadOnlyList<string> MaterialOptions { get; init; } = Array.Empty<string>();

        // Invoked when the user picks a different material. Left null while the grid is being
        // built so seeding the initial selection doesn't trigger a re-nest.
        public Action<string>? MaterialChanged { get; set; }

        private string? _selectedMaterial;
        public string? SelectedMaterial
        {
            get => _selectedMaterial;
            set
            {
                if (_selectedMaterial == value)
                    return;

                _selectedMaterial = value;
                OnPropertyChanged();

                if (value != null)
                    MaterialChanged?.Invoke(value);
            }
        }

        // Same reasoning as ApplyUserSelection, for the material column's ComboBox.
        public void ApplyMaterialSelection(string? choice)
        {
            if (choice == null || _selectedMaterial == choice)
                return;

            SelectedMaterial = choice;
        }

        // False while a nest is running. Picking a stock size or a material re-nests that group on
        // the UI thread, and the background run is nesting the very same objects, so both would be
        // rebuilding one group's Sheets list at once - a torn sheet count, or a drawing that does
        // not match it.
        //
        // Carried per row rather than by disabling the grid, so the list stays readable and
        // scrollable while it nests, and re-applies itself when a virtualized row comes back.
        private bool _selectorsEnabled = true;
        public bool SelectorsEnabled
        {
            get => _selectorsEnabled;
            set { if (_selectorsEnabled != value) { _selectorsEnabled = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
