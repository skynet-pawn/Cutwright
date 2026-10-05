using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Cutwright
{
    // File > Export > SolidWorks Parts. Lists the SolidWorks part each unique BOM part becomes, lets
    // the estimator fix file names and leave parts out, then builds them.
    //
    // Opening it may start SolidWorks: the ANSI profile sizes can only be read through SolidWorks,
    // and the Source column cannot say which parts they cover until they have been. That happens
    // once per machine - see SolidWorksSession.ReadAnsiProfiles - and every window after reads them
    // from a cache.
    public partial class SolidWorksPartsWindow : Window
    {
        private readonly List<SwPartJob> jobs;
        private readonly List<SwPartRow> rows;
        private bool building;

        internal SolidWorksPartsWindow(IEnumerable<Part> parts, string bomPath)
        {
            InitializeComponent();

            jobs = SwBuildPlan.Create(parts);
            rows = jobs.Select(j => new SwPartRow(j)).ToList();
            PartsGrid.ItemsSource = rows;

            string? bomFolder = string.IsNullOrEmpty(bomPath) ? null : Path.GetDirectoryName(bomPath);
            FolderTextBox.Text = bomFolder is null ? string.Empty : Path.Combine(bomFolder, "SolidWorks Parts");

            Loaded += async (_, _) => await MatchProfilesAsync();
            Closing += RefuseToCloseWhileBuilding;
        }

        // Where each stick part's cross-section comes from depends on both profile libraries, and
        // reading either can take a moment - the shop one is on a network drive, the ANSI one may
        // need SolidWorks started - so the grid opens first and fills in its Source column after.
        private async Task MatchProfilesAsync()
        {
            SummaryText.Text = "Checking the profile libraries...";
            var warnings = new List<string>();

            string shopFolder = SolidWorksPaths.ShopProfileFolder;
            List<SwProfileEntry> shop = shopFolder.Length == 0
                ? new List<SwProfileEntry>()
                : await Task.Run(() => SwProfileLibrary.LoadShop(shopFolder));

            if (shopFolder.Length == 0)
                warnings.Add($"No shop profile library is set (ShopProfileFolder in {CutwrightSettings.DefaultFilePath}), so " +
                             "stick parts use the ANSI profiles or a sketched cross-section.");
            else if (shop.Count == 0)
                warnings.Add($"The shop profile library could not be read ({shopFolder}) - is the drive connected?");

            List<SwProfileEntry>? ansi = SolidWorksSession.AnyCachedAnsiProfiles();
            if (ansi is null)
            {
                SummaryText.Text = "Reading the ANSI profile sizes from SolidWorks. This happens once per computer and takes about a minute...";
                try
                {
                    await SolidWorksSession.Run(() =>
                    {
                        using var session = SolidWorksSession.Connect();
                        ansi = session.ReadAnsiProfiles();
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"SolidWorks parts: could not read the ANSI profiles: {ex.Message}");
                    warnings.Add($"The ANSI profile sizes could not be read from SolidWorks ({ex.Message}).");
                }
            }

            SwProfileMatcher.Resolve(jobs, shop, ansi ?? new List<SwProfileEntry>());

            foreach (SwPartRow row in rows)
                row.Refresh();

            BuildButton.IsEnabled = true;
            SummaryText.Text = string.Join("  ", warnings.Prepend(Summary()));
        }

        private string Summary()
        {
            int included = jobs.Count(j => j.Include && j.CanBuild);
            int blocked = jobs.Count(j => !j.CanBuild);
            return $"{included} of {jobs.Count} parts will be built." +
                   (blocked > 0 ? $" {blocked} cannot be built - see Status." : string.Empty);
        }

        private void BrowseForFolder(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Choose where to save the SolidWorks parts" };
            if (Directory.Exists(FolderTextBox.Text))
                dialog.InitialDirectory = FolderTextBox.Text;

            if (dialog.ShowDialog(this) == true)
                FolderTextBox.Text = dialog.FolderName;
        }

        private void OpenFolder(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(FolderTextBox.Text))
                Process.Start(new ProcessStartInfo(FolderTextBox.Text) { UseShellExecute = true });
        }

        // A renamed part can clear a collision or cause one, so every name is rechecked - once the
        // edit has actually committed, which is after this event.
        private void PartsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var before = jobs.ToDictionary(j => j, j => j.CanBuild);
                SwBuildPlan.CheckFileNames(jobs);

                foreach (SwPartJob job in jobs)
                {
                    if (before[job] != job.CanBuild)
                        job.Include = job.CanBuild;
                }

                foreach (SwPartRow row in rows)
                    row.Refresh();

                SummaryText.Text = Summary();
            });
        }

        private async void BuildParts(object sender, RoutedEventArgs e)
        {
            PartsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            SwBuildPlan.CheckFileNames(jobs);

            string folder = FolderTextBox.Text.Trim();
            if (folder.Length == 0)
            {
                MessageBox.Show(this, "Choose a folder to save the parts in.", "No folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, $"The folder could not be created:\n\n{ex.Message}", "No folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var toBuild = rows.Where(r => r.Job.Include && r.Job.CanBuild).ToList();
            if (toBuild.Count == 0)
            {
                MessageBox.Show(this, "No parts are ticked to build.", "Nothing to build", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetBuilding(true);
            int built = 0, failed = 0, done = 0;
            Log.Info($"SolidWorks parts: building {toBuild.Count} part(s) into {folder}");

            try
            {
                await SolidWorksSession.Run(() =>
                {
                    using var session = SolidWorksSession.Connect();
                    var builder = new SwPartBuilder(session.App);

                    foreach (SwPartRow row in toBuild)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            row.ShowBuilding();
                            SummaryText.Text = $"Building {done + 1} of {toBuild.Count}: {row.FileName}...";
                        });

                        SwBuildResult result = builder.Build(row.Job, folder);

                        if (result.Built) built++;
                        else failed++;
                        done++;

                        Log.Info($"SolidWorks parts: {row.FileName} - {(result.Built ? "built" : "failed")}: {result.Message}" +
                                 (result.Notes.Count > 0 ? " | " + string.Join("; ", result.Notes) : string.Empty));
                        Dispatcher.Invoke(() => row.ShowResult(result));
                    }
                });

                SummaryText.Text = $"Done. {built} built" + (failed > 0 ? $", {failed} failed - see Status." : ".") +
                                   $" Saved in {folder}";
            }
            catch (Exception ex)
            {
                Log.Warn($"SolidWorks parts: the build stopped: {ex}");
                SummaryText.Text = $"Stopped after {built} built, {failed} failed.";
                MessageBox.Show(this, $"The build stopped:\n\n{ex.Message}", "SolidWorks", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBuilding(false);
                OpenFolderButton.Visibility = Visibility.Visible;
            }
        }

        private void SetBuilding(bool busy)
        {
            building = busy;
            BuildButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            FolderTextBox.IsEnabled = !busy;
            PartsGrid.IsReadOnly = busy;

            foreach (SwPartRow row in rows)
                row.Building = busy;
        }

        private void RefuseToCloseWhileBuilding(object? sender, CancelEventArgs e)
        {
            if (!building)
                return;

            e.Cancel = true;
            MessageBox.Show(this, "Parts are still being built. Wait for the build to finish, then close.",
                "Build in progress", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // One row of the grid: an SwPartJob, formatted, with a live Status.
    internal sealed class SwPartRow : INotifyPropertyChanged
    {
        private string? result;
        private bool resultIsError;
        private bool building;

        public SwPartRow(SwPartJob job) => Job = job;

        public SwPartJob Job { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Include
        {
            get => Job.Include && Job.CanBuild;
            set
            {
                if (!CanInclude)
                    return;
                Job.Include = value;
                Raise(nameof(Include));
            }
        }

        public bool CanInclude => Job.CanBuild && !building;

        public bool Building
        {
            set
            {
                building = value;
                Raise(nameof(CanInclude));
            }
        }

        public string FileName
        {
            get => Job.FileName;
            set
            {
                Job.FileName = SwBuildPlan.Sanitize(value);
                result = null;
                Raise(nameof(FileName));
            }
        }

        public string Det => Job.Det > 0 ? Job.Det.ToString(CultureInfo.InvariantCulture) : string.Empty;
        public string Description => Job.Description;
        public string Kind => Job.Kind.ToString();
        public string Length => Inches(Job.Length);
        public string Width => Job.Kind == SwPartKind.Sheet ? Inches(Job.Width) : string.Empty;
        public string Thickness => Job.Thickness is double t ? Inches(t) : string.Empty;
        public string Material => Job.Material?.ToString() ?? string.Empty;

        public string Source => Job.Kind == SwPartKind.Sheet
            ? (Job.CanBuild ? "Sheet metal" : string.Empty)
            : Job.Profile?.Describe() ?? (Job.CanBuild ? "..." : string.Empty);

        public string Status => result
            ?? Job.Problem
            ?? (Job.Notes.Count > 0 ? string.Join("; ", Job.Notes) : "Ready");

        public bool IsError => result is not null ? resultIsError : !Job.CanBuild;

        public void ShowBuilding()
        {
            result = "Building...";
            resultIsError = false;
            Refresh();
        }

        public void ShowResult(SwBuildResult build)
        {
            var parts = new List<string> { build.Built ? $"Saved {Path.GetFileName(build.Message)}" : build.Message };
            parts.AddRange(build.Notes);
            result = string.Join("; ", parts);
            resultIsError = !build.Built;
            Refresh();
        }

        public void Refresh()
        {
            foreach (string name in new[] { nameof(Include), nameof(CanInclude), nameof(FileName), nameof(Thickness),
                         nameof(Source), nameof(Status), nameof(IsError) })
                Raise(name);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string Inches(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
