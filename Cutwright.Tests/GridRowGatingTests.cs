using System.Collections.Generic;
using Xunit;

namespace Cutwright.Tests
{
    // The row-level flag the stock and material selectors bind their IsEnabled to while a nest is
    // running. Picking either re-nests that group on the UI thread, and the background run is
    // nesting the same objects, so both would rebuild one group's Sheets list at once.
    //
    // The gating itself lives in MainWindow.SetBusy and needs a window to exercise, so what is
    // covered here is the part that can be: the flag notifies, so a virtualized row coming back
    // into view picks up the current state rather than the state it was created with.
    public class GridRowGatingTests
    {
        [Fact]
        public void SelectorsStartEnabled()
        {
            Assert.True(new NestedItemsGridView().SelectorsEnabled);
        }

        // Bound to IsEnabled on a control inside a virtualizing DataGrid, so without the
        // notification a row scrolled off and back would show an enabled selector during a nest.
        [Fact]
        public void ChangingTheFlagRaisesPropertyChanged()
        {
            var row = new NestedItemsGridView();
            var changed = new List<string?>();
            row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            row.SelectorsEnabled = false;

            Assert.Contains(nameof(NestedItemsGridView.SelectorsEnabled), changed);
        }

        [Fact]
        public void SettingTheSameValueDoesNotNotify()
        {
            var row = new NestedItemsGridView { SelectorsEnabled = false };
            var changed = new List<string?>();
            row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            row.SelectorsEnabled = false;

            Assert.Empty(changed);
        }

        // Disabling is a UI affordance, not the guard itself - MainWindow also refuses the pick if
        // one arrives anyway. So the row must still route a pick when told to, or the belt-and-
        // braces check in the window would never be reached and a disabled-but-clicked control
        // would do nothing at all rather than explain itself.
        [Fact]
        public void APickStillRoutesWhileDisabled()
        {
            string? seen = null;
            var row = new NestedItemsGridView
            {
                StockOptions = new[] { "48 x 96", "60 x 120" },
                StockChanged = choice => seen = choice,
                SelectorsEnabled = false
            };

            row.ApplyUserSelection("60 x 120");

            Assert.Equal("60 x 120", seen);
        }
    }
}
