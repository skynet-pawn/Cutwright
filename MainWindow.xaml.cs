using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;




namespace Cutwright
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            prs = new FileParser();
            wrt = new FileWriter();

            this.newBOM = new BillOfMaterials();


            //Create Nested Items Grid View

            NestData = new ObservableCollection<NestedItemsGridView>();

            dxfFileWriter = new DXFWriter();




            // Captures the UI SynchronizationContext here, on the UI thread, so a later
            // .Report() call from the background nest task marshals back automatically instead of
            // needing its own Dispatcher.Invoke.
            _nestProgress = new Progress<int>(p => MenuProgressBar.Value = p);

            InitializeNest2DNavigation();
            InitializeNest1DNavigation();
            InitializeEndFeatures();

#if !SOLIDWORKS
            // No SolidWorks export in this build, so there is nothing for the button to do.
            ExportSolidWorksButton.Visibility = Visibility.Collapsed;
#endif

            NotificationsButton.DataContext = _notifications;
            NotificationsPopup.DataContext = _notifications;

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                Title = $"Cutwright {version.Major}.{version.Minor}.{version.Build}";

            _baseTitle = Title;
        }

        //Mouse-wheel zoom (anchored on the cursor) and middle-mouse-drag pan for the Nest 2D
        //Layouts view. Set up once, rather than in DrawSheetNests(), so redrawing a nest doesn't
        //stack duplicate handlers.
        //
        //Handlers live on the viewport Border rather than the canvas: the canvas is unsized, so
        //it is only hit-testable where something has actually been drawn, and panning needs to
        //work over empty space too.
        private readonly ScaleTransform _nest2DScale = new ScaleTransform();
        private readonly TranslateTransform _nest2DPan = new TranslateTransform();
        private bool _isPanningNest2D;
        private System.Windows.Point _panStartMouse;
        private double _panStartX;
        private double _panStartY;

        //Without a ScrollViewer there's nothing bounding the view, so zoom is clamped to keep the
        //drawing recoverable.
        private const double Nest2DMinScale = 0.02;
        private const double Nest2DMaxScale = 50.0;

        private void InitializeNest2DNavigation()
        {
            var transform = new TransformGroup();
            transform.Children.Add(_nest2DScale);
            transform.Children.Add(_nest2DPan);
            Nest2DLayoutCanvas.RenderTransform = transform;

            Nest2DViewport.MouseWheel += (sender, e) =>
            {
                //Anchor the zoom on the cursor by working out which content point is under it
                //*before* the scale changes, then translating so that same point lands back under
                //the cursor afterwards. Nudging ScaleTransform.CenterX/Y instead would drift,
                //because the centre moves while the accumulated scale doesn't account for it.
                System.Windows.Point cursor = e.GetPosition(Nest2DViewport);

                double oldScale = _nest2DScale.ScaleX;
                double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
                double newScale = Math.Clamp(oldScale * factor, Nest2DMinScale, Nest2DMaxScale);

                if (newScale == oldScale)
                {
                    e.Handled = true;
                    return;
                }

                double contentX = (cursor.X - _nest2DPan.X) / oldScale;
                double contentY = (cursor.Y - _nest2DPan.Y) / oldScale;

                _nest2DScale.ScaleX = newScale;
                _nest2DScale.ScaleY = newScale;
                _nest2DPan.X = cursor.X - contentX * newScale;
                _nest2DPan.Y = cursor.Y - contentY * newScale;

                e.Handled = true;
            };

            Nest2DViewport.MouseDown += (sender, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle)
                    return;

                _isPanningNest2D = true;
                _panStartMouse = e.GetPosition(this);
                _panStartX = _nest2DPan.X;
                _panStartY = _nest2DPan.Y;

                Nest2DViewport.CaptureMouse();
                Nest2DViewport.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            };

            Nest2DViewport.MouseMove += (sender, e) =>
            {
                if (!_isPanningNest2D)
                    return;

                //Measured against the window, not the canvas - the canvas moves as we pan, so
                //using it as the reference would feed back on itself.
                System.Windows.Point current = e.GetPosition(this);
                _nest2DPan.X = _panStartX + (current.X - _panStartMouse.X);
                _nest2DPan.Y = _panStartY + (current.Y - _panStartMouse.Y);
            };

            Nest2DViewport.MouseUp += (sender, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle)
                    return;

                _isPanningNest2D = false;
                Nest2DViewport.ReleaseMouseCapture();
                Nest2DViewport.Cursor = Cursors.Arrow;
            };

            //Double-click puts the whole drawing back in view - the way home after zooming into
            //one corner of a big nest.
            Nest2DViewport.MouseLeftButtonDown += (sender, e) =>
            {
                if (e.ClickCount == 2)
                {
                    FitNest2DToView(_nest2DContentBounds);
                    e.Handled = true;
                }
            };

            Nest2DViewport.SizeChanged += Nest2DViewport_SizeChanged;
        }

        //Bounds of the last drawing, kept so the fit can be retried once the viewport has a size.
        private Rect _nest2DContentBounds = Rect.Empty;
        private bool _nest2DFitPending;

        //Scales and centres the view so the whole drawing is visible. Called after a redraw, which
        //only happens when the content itself changed (nest finished, sheet size changed), so it
        //doesn't fight manual navigation in between.
        private void FitNest2DToView(Rect contentBounds)
        {
            _nest2DContentBounds = contentBounds;

            if (contentBounds.IsEmpty || contentBounds.Width <= 0 || contentBounds.Height <= 0)
                return;

            double viewportWidth = Nest2DViewport.ActualWidth;
            double viewportHeight = Nest2DViewport.ActualHeight;

            //A TabControl only lays out the selected tab's content, so if the nest finished while
            //another tab was showing there's no viewport to fit to yet. Defer rather than guess -
            //Nest2DViewport_SizeChanged picks it up the moment the tab is first shown.
            if (viewportWidth <= 0 || viewportHeight <= 0)
            {
                _nest2DFitPending = true;
                return;
            }

            _nest2DFitPending = false;

            const double margin = 20.0;
            double scale = Math.Min((viewportWidth - margin) / contentBounds.Width,
                                    (viewportHeight - margin) / contentBounds.Height);
            scale = Math.Clamp(scale, Nest2DMinScale, Nest2DMaxScale);

            _nest2DScale.ScaleX = scale;
            _nest2DScale.ScaleY = scale;

            //Centre the content's midpoint in the viewport.
            _nest2DPan.X = viewportWidth / 2.0 - (contentBounds.X + contentBounds.Width / 2.0) * scale;
            _nest2DPan.Y = viewportHeight / 2.0 - (contentBounds.Y + contentBounds.Height / 2.0) * scale;
        }

        private void Nest2DViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_nest2DFitPending)
                FitNest2DToView(_nest2DContentBounds);
        }

        //Shared palette for both nest views. The 1D and 2D layouts are the same drawing of the
        //same idea - parts laid onto stock - so they read as one tool only if stock, parts, and
        //outlines are the same colour in both. Kept here rather than at each call site so they
        //cannot drift apart again.
        private static readonly Brush NestStockFillBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x72, 0x8A, 0x9F)));
        private static readonly Brush NestPartFillBrush = Brushes.Coral;
        private static readonly Brush NestOutlineBrush = Brushes.Black;
        private static readonly Color NestLabelColor = Colors.White;
        private const double NestOutlineThickness = 0.1;

        //Frozen so one brush can back every shape on the canvas without WPF tracking each use.
        private static Brush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        //Sheets are drawn turned 90 degrees, so their long axis runs left-to-right, and laid out
        //in a grid that fills rightward then wraps. Every sheet within a material is the same
        //size, so a fixed count per row keeps the columns aligned.
        private const int Nest2DSheetsPerRow = 6;
        private const double Nest2DSheetGap = 6.0;
        private const double Nest2DGroupGap = 24.0;
        private const double Nest2DLabelHeight = 15.0;
        private const double Nest2DMargin = 5.0;

        //Room above each sheet for its multiplier.
        private const double Nest2DSheetLabelHeight = 11.0;

        //Shown in the selector when every group is wanted at once.
        private const string Nest2DAllGroups = "All materials";

        //Set while the selector is being refilled, so rebuilding the list does not read as the
        //estimator picking something and trigger a redraw per item added.
        private bool _fillingNest2DGroups;

        //Rebuilds the group list, keeping the current pick if that group is still listed, so a
        //re-nest does not quietly move the view to a different material.
        private void PopulateNest2DGroupSelector()
        {
            int previous = Nest2DGroupSelector.SelectedIndex;

            _fillingNest2DGroups = true;

            try
            {
                Nest2DGroupSelector.Items.Clear();

                foreach (var list in this.prs.PNestList)
                {
                    int sheets = list.Sheets?.Count ?? 0;
                    int layouts = SheetLayouts.Group(list.Sheets).Count;

                    //Both numbers, because they are different things: sheets are what gets bought,
                    //layouts are what gets drawn and programmed.
                    Nest2DGroupSelector.Items.Add(sheets == 0
                        ? list.Description + "  -  not nested"
                        : list.Description + "  -  " + sheets + " sheet(s), " + layouts + " layout(s)");
                }

                if (Nest2DGroupSelector.Items.Count > 1)
                    Nest2DGroupSelector.Items.Add(Nest2DAllGroups);

                if (previous >= 0 && previous < Nest2DGroupSelector.Items.Count)
                    Nest2DGroupSelector.SelectedIndex = previous;
                else if (Nest2DGroupSelector.Items.Count > 0)
                    Nest2DGroupSelector.SelectedIndex = 0;
            }
            finally
            {
                _fillingNest2DGroups = false;
            }
        }

        private void Nest2DGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingNest2DGroups)
                return;

            DrawSheetNests();
            SyncSpacingBoxes();
        }

        //Whenever a nest changed. The selector labels carry each group's sheet and layout counts,
        //so refilling and redrawing belong together - updating one without the other leaves the
        //list describing a nest that has been replaced.
        private void RefreshNest2DView()
        {
            PopulateNest2DGroupSelector();
            DrawSheetNests();
            SyncSpacingBoxes();
        }

        //The groups the drawing should cover: the one selected, or all of them.
        //
        //Drawing one material at a time is the difference between a view that opens and one that
        //does not. A nest is read a material at a time anyway - the sheets of 11GA tell you nothing
        //about the plate - so the rest is work spent on pictures nobody is looking at.
        private List<PNest> SelectedNest2DGroups()
        {
            int index = Nest2DGroupSelector.SelectedIndex;

            //Below zero is nothing chosen yet; at or past the group count is the "All materials"
            //entry, which sits after them.
            if (index < 0 || index >= this.prs.PNestList.Count)
                return this.prs.PNestList;

            return new List<PNest> { this.prs.PNestList[index] };
        }

        // Renders each PNest group's nested sheets directly from the placement results the
        // engine produced (true part outlines + holes, already positioned/rotated) - no DXF
        // round-trip needed since we hold the exact geometry in memory.
        private void DrawSheetNests()
        {
            Nest2DLayoutCanvas.Children.Clear();

            double groupTop = Nest2DMargin;
            double contentRight = 0.0;

            foreach (var list in SelectedNest2DGroups())
            {
                if (list.Sheets == null || list.Sheets.Count == 0)
                    continue;

                //Sheets cut the same way are drawn once, with how many were nested that way. On a
                //repeat-quantity job that is nearly all of them - a measured 52,000 part job came
                //to 1,198 sheets in 24 distinct layouts - and drawing all 1,198 meant building
                //3.18 million points of geometry for 24 pictures. It is also unreadable: a hundred
                //identical sheets say far less than one and a count.
                var layouts = SheetLayouts.Group(list.Sheets);

                DrawText(Nest2DMargin, groupTop, $"{list.Description}  -  {list.Sheets.Count} sheet(s)",
                    NestLabelColor, Nest2DLayoutCanvas);
                groupTop += Nest2DLabelHeight;

                for (int i = 0; i < layouts.Count; i++)
                {
                    var sheet = layouts[i].Representative;

                    //Turned 90 degrees, a sheet spans its Length horizontally and its Width
                    //vertically.
                    int column = i % Nest2DSheetsPerRow;
                    int row = i / Nest2DSheetsPerRow;

                    double originX = Nest2DMargin + column * (sheet.SheetLength + Nest2DSheetGap);
                    double originY = groupTop + row * (sheet.SheetWidth + Nest2DSheetGap + Nest2DSheetLabelHeight)
                                     + Nest2DSheetLabelHeight;

                    DrawText(originX, originY - Nest2DSheetLabelHeight, $"x{layouts[i].Count}",
                        NestLabelColor, Nest2DLayoutCanvas);

                    //Hit-testable, unlike the parts drawn over it, so hovering a sheet can say what
                    //it is. It is one rectangle per layout, so this costs nothing.
                    var sheetPolygon = new Polygon
                    {
                        Fill = NestStockFillBrush,
                        Stroke = NestOutlineBrush,
                        StrokeThickness = NestOutlineThickness,
                        ToolTip = $"{list.Description}{Environment.NewLine}" +
                                  $"{sheet.SheetWidth:0.##} x {sheet.SheetLength:0.##} sheet, " +
                                  $"{layouts[i].Count} cut this way{Environment.NewLine}" +
                                  $"{sheet.NestedParts.Count} part(s) on each"
                    };

                    sheetPolygon.Points.Add(ToCanvasPoint(0, 0, originX, originY));
                    sheetPolygon.Points.Add(ToCanvasPoint(sheet.SheetWidth, 0, originX, originY));
                    sheetPolygon.Points.Add(ToCanvasPoint(sheet.SheetWidth, sheet.SheetLength, originX, originY));
                    sheetPolygon.Points.Add(ToCanvasPoint(0, sheet.SheetLength, originX, originY));
                    Nest2DLayoutCanvas.Children.Add(sheetPolygon);

                    //All part outers + hole contours go into one even-odd filled geometry, so
                    //holes show through to the sheet background.
                    //
                    //A frozen StreamGeometry rather than a PathGeometry built out of PathFigure and
                    //PolyLineSegment objects. Those are Freezables in their own right, one per
                    //contour, each carrying change notification and a parent chain, where a
                    //StreamGeometry holds the same outlines as a flat figure stream with none of
                    //that. Measured on a 24 layout drawing of 407,000 points: 147ms against 351ms
                    //to build, before the render, which no longer has per-figure objects to walk.
                    var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                    bool anyFigure = false;

                    using (var context = geometry.Open())
                    {
                        foreach (var placedPart in sheet.NestedParts)
                        {
                            var (outer, holes) = placedPart.ToWorld();
                            anyFigure |= AddLoopFigure(outer, originX, originY, context);

                            foreach (var hole in holes)
                                anyFigure |= AddLoopFigure(hole, originX, originY, context);
                        }
                    }

                    if (anyFigure)
                    {
                        geometry.Freeze();

                        var partsPath = new System.Windows.Shapes.Path
                        {
                            Data = geometry,
                            Fill = NestPartFillBrush,
                            Stroke = NestOutlineBrush,
                            StrokeThickness = NestOutlineThickness,

                            //Nothing drawn here is clickable - the viewport Border is what the zoom
                            //and pan handlers attach to - so hit testing several hundred thousand
                            //points is pure cost.
                            IsHitTestVisible = false
                        };

                        Nest2DLayoutCanvas.Children.Add(partsPath);
                    }

                    contentRight = Math.Max(contentRight, originX + sheet.SheetLength);
                }

                int rows = (layouts.Count + Nest2DSheetsPerRow - 1) / Nest2DSheetsPerRow;
                groupTop += rows * (layouts[0].Representative.SheetWidth + Nest2DSheetGap + Nest2DSheetLabelHeight)
                            + Nest2DGroupGap;
            }

            FitNest2DToView(new Rect(0, 0, contentRight + Nest2DMargin, groupTop));
        }

        //Returns whether anything was written, so the caller knows not to add an empty path.
        private static bool AddLoopFigure(List<(double X, double Y)> loop, double originX, double originY,
            StreamGeometryContext context)
        {
            if (loop.Count < 3)
                return false;

            var rest = new System.Windows.Point[loop.Count - 1];
            for (int i = 1; i < loop.Count; i++)
                rest[i - 1] = ToCanvasPoint(loop[i].X, loop[i].Y, originX, originY);

            context.BeginFigure(ToCanvasPoint(loop[0].X, loop[0].Y, originX, originY), true, true);
            context.PolyLineTo(rest, true, false);
            return true;
        }

        //Maps sheet space to canvas space, turning the sheet 90 degrees clockwise so its Length
        //axis runs left-to-right across the screen.
        //
        //Swapping the axes looks like a mirror on its own, but it isn't: sheet space is Y-up and
        //the canvas is Y-down, and composing that existing flip with the rotation cancels out to
        //a plain transpose. Verified corner by corner - a part at the sheet's bottom-left lands
        //top-left on screen with its dimensions swapped, exactly as rotating the old portrait
        //view clockwise would put it. This matters: a mirrored nest wouldn't match the DXF export.
        private static System.Windows.Point ToCanvasPoint(double sheetX, double sheetY, double originX, double originY)
        {
            return new System.Windows.Point(sheetY + originX, sheetX + originY);
        }

        //Sticks are drawn one per row, longest axis left to right, grouped by material with the
        //group's description above it. Laid out in the same terms as the 2D view - label, then
        //rows, then a gap before the next group - so the two read the same way.
        private const double Nest1DMargin = 5.0;
        private const double Nest1DStickHeight = 7.5;
        private const double Nest1DPartHeight = 5.0;
        private const double Nest1DStickGap = 2.5;
        private const double Nest1DLabelHeight = 11.0;
        private const double Nest1DGroupGap = 8.0;
        private const double Nest1DCountGap = 6.0;

        //The 1D drawing is in inches, which would be a few hundred pixels across for a whole
        //stick, so it is scaled up to fill the tab. A LayoutTransform rather than a
        //RenderTransform: a render transform is applied after layout, so the ScrollViewer would
        //size itself to the unscaled canvas and the bottom of a tall drawing could not be
        //scrolled to.
        //A field rather than a constant: Ctrl + mouse wheel changes it.
        private double _nest1DScale = 4.0;
        private const double Nest1DMinScale = 0.5;
        private const double Nest1DMaxScale = 24.0;

        //Ctrl + wheel zooms the 1D drawing. Plain wheel is left alone so it still scrolls the tall
        //drawing. The scroll offsets are kept proportional so the view stays roughly where it was
        //rather than jumping back to the top left.
        private void InitializeNest1DNavigation()
        {
            Nest1DScrollViewer.PreviewMouseWheel += (sender, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    return;

                double oldScale = _nest1DScale;
                double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
                double newScale = Math.Clamp(oldScale * factor, Nest1DMinScale, Nest1DMaxScale);

                e.Handled = true;

                if (newScale == oldScale)
                    return;

                double ratio = newScale / oldScale;
                System.Windows.Point cursor = e.GetPosition(Nest1DScrollViewer);
                double contentX = Nest1DScrollViewer.HorizontalOffset + cursor.X;
                double contentY = Nest1DScrollViewer.VerticalOffset + cursor.Y;

                _nest1DScale = newScale;
                Nest1DLayoutCanvas.LayoutTransform = new ScaleTransform(newScale, newScale);
                Nest1DScrollViewer.UpdateLayout();

                Nest1DScrollViewer.ScrollToHorizontalOffset(contentX * ratio - cursor.X);
                Nest1DScrollViewer.ScrollToVerticalOffset(contentY * ratio - cursor.Y);
            };
        }

        //Shown in the selector when every group is wanted at once.
        private const string Nest1DAllGroups = "All materials";

        //Set while the selector is being refilled, so rebuilding the list does not read as the
        //estimator picking something and trigger a redraw per item added.
        private bool _fillingNest1DGroups;

        //Rebuilds the group list, keeping the current pick if that group is still listed, so a
        //re-nest does not quietly move the view to a different material.
        private void PopulateNest1DGroupSelector()
        {
            int previous = Nest1DGroupSelector.SelectedIndex;

            _fillingNest1DGroups = true;

            try
            {
                Nest1DGroupSelector.Items.Clear();

                foreach (var list in this.prs.TNestList)
                {
                    //Bought by the piece is not the same as not nested yet, and the label should
                    //not send anyone looking for sticks that were never cut.
                    string detail = list.Purchase == StickPurchase.Pieces ? "bought by the piece"
                        : list.Sticks.Count == 0 ? "not nested"
                        : $"{list.StickCount} stick(s)";

                    Nest1DGroupSelector.Items.Add($"{list.Description}  -  {detail}");
                }

                if (Nest1DGroupSelector.Items.Count > 1)
                    Nest1DGroupSelector.Items.Add(Nest1DAllGroups);

                if (previous >= 0 && previous < Nest1DGroupSelector.Items.Count)
                    Nest1DGroupSelector.SelectedIndex = previous;
                else if (Nest1DGroupSelector.Items.Count > 0)
                    Nest1DGroupSelector.SelectedIndex = 0;
            }
            finally
            {
                _fillingNest1DGroups = false;
            }
        }

        private void Nest1DGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingNest1DGroups)
                return;

            DrawTubeNests();
            SyncStickSettingBoxes();
        }

        // ---- Tube End Features tab

        private const string EndFeaturesAllGroups = "All materials";
        private const string BulkNoChange = "(no change)";
        private bool _fillingEndFeatureGroups;
        private readonly ObservableCollection<EndFeatureRow> _endFeatureRows = new();

        // Filled once: the two bulk dropdowns offer the ends plus a "leave alone" entry.
        private void InitializeEndFeatures()
        {
            foreach (var box in new[] { BulkEndAComboBox, BulkEndBComboBox })
            {
                box.Items.Add(BulkNoChange);
                foreach (string option in TubeEndFeatureText.Options)
                    box.Items.Add(option);

                box.SelectedIndex = 0;
            }

            EndFeaturesDataGrid.ItemsSource = _endFeatureRows;
        }

        // Rebuilds the group list, keeping the current pick if that group is still listed.
        private void PopulateEndFeaturesGroupSelector()
        {
            int previous = EndFeaturesGroupSelector.SelectedIndex;

            _fillingEndFeatureGroups = true;

            try
            {
                EndFeaturesGroupSelector.Items.Clear();

                foreach (var group in this.prs.TNestList)
                    EndFeaturesGroupSelector.Items.Add($"{group.Description}  -  {group.Parts.Count} part line(s)");

                if (EndFeaturesGroupSelector.Items.Count > 1)
                    EndFeaturesGroupSelector.Items.Add(EndFeaturesAllGroups);

                if (previous >= 0 && previous < EndFeaturesGroupSelector.Items.Count)
                    EndFeaturesGroupSelector.SelectedIndex = previous;
                else if (EndFeaturesGroupSelector.Items.Count > 0)
                    EndFeaturesGroupSelector.SelectedIndex = 0;
            }
            finally
            {
                _fillingEndFeatureGroups = false;
            }
        }

        private List<TNest> SelectedEndFeatureGroups()
        {
            int index = EndFeaturesGroupSelector.SelectedIndex;

            if (index < 0 || index >= this.prs.TNestList.Count)
                return this.prs.TNestList;

            return new List<TNest> { this.prs.TNestList[index] };
        }

        private void EndFeaturesGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingEndFeatureGroups)
                return;

            RebuildEndFeatureRows();
        }

        // One row per stick part line in the group(s) shown, in BOM order. Rebuilt when the group
        // shown changes or a nest finishes; a single edit updates its own row in place instead, so
        // the grid does not jump under someone working down it.
        private void RebuildEndFeatureRows()
        {
            _endFeatureRows.Clear();

            foreach (var group in SelectedEndFeatureGroups())
            {
                foreach (var part in group.Parts)
                    _endFeatureRows.Add(new EndFeatureRow(part, group, OnEndFeatureRowChanged));
            }

            UpdateEndFeaturesSummary();
        }

        private void RefreshEndFeaturesView()
        {
            PopulateEndFeaturesGroupSelector();
            RebuildEndFeatureRows();

            bool any = this.prs.TNestList.Count > 0;
            EndFeaturesDataGrid.IsEnabled = any && !_nestRunning;
            ApplyBulkEndsButton.IsEnabled = any && !_nestRunning;
        }

        private void UpdateEndFeaturesSummary()
        {
            int total = _endFeatureRows.Count;

            if (total == 0)
            {
                EndFeaturesSummaryText.Text = this.prs.TNestList.Count == 0
                    ? "No stick parts in this BOM."
                    : "Open a BOM to review its stick parts.";
                return;
            }

            int unreviewed = _endFeatureRows.Count(r => r.IsUnreviewed);
            EndFeaturesSummaryText.Text = unreviewed == 0
                ? $"All {total} part line(s) shown have been reviewed."
                : $"{unreviewed} of {total} part line(s) shown have both ends Unreviewed - they are kept out of " +
                  "the tube laser's clamp zone. Saving writes these choices to the BOM's End Features sheet.";
        }

        private void EndASelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is EndFeatureRow row)
                row.ApplyEndA(combo.SelectedItem as string);
        }

        private void EndBSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is EndFeatureRow row)
                row.ApplyEndB(combo.SelectedItem as string);
        }

        // One part's ends changed: renest its group, since a miter or a clear end changes where the
        // part can go, then bring the other views in line.
        private void OnEndFeatureRowChanged(EndFeatureRow row)
        {
            if (NestInProgress("An end feature change"))
                return;

            FinishEndFeatureChange(new[] { row.Group }, 1);
        }

        private void FinishEndFeatureChange(IEnumerable<TNest> groups, int partsChanged)
        {
            bool renested = false;

            foreach (var group in groups.Distinct())
                renested |= NestGroupCoordinator.ApplyEndFeaturesChange(group) == NestChangeOutcome.Renested;

            if (renested)
                RefreshNest1DView();

            RefreshNestDataGrid();
            UpdateEndFeaturesSummary();
            SetStatus("End features changed: " + NestSummary());

            Log.Info($"End features changed on {partsChanged} part line(s).");
        }

        // Sets the chosen ends on every part shown, then renests each affected group once.
        private void ApplyBulkEnds_Click(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("An end feature change"))
                return;

            TubeEndFeature? Chosen(ComboBox box) =>
                box.SelectedItem is string text && text != BulkNoChange
                    ? TubeEndFeatureText.Parse(text)
                    : null;

            var a = Chosen(BulkEndAComboBox);
            var b = Chosen(BulkEndBComboBox);

            if (a is null && b is null)
            {
                Notify(NotificationLevel.Info, "Nothing to apply",
                    "Choose an End A and/or End B first - both are on (no change).");
                return;
            }

            var changedGroups = new List<TNest>();
            int changed = 0;

            foreach (var row in _endFeatureRows)
            {
                if (row.SetEnds(a, b))
                {
                    changed++;
                    changedGroups.Add(row.Group);
                }
            }

            if (changed == 0)
            {
                SetStatus("Every part shown already has those ends.");
                return;
            }

            FinishEndFeatureChange(changedGroups, changed);
        }

        // Shows the kerf and minimum cut length of whatever the 1D tab is showing: that group's own
        // values, or - for "All materials" - the shared value when every group agrees and blank
        // when they differ, so the box never claims a figure some group is not nested with.
        private void SyncStickSettingBoxes()
        {
            var groups = SelectedNest1DGroups();

            SetSpacingBox(KerfTextBox, groups.Select(g => g.Kerf).Distinct().ToList());
            SetSpacingBox(MinCutLengthTextBox, groups.Select(g => g.MinClampLength).Distinct().ToList());

            bool editable = groups.Count > 0 && !_nestRunning;
            KerfTextBox.IsEnabled = editable;
            MinCutLengthTextBox.IsEnabled = editable;
        }

        // Enter commits immediately, the same as tabbing away - see UnitsTextBox_KeyDown.
        private void StickSettingTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            CommitStickSettingsChange();
            e.Handled = true;
        }

        private void StickSettingTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitStickSettingsChange();

        // The 1D counterpart of CommitSpacingChange: applies the kerf and minimum cut length boxes
        // to the group(s) the 1D tab is showing and renests them. Read as a pair for the same
        // reason - tabbing through both fields on one edit would otherwise renest twice.
        private void CommitStickSettingsChange()
        {
            var groups = SelectedNest1DGroups();
            if (groups.Count == 0)
                return;

            var notes = new List<string>();
            bool blankMeansKeep = groups.Count > 1;

            float? kerf = ReadSpacingBox(KerfTextBox, "Kerf", notes, blankMeansKeep, TNest.DefaultKerf);

            // Zero is a real minimum cut length - no clamp allowance - so it is accepted here.
            float? minCut = ReadSpacingBox(MinCutLengthTextBox, "Min cut length", notes, blankMeansKeep,
                TNest.DefaultMinClampLength, allowZero: true);

            if (notes.Count > 0)
            {
                foreach (string note in notes)
                    Log.Warn(note);

                Notify(NotificationLevel.Warning, "Stick settings not applied",
                    string.Join(Environment.NewLine + Environment.NewLine, notes));
            }

            var affected = groups.Where(g => (kerf ?? g.Kerf) != g.Kerf
                                             || (minCut ?? g.MinClampLength) != g.MinClampLength).ToList();

            if (affected.Count == 0)
            {
                // Shown back so the box always reads as what will actually be nested with.
                SyncStickSettingBoxes();
                return;
            }

            if (NestInProgress("A kerf or minimum cut length change"))
            {
                SyncStickSettingBoxes();
                return;
            }

            bool renested = false;
            foreach (var group in affected)
            {
                var outcome = NestGroupCoordinator.ApplyGroupStickSettingsChange(group,
                    kerf ?? group.Kerf, minCut ?? group.MinClampLength);
                renested |= outcome == NestChangeOutcome.Renested;
            }

            if (renested)
                RefreshNest1DView();
            else
                SyncStickSettingBoxes();

            RefreshNestDataGrid();
            SetStatus("Stick settings changed: " + NestSummary());

            Log.Info($"Kerf / min cut length changed for {affected.Count} stick group(s): " +
                     $"{kerf:0.###}\" / {minCut:0.###}\".");
        }

        //Whenever a nest changed. The selector labels carry each group's stick counts, so
        //refilling and redrawing belong together - updating one without the other leaves the list
        //describing a nest that has been replaced.
        private void RefreshNest1DView()
        {
            PopulateNest1DGroupSelector();
            DrawTubeNests();
            SyncStickSettingBoxes();
        }

        //The groups the drawing should cover: the one selected, or all of them.
        private List<TNest> SelectedNest1DGroups()
        {
            int index = Nest1DGroupSelector.SelectedIndex;

            //Below zero is nothing chosen yet; at or past the group count is the "All materials"
            //entry, which sits after them.
            if (index < 0 || index >= this.prs.TNestList.Count)
                return this.prs.TNestList;

            return new List<TNest> { this.prs.TNestList[index] };
        }

        private void DrawTubeNests()
        {
            Nest1DLayoutCanvas.Children.Clear();
            Nest1DLayoutCanvas.LayoutTransform = new ScaleTransform(_nest1DScale, _nest1DScale);

            double top = Nest1DMargin;
            double contentRight = 0.0;

            foreach (var list in SelectedNest1DGroups())
            {
                if (list.Sticks == null || list.Sticks.Count == 0)
                    continue;

                string label = $"{list.Description}  -  {list.StickCount} stick(s)";
                DrawText(Nest1DMargin, top, label, NestLabelColor, Nest1DLayoutCanvas);
                contentRight = Math.Max(contentRight, Nest1DMargin + EstimateTextWidth(label));
                top += Nest1DLabelHeight;

                foreach (var (representative, count) in StickPatterns.GroupIdenticalSticks(list.Sticks))
                {
                    DrawStick(representative, list.Kerf, top);

                    //Count sits past the end of the stock bar, so the bars stay flush left and can
                    //be compared down the column.
                    string countText = $"x{count}";
                    double countX = Nest1DMargin + representative.StickLength + Nest1DCountGap;
                    DrawText(countX, top, countText, NestLabelColor, Nest1DLayoutCanvas);

                    contentRight = Math.Max(contentRight, countX + EstimateTextWidth(countText));
                    top += Nest1DStickHeight + Nest1DStickGap;
                }

                top += Nest1DGroupGap;
            }

            //Sized to what was actually drawn, so the ScrollViewer's extent matches the content
            //rather than a fixed guess.
            Nest1DLayoutCanvas.Width = contentRight + Nest1DMargin;
            Nest1DLayoutCanvas.Height = top + Nest1DMargin;
        }

        private void DrawStick(Stick stick, float kerf, double top)
        {
            var bar = new Rectangle
            {
                Width = stick.StickLength,
                Height = Nest1DStickHeight,
                Stroke = NestOutlineBrush,
                StrokeThickness = NestOutlineThickness,
                Fill = NestStockFillBrush,
                ToolTip = $"{stick.StickLength:0.##}\" stick, {stick.NestedParts.Count} part(s), " +
                          $"{Math.Max(0f, stick.RemainingLength):0.##}\" left over"
            };

            Canvas.SetLeft(bar, Nest1DMargin);
            Canvas.SetTop(bar, top);
            Nest1DLayoutCanvas.Children.Add(bar);

            double partTop = top + (Nest1DStickHeight - Nest1DPartHeight) / 2.0;
            double x = Nest1DMargin;

            foreach (var part in stick.NestedParts)
            {
                var partBar = new Rectangle
                {
                    Width = part.length,
                    Height = Nest1DPartHeight,
                    Stroke = NestOutlineBrush,
                    StrokeThickness = NestOutlineThickness,
                    Fill = NestPartFillBrush,
                    ToolTip = PartToolTip(part)
                };

                Canvas.SetLeft(partBar, x);
                Canvas.SetTop(partBar, partTop);
                Nest1DLayoutCanvas.Children.Add(partBar);

                //Advance by the part plus the cut that frees it, using the nest's own kerf rather
                //than a copy of the number - the drawing should show the gaps the nest reserved.
                x += part.length + kerf;
            }
        }

        //Part number and description on the first line (either may be missing on a BOM row), then
        //the length that was cut.
        private static string PartToolTip(Part part)
        {
            string name = string.Join("  ", new[] { part.PartNumber, part.Description }
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            string length = $"Length {part.length:0.###}\"";

            return name.Length == 0 ? length : name + Environment.NewLine + length;
        }

        //Rough width of a run of text at the size DrawText uses, for working out how far the
        //drawing extends. Only feeds the canvas size, so an approximation is enough - measuring
        //would mean a layout pass per label.
        private static double EstimateTextWidth(string text) =>
            string.IsNullOrEmpty(text) ? 0.0 : text.Length * 5.0;

        // Refreshes just the grid's values, leaving the per-group combo box controls alone -
        // those are built once per loaded BOM by BuildNestDataGrid.
        private void RefreshNestDataGrid()
        {
            //Updates the existing rows in place rather than clearing and refilling. The stock
            //selector now lives in the row, so a rebuild here would destroy the very row whose
            //ComboBox raised the event that got us here. It also keeps scroll position and the
            //user's current selection.
            int row = 0;

            foreach (var nest in this.prs.PNestList)
            {
                if (row >= NestData.Count)
                    break;

                var view = NestData[row++];
                view.Efficiency = nest.Efficiency;

                //Sheets when the group is nested, finished pieces when it is bought by the piece -
                //the same number the export quotes, so the grid and the BOM cannot differ.
                view.StockCount = nest.PurchaseQuantity.ToString();
                view.UnnestedCount = nest.UnnestedList.Count.ToString();

                // Safe to assign rather than needing to suppress the callback: the setter is a
                // no-op when the value already matches, and by the time a re-nest gets here the
                // policy already is whatever was picked.
                view.SelectedMaterial = nest.Policy.Name;

                // A fixed size is already showing correctly - the user's own pick set it, and
                // nothing here changes it again. A Smallest Drop group is different: its size is
                // recomputed by this very Nest() call, so without this the column would go on
                // showing the bare "Smallest Drop" label forever with no way to see what it
                // actually bought.
                if (nest.SizeToSmallestDrop)
                {
                    string current = SmallestDropDisplay(StockSize.Format(nest.SheetWidth, nest.SheetLength));
                    view.SetCurrentStock(WithCurrent(SheetSizeOptions, current), current);
                }
            }

            foreach (var nest in this.prs.TNestList)
            {
                if (row >= NestData.Count)
                    break;

                var view = NestData[row++];
                // Blank when bought by the piece: there is no stick to use well or badly, and a
                // stale percentage reads as one that was measured.
                view.Efficiency = nest.Purchase == StickPurchase.Pieces ? "" : nest.Efficiency.ToString();
                view.StockCount = nest.PurchaseQuantity.ToString();
                view.UnnestedCount = nest.UnnestedList.Count.ToString();

                if (nest.SizeToSmallestDrop)
                {
                    string current = SmallestDropDisplay(nest.StickLength.ToString("0.##"));
                    view.SetCurrentStock(WithCurrent(StickLengthOptions, current), current);
                }
            }
        }

        // Both named here rather than spelled out at each use, and shared with
        // NestGroupCoordinator rather than duplicated, so the option text a picker shows and the
        // text it is matched against can never drift apart.
        private const string SheetQuantityOption = NestGroupCoordinator.SheetQuantityOption;
        private const string SmallestDropOption = NestGroupCoordinator.SmallestDropOption;

        // "QTY" is last so the list reads as sizes first, the way the stick list puts "FT" after
        // its lengths.
        private static readonly string[] SheetSizeOptions = { "48 x 96", "48 x 120", "60 x 120", SmallestDropOption, SheetQuantityOption };
        private static readonly string[] StickLengthOptions = { "144", "240", "252", "288", SmallestDropOption, "FT", NestGroupCoordinator.StickQuantityOption };

        // Which machine a linear-stock group is cut on - the material column's only two choices
        // for a TNest row, the same way MaterialPolicy.All supplies PNest's.
        private static readonly string[] CutMethodOptions =
            { NestGroupCoordinator.TubeLaserOption, NestGroupCoordinator.SawOption };

        // The text a Smallest Drop row's stock column shows: the mode's name plus the size it
        // actually computed, so the column reads the same information every other row does
        // instead of a label with no dimensions behind it.
        private static string SmallestDropDisplay(string size) => $"{SmallestDropOption} ({size})";

        // A ComboBox whose SelectedItem isn't in its ItemsSource just renders blank, with no error
        // to notice - so rather than assume the current stock size is always one of the presets,
        // include it when it isn't. A size set outside the preset list then still displays
        // truthfully instead of silently showing the wrong one or nothing at all.
        //
        // When the current text is a Smallest Drop size, the bare SmallestDropOption preset is
        // dropped from the list rather than kept alongside it - otherwise the dropdown would offer
        // two entries that both mean "Smallest Drop" side by side, one with the computed size and
        // one without.
        private static IReadOnlyList<string> WithCurrent(string[] presets, string current)
        {
            if (presets.Contains(current))
                return presets;

            IEnumerable<string> basePresets = current.StartsWith(SmallestDropOption, StringComparison.Ordinal)
                ? presets.Where(p => p != SmallestDropOption)
                : presets;

            var options = new List<string> { current };
            options.AddRange(basePresets);
            return options;
        }

        // Builds one grid row per nest group, each wired to the group it represents. Only needs to
        // run once per loaded BOM - the set of groups doesn't change while nesting runs, only
        // their results (handled by RefreshNestDataGrid).
        private void BuildNestDataGrid()
        {
            NestData.Clear();

            foreach (var nest in this.prs.PNestList)
            {
                //A group bought by the piece is on no sheet size, so showing one would claim a
                //stock it is not buying.
                string current = nest.Purchase == SheetPurchase.Pieces
                    ? SheetQuantityOption
                    : StockSize.Format(nest.SheetWidth, nest.SheetLength);

                var view = new NestedItemsGridView
                {
                    Description = nest.Description,
                    StockOptions = WithCurrent(SheetSizeOptions, current),
                    MaterialOptions = MaterialPolicy.All.Select(p => p.Name).ToList()
                };

                //Seed the selection before wiring the callback, so showing the current sheet size
                //doesn't count as the user picking it and kick off a re-nest.
                view.SelectedStock = current;
                view.StockChanged = choice => ApplySheetSizeChoice(nest, choice);

                view.SelectedMaterial = nest.Policy.Name;
                view.MaterialChanged = choice => ApplyMaterialChoice(nest, choice);

                NestData.Add(view);
            }

            foreach (var nest in this.prs.TNestList)
            {
                string current = nest.Purchase == StickPurchase.Pieces
                    ? NestGroupCoordinator.StickQuantityOption
                    : nest.StickLength.ToString("0.##");

                var view = new NestedItemsGridView
                {
                    Description = nest.Description,
                    StockOptions = WithCurrent(StickLengthOptions, current),

                    // Linear stock has no sheet material to choose, but it does have a cutting
                    // machine, which changes whether the clamp allowance applies - see
                    // TNest.CutOnSaw.
                    MaterialOptions = CutMethodOptions
                };

                view.SelectedStock = current;
                view.StockChanged = choice => ApplyStickLengthChoice(nest, choice);

                view.SelectedMaterial = nest.CutOnSaw
                    ? NestGroupCoordinator.SawOption
                    : NestGroupCoordinator.TubeLaserOption;
                view.MaterialChanged = choice => ApplyCutMethodChoice(nest, choice);

                NestData.Add(view);
            }

            NestedItemsDataGrid.ItemsSource = NestData;
            RefreshNestDataGrid();
        }

        // The row's own ComboBox raised this. Routing through the row rather than relying on the
        // SelectedItem binding writing back is what makes the pick actually take effect - see
        // NestedItemsGridView.ApplyUserSelection.
        private void StockSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is NestedItemsGridView view)
                view.ApplyUserSelection(combo.SelectedItem as string);
        }

        private void MaterialSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is NestedItemsGridView view)
                view.ApplyMaterialSelection(combo.SelectedItem as string);
        }

        // UI wrapper around NestGroupCoordinator.ApplyMaterialChoice: guards against a pick racing
        // a background nest, then refreshes whichever views the outcome says are now stale.
        private void ApplyMaterialChoice(PNest pnest, string choice)
        {
            if (NestInProgress("A material change"))
                return;

            var outcome = NestGroupCoordinator.ApplyMaterialChoice(pnest, choice);
            RefreshForOutcome(outcome);
        }

        // UI wrapper around NestGroupCoordinator.ApplySheetSizeChoice - see there for why the
        // choice is parsed rather than matched against known option text.
        private void ApplySheetSizeChoice(PNest pnest, string choice)
        {
            if (NestInProgress("A stock size change"))
                return;

            var result = NestGroupCoordinator.ApplySheetSizeChoice(pnest, choice);

            if (result.Error is not null)
            {
                // Say so rather than ignoring the pick, which is the failure this method was
                // rewritten to remove.
                Notify(NotificationLevel.Warning, "Sheet size not applied", result.Error);
                return;
            }

            RefreshForOutcome(result.Outcome);
        }

        private void ApplyStickLengthChoice(TNest tnest, string choice)
        {
            // This used to carry a comment arguing no guard was needed, on the grounds that 1D
            // nesting shares no state with the background 2D run. That is not true: RunNest nests
            // TNestList on the background task as well, so a stick length picked mid-run races the
            // very TNest being nested. The guard also no longer swallows the pick - it says why.
            if (NestInProgress("A stock length change"))
                return;

            var outcome = NestGroupCoordinator.ApplyStickLengthChoice(tnest, choice);
            if (outcome == NestChangeOutcome.NoChange)
                return;

            RefreshNestDataGrid();
            RefreshNest1DView();
        }

        // UI wrapper around NestGroupCoordinator.ApplyCutMethodChoice - same guard and refresh as
        // ApplyStickLengthChoice, since this also re-nests the group on the UI thread.
        private void ApplyCutMethodChoice(TNest tnest, string choice)
        {
            if (NestInProgress("A cut method change"))
                return;

            var outcome = NestGroupCoordinator.ApplyCutMethodChoice(tnest, choice);
            if (outcome == NestChangeOutcome.NoChange)
                return;

            RefreshNestDataGrid();
            RefreshNest1DView();
        }

        // Both PNest pickers refresh the same way for the same reasons - DataOnly means the group's
        // data changed but it was not re-nested, so the 2D drawing is still accurate; Renested means
        // it is not.
        private void RefreshForOutcome(NestChangeOutcome outcome)
        {
            switch (outcome)
            {
                case NestChangeOutcome.Renested:
                    RefreshNest2DView();
                    RefreshNestDataGrid();
                    break;
                case NestChangeOutcome.DataOnly:
                    RefreshNestDataGrid();
                    break;
                case NestChangeOutcome.NoChange:
                    break;
            }
        }

        // Enter commits immediately, the same as tabbing away - typing a new unit count and
        // hitting Enter is the expected way to trigger a renest, not a way to insert a newline
        // nowhere.
        private void UnitsTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            CommitUnitsChange();
            e.Handled = true;
        }

        private void UnitsTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitUnitsChange();

        // Parses what was typed and, if it names a real change, renests every group for it.
        // Refused rather than defaulted - unlike the spacing boxes, a bad value here has nowhere
        // safe to fall back to: quietly picking some other unit count and renesting on it would
        // change every quantity in the job to a number nobody asked for.
        private void CommitUnitsChange()
        {
            string text = UnitsTextBox.Text.Trim();

            if (!int.TryParse(text, out int newUnits) || newUnits <= 0)
            {
                Notify(NotificationLevel.Warning, "Units not applied", $"'{text}' is not a usable number of units, so it was left at " +
                                $"{this.prs.Units}.");
                UnitsTextBox.Text = this.prs.Units.ToString();
                return;
            }

            if (newUnits == this.prs.Units)
                return;

            ApplyUnitsChange(newUnits);
        }

        // Enter commits immediately, the same as tabbing away - see UnitsTextBox_KeyDown.
        private void SpacingTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            CommitSpacingChange();
            e.Handled = true;
        }

        private void SpacingTextBox_LostFocus(object sender, RoutedEventArgs e) => CommitSpacingChange();

        // Shows the spacing of whatever the 2D tab is showing: that group's own values, or - for
        // "All materials" - the shared value when every group agrees and blank when they differ, so
        // the box never claims a figure some group is not nested with.
        private void SyncSpacingBoxes()
        {
            var groups = SelectedNest2DGroups();

            SetSpacingBox(PartSpacingTextBox, groups.Select(g => g.PartSpacing).Distinct().ToList());
            SetSpacingBox(SheetSpacingTextBox, groups.Select(g => g.SheetSpacing).Distinct().ToList());

            bool editable = groups.Count > 0 && !_nestRunning;
            PartSpacingTextBox.IsEnabled = editable;
            SheetSpacingTextBox.IsEnabled = editable;
        }

        private static void SetSpacingBox(TextBox box, List<float> distinct) =>
            box.Text = distinct.Count == 1
                ? distinct[0].ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)
                : string.Empty;

        // Reads one spacing box. A blank box means "the default" when a single group is shown, but
        // "leave each group as it is" when several are - a blank there is the mixed display, not a
        // request. Returns null for that case.
        private static float? ReadSpacingBox(TextBox box, string label, List<string> notes, bool blankMeansKeep,
            float defaultValue = SpacingInput.Default, bool allowZero = false)
        {
            if (blankMeansKeep && string.IsNullOrWhiteSpace(box.Text))
                return null;

            return SpacingInput.Read(box.Text, label, notes, defaultValue, allowZero);
        }

        // Applies the spacing boxes to the group(s) the 2D tab is showing and renests them. Read as
        // a pair rather than per-box, since tabbing through both fields on one edit would otherwise
        // renest twice for a single change - once per box losing focus.
        //
        // Unlike Units, a bad value here does have somewhere safe to fall back to - SpacingInput's
        // own default - so this never refuses the way CommitUnitsChange does; it renests on
        // whatever was actually used and says so.
        private void CommitSpacingChange()
        {
            var groups = SelectedNest2DGroups();
            if (groups.Count == 0)
                return;

            var notes = new List<string>();
            bool blankMeansKeep = groups.Count > 1;

            float? sheetSpacing = ReadSpacingBox(SheetSpacingTextBox, "Sheet Spacing", notes, blankMeansKeep);
            float? partSpacing = ReadSpacingBox(PartSpacingTextBox, "Part Spacing", notes, blankMeansKeep);

            if (notes.Count > 0)
            {
                foreach (string note in notes)
                    Log.Warn(note);

                Notify(NotificationLevel.Warning, "Spacing not applied", string.Join(Environment.NewLine + Environment.NewLine, notes));
            }

            // Only a group whose values would actually change matters, which also makes the
            // LostFocus that follows an Enter (or a plain tab through) a no-op.
            var affected = groups.Where(g => (sheetSpacing ?? g.SheetSpacing) != g.SheetSpacing
                                             || (partSpacing ?? g.PartSpacing) != g.PartSpacing).ToList();

            if (affected.Count == 0)
            {
                // Shown back so the box always reads as what will actually be nested with, not what
                // was typed.
                SyncSpacingBoxes();
                return;
            }

            if (NestInProgress("A spacing change"))
            {
                SyncSpacingBoxes();
                return;
            }

            bool renested = false;
            foreach (var group in affected)
            {
                var outcome = NestGroupCoordinator.ApplyGroupSpacingChange(group,
                    sheetSpacing ?? group.SheetSpacing, partSpacing ?? group.PartSpacing);
                renested |= outcome == NestChangeOutcome.Renested;
            }

            if (renested)
                RefreshNest2DView();
            else
                SyncSpacingBoxes();

            RefreshNestDataGrid();
            SetStatus("Spacing changed: " + NestSummary());

            Log.Info($"Spacing changed for {affected.Count} sheet group(s): " +
                     $"{sheetSpacing:0.###}\" sheet / {partSpacing:0.###}\" part.");
        }

        // UI wrapper around NestGroupCoordinator.ApplyUnitsChange - see there for why every group
        // is rescaled from PerUnitQuantity rather than from its current quantity.
        private void ApplyUnitsChange(int newUnits)
        {
            if (NestInProgress("A units change"))
            {
                UnitsTextBox.Text = this.prs.Units.ToString();
                return;
            }

            NestGroupCoordinator.ApplyUnitsChange(this.prs, newUnits);

            RefreshNest2DView();
            RefreshNestDataGrid();
            RefreshNest1DView();
            RefreshEndFeaturesView();
            SetStatus("Units changed: " + NestSummary());

            Log.Info($"Units changed to {newUnits}; every group renested.");
        }

        private void SaveSheetToDXF(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("A DXF export"))
            {
                e.Handled = true;
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "DXF Files (*.dxf)|*.dxf";

            if (saveFileDialog.ShowDialog() != true)
            {
                e.Handled = true;
                return;
            }

            string baseDir = System.IO.Path.GetDirectoryName(saveFileDialog.FileName) ?? string.Empty;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(saveFileDialog.FileName);

            //One DXF file per distinct layout, across every material/thickness group, named after
            //the chosen file plus that group's description, a number, and how many sheets are cut
            //that way.
            //
            //Per layout rather than per sheet: sheets nested identically are one program the shop
            //runs that many times, so 834 copies of the same file is 833 files nobody wants. The
            //count goes in the name, where it cannot be separated from the geometry it applies to.
            //
            //Group names are used as part of a filename, and they come from the BOM description -
            //free text, and in practice full of fraction slashes ("3/16 x 3 x 4-3/4") and colons.
            //Those have to be scrubbed, not just spaces: a slash makes Path.Combine read the rest
            //of the name as a subdirectory that was never created.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int written = 0;
            int sheetsCovered = 0;

            try
            {
                foreach (var list in this.prs.PNestList)
                {
                    string safeDescription = NestFileNaming.UniqueName(NestFileNaming.SafeFileNamePart(list.Description), used);

                    var layouts = SheetLayouts.Group(list.Sheets);

                    for (int i = 0; i < layouts.Count; i++)
                    {
                        string fileName = System.IO.Path.Combine(baseDir,
                            $"{baseName}_{safeDescription}_{i + 1}_x{layouts[i].Count}.dxf");

                        dxfFileWriter.WriteDXF(layouts[i].Representative, fileName);
                        written++;
                    }

                    sheetsCovered += list.Sheets?.Count ?? 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("DXF export failed", ex);
                ShowFailure(NotificationLevel.Warning, "DXF Export Failed", ex.Message);
                e.Handled = true;
                return;
            }

            if (written == 0)
            {
                Notify(NotificationLevel.Info, "Export to DXF", "There are no nested sheets to export yet.");
            }
            else
            {
                //Both numbers, because the gap between them is the point: the file count is what
                //gets programmed, the sheet count is what gets bought.
                Log.Info($"Exported {written} DXF file(s) covering {sheetsCovered} sheet(s).");

                Notify(NotificationLevel.Info, "Export to DXF", $"Wrote {written} DXF file(s) for {sheetsCovered} sheet(s)." +
                                $"{Environment.NewLine}{Environment.NewLine}" +
                                "Sheets nested the same way share one file - the x count in each " +
                                "file name is how many times to run it.");
            }

            e.Handled = true;
        }

        private void SaveFile(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("An export"))
                return;

            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "Microsoft Excel Spreadsheet (*.xlsx)|*.xlsx";

            if (dlg.ShowDialog() == true)
            {
                wrt.AddList(this.prs.TNestList);
                wrt.AddSheetNest(this.prs.PNestList);
                wrt.SendToFile(dlg.FileName, this.prs.PNestList, this.prs.TNestList, this.prs.SourcePath,
                               this.prs.Units);

                // A save that quietly did nothing used to look exactly like one that worked, so
                // both outcomes are now stated.
                if (wrt.Errors.Count > 0)
                {
                    foreach (string error in wrt.Errors)
                        Log.Warn($"Export failed: {error}");

                    ShowFailure(NotificationLevel.Warning, "Results were not saved", string.Join(Environment.NewLine + Environment.NewLine, wrt.Errors));
                }
                else
                {
                    Log.Info($"Exported results to {System.IO.Path.GetFileName(dlg.FileName)}");

                    Notify(NotificationLevel.Info, "Export complete", $"Results written to {System.IO.Path.GetFileName(dlg.FileName)}.");
                }
            }


        }

        private void LoadFile(object sender, RoutedEventArgs e)
        {

            Nest2DLayoutCanvas.Children.Clear();



            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Microsoft Excel Spreadsheet (*.xlsx)|*.xlsx";



            if (dlg.ShowDialog() == true)
            {
                FileNameTextBlock.Text = dlg.FileName;
                Title = $"{_baseTitle} - {System.IO.Path.GetFileName(dlg.FileName)}";
                prs = new FileParser();
                prs.Parse(dlg.FileName);

                // Loading a BOM is where most support questions start, so record what came out of
                // it: which file, how many groups, and every note the parser raised.
                Log.Info($"Loaded {System.IO.Path.GetFileName(dlg.FileName)}: " +
                         $"{prs.PNestList.Count} sheet group(s), {prs.TNestList.Count} linear group(s), " +
                         $"{prs.Errors.Count} error(s), {prs.Warnings.Count} note(s)");

                foreach (string note in prs.Errors)
                    Log.Warn($"BOM error: {note}");

                foreach (string note in prs.Warnings)
                    Log.Warn($"BOM note: {note}");

                // The parser reports rather than displays, so showing what went wrong is on us.
                if (prs.Errors.Count > 0)
                    ShowFailure(NotificationLevel.Warning, "Could not read the bill of materials", string.Join(Environment.NewLine + Environment.NewLine, prs.Errors));

                // Parts nesting as rectangles, and part numbers matching several DXFs, both move
                // the sheet count without stopping the nest - so they are worth showing even
                // though the BOM read successfully.
                if (prs.Warnings.Count > 0)
                    Notify(NotificationLevel.Info, "Notes on this bill of materials", string.Join(Environment.NewLine + Environment.NewLine, prs.Warnings));
            }
            else
                return;

            // Puts the file dialogs in the job's folder for the rest of the session, so exporting
            // lands beside the BOM. GetDirectoryName returns null for a path with no directory
            // part, which threw before the guard - and being unable to move the working directory
            // is no reason to abandon a BOM that read fine.
            string? jobFolder = System.IO.Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(jobFolder))
            {
                try
                {
                    Directory.SetCurrentDirectory(jobFolder);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not switch the working directory to '{jobFolder}': {ex.Message}");
                }
            }

            foreach (var lst in this.prs.PNestList)
            {
                lst.SheetWidth = 48.0f;
                lst.SheetLength = 96.0f;
            }

            // Every group starts at the default spacing and is then adjusted per group from the
            // 2D tab; RefreshNest2DView (run when the nest finishes) shows the values in the boxes.
            foreach (var list in this.prs.PNestList)
            {
                list.SheetSpacing = SpacingInput.Default;
                list.PartSpacing = SpacingInput.Default;
            }

            UnitsTextBox.Text = this.prs.Units.ToString();

            BuildNestDataGrid();
            RefreshEndFeaturesView();
            SetBusy(true);
            SetStatus("Nesting...");

            _ = RunNestAsync();
        }

        private void New_File(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("Starting a new BOM"))
                return;

            OpenFileDialog dlg = new OpenFileDialog();

            if (dlg.ShowDialog() == true)
            {
                this.newBOM = new BillOfMaterials();
                this.newBOM.ReadFile(dlg.FileName);

                // Stop if reading failed - writing out a bill of materials that was never read
                // produces an empty one, which is worse than not producing it.
                if (this.newBOM.Errors.Count > 0)
                {
                    foreach (string error in this.newBOM.Errors)
                        Log.Warn($"Bill of materials: {error}");

                    ShowFailure(NotificationLevel.Warning, "Could not build the bill of materials", string.Join(Environment.NewLine + Environment.NewLine, this.newBOM.Errors));

                    return;
                }

                BomDetailsWindow.AskAndWrite(this.newBOM, this);
            }
        }

        // A blank Cutwright bill of materials to fill in by hand: the job block, an empty parts table and
        // the Finishes sheet. Cutwright reads only its own format, so this is how a job that has no CSV,
        // PDF or SolidWorks export to import from gets into Cutwright.
        private void NewBlankTemplate(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("Making a blank BOM template"))
                return;

            SaveFileDialog dlg = new SaveFileDialog
            {
                Filter = "Microsoft Excel Spreadsheet (*.xlsx)|*.xlsx",
                Title = "Save the blank bill of materials",
                FileName = "Blank Bill of Materials"
            };

            if (dlg.ShowDialog() != true)
                return;

            var blank = new BillOfMaterials { Units = 1, Date = DateTime.Now.ToString("d", System.Globalization.CultureInfo.CurrentCulture) };
            blank.WriteToFile(dlg.FileName);

            if (blank.Errors.Count > 0)
            {
                foreach (string error in blank.Errors)
                    Log.Warn($"Bill of materials: {error}");

                ShowFailure(NotificationLevel.Warning, "Could not write the bill of materials", string.Join(Environment.NewLine + Environment.NewLine, blank.Errors));
                return;
            }

            Log.Info($"Wrote a blank bill of materials template to {System.IO.Path.GetFileName(blank.Filename)}.");
            Notify(NotificationLevel.Info, "Blank template saved",
                $"{System.IO.Path.GetFileName(blank.Filename)} is ready to fill in. Add parts to the BOM sheet " +
                "(a row with no length is a purchased item), then open it in Cutwright.");
        }

        // Everything that could read a nest, replace a nest, or start a second one, turned off for
        // the duration of a run and back on when it finishes.
        //
        // The grid is built before the worker starts, so its stock and material selectors are live
        // and clickable while the background run is nesting the very same PNest and TNest objects.
        // Picking one re-nests that group on the UI thread, and two threads rebuilding one group's
        // Sheets list produces a torn sheet count or a drawing that disagrees with it. Save and
        // Export to DXF are worse: both read SheetCount and Sheets straight out of a nest that is
        // being rebuilt underneath them, so a half-finished nest gets written to a file and looks
        // exactly like a finished one.
        //
        // Disabled rather than deferred. Remembering a pick and applying it afterwards would be
        // friendlier, but it adds state that can itself be wrong, and for a quoting tool a control
        // that is plainly unavailable beats one that accepts input and acts on it later.
        //
        // The window's close button is never disabled - refusing to let someone close the
        // application because it is mid-nest would be worse than the race it avoids.
        private void SetBusy(bool busy)
        {
            NewBomButton.IsEnabled = !busy;
            NewMenuOption.IsEnabled = !busy;
            BlankTemplateMenuOption.IsEnabled = !busy;
            OpenButton.IsEnabled = !busy;
            ImportCsvMenuOption.IsEnabled = !busy;
            ImportPdfMenuOption.IsEnabled = !busy;
            TypeInNewBomMenuOption.IsEnabled = !busy;
            SaveButton.IsEnabled = !busy;
            ExportDxfButton.IsEnabled = !busy;
            ExportSolidWorksButton.IsEnabled = !busy;

            // Read once when a nest starts, so typing in them mid-run has no effect on the run in
            // progress. Better to say that by greying them out than to accept input and drop it.
            UnitsTextBox.IsEnabled = !busy;

            // Off during a run like Units; back on only if there is a group to edit.
            PartSpacingTextBox.IsEnabled = !busy && this.prs.PNestList.Count > 0;
            SheetSpacingTextBox.IsEnabled = !busy && this.prs.PNestList.Count > 0;
            KerfTextBox.IsEnabled = !busy && this.prs.TNestList.Count > 0;
            MinCutLengthTextBox.IsEnabled = !busy && this.prs.TNestList.Count > 0;

            //Reads the nests in order to draw them, so it waits like everything else.
            Nest2DGroupSelector.IsEnabled = !busy;
            Nest1DGroupSelector.IsEnabled = !busy;

            // Ends are picked against the nest, which the run is rebuilding, so they wait like the
            // other selectors. The group selector stays live - it only changes what is shown.
            EndFeaturesDataGrid.IsEnabled = !busy && this.prs.TNestList.Count > 0;
            ApplyBulkEndsButton.IsEnabled = !busy && this.prs.TNestList.Count > 0;

            foreach (var row in this.NestData)
                row.SelectorsEnabled = !busy;
        }

        // Second line of defence behind SetBusy, for the same reason the selectors route their
        // picks through the row rather than trusting a binding: a control that should be disabled
        // and somehow is not must still not start a second nest on an object the background task
        // holds.
        private bool NestInProgress(string what)
        {
            if (!_nestRunning)
                return false;

            Log.Warn($"{what} was refused because a nest is still running.");
            Notify(NotificationLevel.Info, "Nest in progress", "The nest is still running. Wait for it to finish, then try again.");
            return true;
        }

        // Runs on a thread-pool thread via Task.Run (see LoadFile) - nothing in here may touch a
        // UI element directly. progress.Report marshals back to the UI thread on its own, since
        // _nestProgress was constructed on it.
        private void RunNest(IProgress<int> progress)
        {
            int total = this.prs.TNestList.Count + this.prs.PNestList.Count;
            int done = 0;

            foreach (var lst in this.prs.TNestList)
            {
                lst.Nest();
                progress.Report(++done * 100 / Math.Max(1, total));
            }

            foreach (var lst in this.prs.PNestList)
            {
                lst.Nest();
                progress.Report(++done * 100 / Math.Max(1, total));
            }

            progress.Report(100);
        }

        // Runs RunNest on the thread pool and hands the outcome to UpdateUI, replacing the old
        // BackgroundWorker.RunWorkerAsync/RunWorkerCompleted pair. _nestRunning is set before the
        // first await so NestInProgress sees the run start synchronously with this call - a picker
        // firing on the UI thread between here and the Task.Run actually starting cannot race it.
        private async Task RunNestAsync()
        {
            _nestRunning = true;

            Exception? error = null;
            try
            {
                await Task.Run(() => RunNest(_nestProgress));
            }
            catch (Exception ex)
            {
                error = ex;
            }

            _nestRunning = false;
            UpdateUI(error);
        }

        private void DrawText(double x, double y, string text, Color color, Canvas Parent)
        {
            TextBlock textBlock = new TextBlock();
            textBlock.FontSize = 8;
            textBlock.Text = text;
            textBlock.Foreground = new SolidColorBrush(color);
            Canvas.SetLeft(textBlock, x);
            Canvas.SetTop(textBlock, y);
            Parent.Children.Add(textBlock);
        }

        // Called on the UI thread after the background nest task finishes, whether it succeeded or
        // threw - see RunNestAsync.
        private void UpdateUI(Exception? error)
        {
            // First, and before the error branch returns: a failed nest that left the application
            // permanently read-only would be a worse fault than the one that caused it.
            SetBusy(false);

            if (error != null)
            {
                // The stack trace belongs in the log, not in a dialog. Showing ToString() gave the
                // estimator a wall of frame addresses that told them nothing and did not survive
                // closing the box.
                Log.Error("Nesting failed", error);
                SetStatus("The nest did not finish - see the message shown.");

                string where = Log.CurrentFile is null
                    ? string.Empty
                    : $"{Environment.NewLine}{Environment.NewLine}Details were written to:" +
                      $"{Environment.NewLine}{Log.CurrentFile}";

                ShowFailure(NotificationLevel.Error, "Nesting failed", $"The nest did not finish:{Environment.NewLine}{Environment.NewLine}" +
                                error.Message + where);
                return;
            }

            this.RefreshNestDataGrid();
            this.RefreshNest2DView();
            this.RefreshNest1DView();
            RefreshEndFeaturesView();
            SetStatus("Nested: " + NestSummary());

            //Surface anything the estimator should know about the nest rather than letting it
            //pass silently: DXFs that couldn't be read (part nested from its BOM size instead),
            //parts too large for the chosen sheet, and gusset pairing.
            var warnings = this.prs.PNestList.SelectMany(l => l.Warnings).ToList();
            if (warnings.Count > 0)
                Notify(NotificationLevel.Warning, "Nesting notes", string.Join("\n", warnings));
        }

        // Everything the program says that is not a question. Shown from the bell in the status bar
        // instead of as dialogs: a message box stops the estimator's work to tell them something
        // that mostly needs no answer.
        private readonly NotificationLog _notifications = new();

        //The title without the file name, which is appended once a BOM is open.
        private readonly string _baseTitle;

        // Records a message in the log and puts its first line in the status bar, so an action
        // that was refused or finished still visibly did something even if nobody opens the bell.
        private void Notify(NotificationLevel level, string title, string message)
        {
            _notifications.Add(level, title, message);

            string firstLine = message.Split('\n', 2)[0].Trim();
            SetStatus(firstLine.Length == 0 ? title : title + ": " + firstLine);
        }

        // A failure the user must not miss - work stopped - so it still interrupts. Recorded in the
        // log as well, which is then the complete list of what the program said.
        private void ShowFailure(NotificationLevel level, string title, string message)
        {
            _notifications.Add(level, title, message);

            MessageBox.Show(message, title, MessageBoxButton.OK,
                level == NotificationLevel.Error ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }

        //Opens the notification list from the bell. The popup closes itself on any click outside
        //it, including on the bell - so a click on the bell while it is open would close and
        //immediately reopen it. Remembering when it last closed lets that click be ignored.
        private DateTime _notificationsClosedAt = DateTime.MinValue;

        private void NotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.UtcNow - _notificationsClosedAt).TotalMilliseconds < 250)
                return;

            NotificationsPopup.IsOpen = true;
        }

        //What was on the list has been seen once it is dismissed.
        private void NotificationsPopup_Closed(object? sender, EventArgs e)
        {
            _notificationsClosedAt = DateTime.UtcNow;
            _notifications.MarkAllRead();
        }

        //Opens the New BOM dropdown under its button - a Button has no menu of its own, so its
        //ContextMenu is placed and opened by hand on a left click.
        private void NewBomButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = NewBomButton.ContextMenu;
            menu.PlacementTarget = NewBomButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        //Ctrl+O / Ctrl+S, which the File menu's shortcuts used to cover. Ignored while a nest is
        //running, the same as the buttons they stand in for.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            if (e.Key == Key.O && OpenButton.IsEnabled)
            {
                LoadFile(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.S && SaveButton.IsEnabled)
            {
                SaveFile(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void ClearNotifications_Click(object sender, RoutedEventArgs e) => _notifications.Clear();

        private void CopyNotifications_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_notifications.ToText());
                SetStatus("Notifications copied to the clipboard.");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // The clipboard can be held by another program for a moment; not worth a message.
                SetStatus("Could not reach the clipboard - try again.");
            }
        }

        //One line in the status bar saying where things stand - what is running, or what the last
        //nest produced.
        private void SetStatus(string text) => StatusText.Text = text;

        private string NestSummary()
        {
            int sheets = this.prs.PNestList.Sum(l => l.Sheets?.Count ?? 0);
            int sticks = this.prs.TNestList.Sum(l => l.StickCount);
            int unnested = this.prs.PNestList.Sum(l => l.UnnestedList.Count)
                           + this.prs.TNestList.Sum(l => l.UnnestedList.Count);

            string summary = $"{this.prs.PNestList.Count} sheet group(s) on {sheets} sheet(s), " +
                             $"{this.prs.TNestList.Count} stick group(s) on {sticks} stick(s)";

            return unnested == 0
                ? summary
                : summary + $" - {unnested} part(s) did not fit (see the red rows)";
        }

        //Builds one of our bills of materials from a CSV of a customer's drawing table.
        //
        //The drawing itself is put through Tabula first, which is what turns the PDF's table into
        //rows and columns; this reads that CSV. The command was called "Import From PDF", which
        //described where the data started rather than what the command takes, and the file dialog
        //has always filtered for .csv.
        private void ImportCsvBom(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("A CSV import"))
                return;

            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Comma Separated Values (*.csv)|*.csv",
                Title = "Choose the CSV exported from the drawing"
            };

            if (dlg.ShowDialog() == true)
                new CsvImportWindow(dlg.FileName).Show();
        }

        private void ImportPdfBom(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("A PDF import"))
                return;

            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "PDF Files (*.pdf)|*.pdf",
                Title = "Choose the customer drawing to pull a bill of materials from"
            };

            if (dlg.ShowDialog() == true)
                new PdfImportWindow(dlg.FileName).Show();
        }

        private void TypeInNewBom(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("Typing in a new BOM"))
                return;

            new NewBomWindow().Show();
        }

        // One plain SolidWorks part per unique part on the loaded BOM - see SolidWorksPartsWindow.
        // Several people run Cutwright without SolidWorks, so its absence is checked for up front and
        // explained, before anything tries to start it.
#if SOLIDWORKS
        private void ExportSolidWorksParts(object sender, RoutedEventArgs e)
        {
            if (NestInProgress("A SolidWorks export"))
                return;

            var parts = (this.prs.TNestList ?? new List<TNest>()).SelectMany(n => n.Parts)
                .Concat((this.prs.PNestList ?? new List<PNest>()).SelectMany(n => n.Parts))
                .ToList();

            if (parts.Count == 0)
            {
                Notify(NotificationLevel.Info, "No BOM loaded", "Open a BOM first - the SolidWorks parts are built from the loaded BOM's DET section.");
                return;
            }

            if (!SolidWorksSession.IsInstalled)
            {
                Notify(NotificationLevel.Info, "SolidWorks not found", "SolidWorks is not installed on this computer, so parts cannot be built here. " +
                    "Open this BOM in Cutwright on a computer that has SolidWorks.");
                return;
            }

            // The part templates are the one thing that cannot be guessed. Without them nothing can
            // be built, so say how to set them up now rather than after the review window opens.
            if (!SolidWorksPaths.TemplatesConfigured)
            {
                CutwrightSettings.EnsureFile();

                ShowFailure(NotificationLevel.Warning, "SolidWorks export is not set up",
                    "The SolidWorks parts export needs to know where your part templates are " +
                    "(STEEL.prtdot, STAINLESS.prtdot, ALUMINUM.prtdot)." + Environment.NewLine + Environment.NewLine +
                    "Open this file, set TemplateFolder to that folder, and restart Cutwright:" + Environment.NewLine +
                    CutwrightSettings.DefaultFilePath + Environment.NewLine + Environment.NewLine +
                    "Optional: ShopProfileFolder (your own weldment profiles), BendTable, MaterialLibrary and " +
                    "AnsiProfileFolder (if SolidWorks is installed somewhere unusual).");
                return;
            }

            new SolidWorksPartsWindow(parts, this.prs.SourcePath) { Owner = this }.Show();
        }
#else
        // This build was made without the SolidWorks API (see Directory.Build.props), so the export
        // is not in it and its button is hidden; this only exists so the button's handler does.
        private void ExportSolidWorksParts(object sender, RoutedEventArgs e) =>
            Notify(NotificationLevel.Info, "SW Parts not included",
                "This build of Cutwright was made without the SolidWorks parts export.");
#endif

        FileParser prs;

        FileWriter wrt;
        DXFWriter dxfFileWriter;

        ObservableCollection<NestedItemsGridView> NestData;

        private readonly IProgress<int> _nestProgress;
        private bool _nestRunning;




        BillOfMaterials newBOM;


    }

}