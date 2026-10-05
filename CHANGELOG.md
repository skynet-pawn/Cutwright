# Changelog

Notable changes to Cutwright (formerly SPNest), by version. Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

Version numbers are `Major.Minor.Patch`: Major/Minor are bumped by hand in `Cutwright.csproj`
(`VersionMajor`/`VersionMinor`), Patch is the total git commit count at build time and needs
no manual bookkeeping. When you bump Major or Minor, move the `Unreleased` entries below under
a new version heading.

## Unreleased

- **New BOM workbook format (format 2).** A bill of materials is now a plain workbook with no merged cells, laid out as
  named cells and Excel tables (BOM, Finishes, Purchase, End Features sheets) and read by name, not cell position.
  Finish, stencil and stickers move off the parts list onto a **Finishes** sheet with a basis per row (coverage,
  per area, each) that works out quantities from area, coverage and waste. **New BOM > Blank Template** saves an
  empty workbook to fill in. Cutwright reads only this format (other spreadsheets are refused with a pointer to the
  template); **Upgrade Old BOM** and the old-layout reader/writer are removed, and the old TUBE END column is gone
  (ends live on the End Features sheet). Unit costs typed into the Purchase sheet are kept when it is rebuilt.
  New settings: `PowderCoverageSqFtPerLb`, `WetPaintCoverageSqFtPerGal`, `FinishWastePercent`. Files in the new
  format cannot be opened by earlier versions.

- The SolidWorks parts export is now optional at build time. If SolidWorks' API assemblies are found
  (a machine with SolidWorks installed) it is included exactly as before; if not (a contributor or CI build) it is
  left out, the SW Parts button is hidden, and Cutwright builds with plain `dotnet build`. Force it with
  `-p:WithSolidWorks=true|false`. See the README.
- Licensed under the Apache License 2.0 (`LICENSE`, `NOTICE`).


## 1.2 - 2026-09-30

- **Renamed from SPNest to Cutwright**, with a new icon (a C with cut lines through it). The program is now `Cutwright.exe`, and the project, solution,
  test project and C# namespace are renamed to match. Logs and the SolidWorks profile cache moved to
  `%LOCALAPPDATA%\Cutwright` (the cache rebuilds itself; old logs are in `%LOCALAPPDATA%\SPNest`).
- **Cleanup for a standalone project.** Customer files (sample BOMs, drawings, DXFs, SolidWorks parts,
  Tabula tables) and the bundled Tabula app are out of the repository; tests that used them look in a
  git-ignored `Samples` folder and skip without it, and the one-off investigations that pointed at a
  private server were removed. The shop-specific callout naming is now generic - the "stock callout" (`StockCallout`;
  the callout text itself is unchanged). The SolidWorks export's template, profile, bend-table and
  material-library locations are no longer built in: they are set in `%LOCALAPPDATA%\Cutwright\settings.json`
  (see the README). Test environment variables are now `CUTWRIGHT_*`.
- Interface refresh of the main window: the spacing/units/file fields moved out of the menu bar into
  their own toolbar row, the doubled status bar is now one (status message plus a labelled progress
  bar), and the window uses a shared dark palette defined in `App.xaml`. The Nested Parts List has
  smaller, centred text, right-aligned numbers, and rows whose group has unnested parts are tinted
  red. The status bar now summarizes each nest (sheet/stick totals and how many parts did not fit).
- **Tube End Features** tab: a grid of the BOM's stick parts with End A / End B dropdowns (Unreviewed,
  Clear, Miter 45, Has features), a Showing group selector, a "set every part shown" shortcut, and a
  plain-words Effect column. An edit re-nests the group immediately. The choices are saved on a new
  **End Features** sheet in the BOM (so the printed BOM is unchanged), read back on open, and kept
  through Upgrade Old BOM. BOMs without the sheet still use the TUBE END column. The nesting engine is
  unchanged: the ends map onto the same states it always read.
- Tube End Features: a **Miter 45 opposed** end option for parts whose two miters run opposite ways
  (parallelogram, "45x45"). Grid columns now have room: cell text has a margin and numbers are
  left-aligned under their headers (this also applies to the Nested Parts List).
- Nest 1D Layouts: **Kerf** and **Min cut length** boxes, per stick group (or all groups under All
  materials), like the spacing boxes on the 2D tab. Min cut length is the clamp allowance reserved at
  the end of each stick (previously fixed at 4.5"); the saw ignores it. Kerf was fixed at 0.125".
- Nest 1D Layouts: a Showing dropdown (like the 2D tab) picks one material group to draw, or All
  materials. Each entry shows its stick count, "not nested", or "bought by the piece".
- Stick groups get a **QTY** entry in the Stock dropdown, like sheet groups already had: nothing is
  nested and the purchase line is the parts' DET quantities in each ("... (CUT PARTS)"). Picking a
  length again goes back to nesting. Also fixed: picking a length after "FT" left the parts marked
  by-the-foot, so the export kept quoting feet for a group nested on sticks.
- The File menu is replaced by a command strip: grouped large icon-and-label buttons (Bill of
  Materials: Open BOM, New BOM dropdown, Upgrade Old; Save & Export: Save, Nest to DXF, SW Parts; Job:
  Units). Exit is gone (use the window's close button); Ctrl+O and Ctrl+S remain. The loaded file moved
  from the toolbar to the left of the status bar and into the window title.
- Notification log: the informational pop-ups in the main window (BOM notes, spacing/units/stock
  messages, export results, nesting notes, "open a BOM first") no longer interrupt. They are collected
  under a bell in the status bar, with a badge counting what has not been seen, coloured by the worst
  of it. Click the bell for the list (newest first) with Copy all and Clear; the latest message also
  shows in the status bar. Failures (nest failed, BOM could not be read or built, save or DXF export
  failed) still show a dialog and are recorded in the log too. Questions ("Build it anyway?") and the
  dialogs inside the import, Type In and SolidWorks windows are unchanged.
- Part Spacing and Sheet Spacing moved from the toolbar to the top of the Nest 2D Layouts tab and are now
  per material group: the boxes edit the group picked in the Showing dropdown, or every group when
  All materials is picked. Each group starts at the 0.25" default on load.
- Nest 1D Layouts: Ctrl + mouse wheel zooms, hovering a part or stick shows its details, and a legend
  explains the colours. Nest 2D Layouts: double-click fits the drawing to the view, hovering a sheet
  shows its size, count and part count, and the same legend was added.
- Versioned builds: the title bar now shows the real assembly version (`Major.Minor.<git commit count>`)
  instead of a hand-typed string.
- File menu reorganized: every way of creating a BOM (SolidWorks BOM export, CSV, PDF, Type In)
  is now under a **New BOM** submenu, and DXF export is under **Export > Nest to DXF**. Open is
  first; items that open a dialog end in "...".
- **File > Export > SolidWorks Parts**: builds one plain SolidWorks part per unique part number in the
  loaded BOM - sheet metal for sheet parts, a weldment member (shop library, then ANSI within 0.010"
  wall, then a sketched cross-section) for stick parts - with Description/LENGTH/WIDTH
  custom properties. Replaces the coworker's standalone Python/VBA BOM Parts tool. Machines without
  SolidWorks get a message instead of the window. Builds steel, stainless, aluminum, HDPE and UHMW.

## 1.1 - 2026-08-28

Baseline entry at the point versioning was formalized. Prior history is in `git log`.
