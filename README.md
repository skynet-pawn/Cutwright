# Cutwright

A Windows desktop tool for fabrication estimating: reads or builds a bill of materials, nests tube/stick and sheet parts onto stock, and generates a buy sheet.

Tube nesting is longest-part-first: it tries to fit the longest remaining part onto the current stick, falls back to shorter parts as needed, and starts a new stick once nothing fits. Sheet nesting is a bottom-left placement algorithm, largest surface area first. Both use rectangular bounding boxes rather than true part outlines.

## Building

Requires the .NET 10 SDK on Windows (the app is WPF and Windows-only).

```
dotnet build Cutwright.csproj
```

**SolidWorks is optional.** The **SW Parts** export talks to SolidWorks through its API assemblies, which only exist on a machine with SolidWorks installed, so it is included only when they are found:

- If SolidWorks' API folder is found (`C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist`, or the `Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x` equivalent), the export is built in automatically.
- Otherwise it is left out, the **SW Parts** button is hidden, and everything else builds and runs normally.
- Force it either way with `-p:WithSolidWorks=true` or `-p:WithSolidWorks=false`, and point at an unusual install with `-p:SolidWorksApiRedist=<folder>`.

The SolidWorks API assemblies are COM assemblies, which only Visual Studio's MSBuild can build. So **when the export is included, build with Visual Studio** (open `Cutwright.sln`) **or the MSBuild from a Developer Command Prompt** (`msbuild Cutwright.csproj /restore`); plain `dotnet build` fails with MSB4803 in that case. The finished exe does **not** need SolidWorks to run.

## Layout

- `Cutwright.Engine/` - the UI-free library: the BOM model and workbook format, the callout translator, stick and sheet nesting, DXF reading and writing. Other programs (Fillet) use it too, so keep WPF and COM out of it.
- `Cutwright.csproj` (repo root) - the WPF app: windows, the nesting view, and the SolidWorks export.

## License

Cutwright is licensed under the [Apache License, Version 2.0](LICENSE). Third-party components and their licences are listed in [NOTICE](NOTICE).

SolidWorks is a registered trademark of Dassault Systèmes SolidWorks Corporation. Cutwright is an independent project, is not affiliated with or endorsed by Dassault Systèmes, and mentions SolidWorks only to describe the files and API it works with.

Cutwright was written by Thomas Steven Davidson. Much of the code since August 2026 was written with AI assistance (Anthropic's Claude) under the author's direction.

## Versioning

The version shown in the title bar (`Major.Minor.Patch`) is set at build time, not typed by
hand. `Major`/`Minor` live in `Cutwright.csproj` (`VersionMajor`/`VersionMinor`) and only change
when you bump them deliberately; `Patch` is the total git commit count, so every build gets a
unique, ever-increasing number automatically. Note what changed in `CHANGELOG.md` as you go,
and move its `Unreleased` entries under a new heading when you bump Major or Minor.

## Publishing a release

The project is set up to publish as a single portable `.exe` - self-contained (no .NET runtime needed on the target machine) and with WPF's native interop DLLs bundled into the exe rather than left sitting next to it:

```
dotnet publish Cutwright.csproj -c Release -r win-x64 --self-contained true -o publish
```

This produces `publish/Cutwright.exe` plus a `.pdb` (debug symbols - not needed to run the app, only useful for diagnosing a crash later). Nothing else needs to ship alongside the exe.

## Usage

### The command strip

There is no menu bar. Across the top, grouped buttons: **Bill of Materials** (Open BOM, New BOM), **Save & Export** (Save, Nest to DXF, SW Parts) and **Job** (Units). **New BOM** opens a small list of the ways to start one. Ctrl+O and Ctrl+S open and save. The buttons grey out while a nest is running. The loaded file's full path is at the left of the status bar and its name is in the window title.

### Starting a bill of materials

There are a few ways to get a BOM into Cutwright, depending on what you're starting from:

- **Open BOM** - opens a Cutwright bill of materials (an `.xlsx` in Cutwright's own format, described below), to re-nest, adjust, or re-export. Cutwright reads only its own format: any other spreadsheet is refused with a pointer to the blank template.
- **New BOM > Blank Template** - saves an empty Cutwright workbook to fill in by hand (job block, parts table, Finishes sheet). Fill it in Excel, then open it in Cutwright.
- **New BOM > From SolidWorks BOM Export** - reformats an existing spreadsheet into a Cutwright bill of materials. Expects one header row, then data starting row 2, in columns A through F: Part Number, Description, Quantity (per unit), Job Quantity (total for the whole job - used to work out the Units multiplier), Length, Width.
- **New BOM > From CSV** - reads a table already pulled off a customer drawing with the standalone [Tabula](https://tabula.technology/) app, then asks which column is which (quantity, description, length, width, part number, material, etc.) - customer tables vary too much in shape to assume a fixed layout.
- **New BOM > From PDF** - reads a customer drawing PDF directly and finds its ruled tables automatically (using Tabula's extraction engine, no separate app needed). Shows every ruled table it found so you can pick which one(s) are the actual bill of materials - a BOM's header sometimes repeats partway down a page, splitting it into two blocks; check both and they merge in order. A drawing whose table has no visible grid lines won't be found this way - fall back to the manual Tabula-CSV path for those.
- **New BOM > Type In** - a blank grid for building a BOM from scratch by hand, when there's no source file at all.

### Reviewing and adjusting the nest

Once a BOM is loaded, the **Nested Parts List** tab shows one row per material group: its description, the stock it's nested on, how many pieces of stock were used, material utilization %, and how many parts (if any) didn't fit and need attention.

- **Stock** - a dropdown per group. Sheet groups offer standard sizes (48x96, 48x120, 60x120), **Smallest Drop** (the smallest sheet that actually holds the group's parts, capped at 48" wide - the widest stock this nests a drop onto automatically), or **QTY** (buy the parts already cut to size, no nesting). Stick groups offer standard lengths, Smallest Drop, a by-the-foot length ("FT"), or **QTY** (buy the parts already cut to length: nothing is nested, and the purchase line is the DET quantities, in each, marked "(CUT PARTS)"; Units still scales it).
- **Material** - a dropdown per group, choosing the cutting policy (Sheet/Plate, Expanded Metal, Wire Mesh, Foam) that governs rotation locks, edge allowance, and minimum part spacing.
- **Part Spacing / Sheet Spacing** (top of the **Nest 2D Layouts** tab) - the gap left between parts, and between a part and the sheet edge. Set per material group: pick a group in the **Showing** dropdown and the boxes edit that group alone; pick **All materials** to set every group at once (the boxes read blank when the groups differ). Every group starts at 0.25".
- **Units** (in the command strip) - rescales every part's quantity by a rack/unit count (e.g. building 35 of something quotes 35x the per-unit quantities). Commits on Enter or when the box loses focus, not per keystroke, since changing it re-nests every group.

Changing any of these re-nests the affected group(s) immediately; the Efficiency and Unnested columns update accordingly. An **Unnested** count above zero means the stock picked is too small to hold every part in that group.

### Notifications

Messages that do not need an answer - notes about the BOM, a spacing value that was replaced, "results written" - are collected under the **bell** at the bottom right of the window instead of popping up. The badge counts what you have not looked at yet and is coloured by the worst of it (blue info, amber warning, red error). Click the bell for the list, newest first; **Copy all** puts it on the clipboard and **Clear** empties it. The latest message is also shown in the status bar. Failures where work stopped (the nest failed, the BOM could not be read, a save failed) still show a dialog, and are listed here as well.

### Layout tabs

- **Kerf / Min cut length** (top of the **Nest 1D Layouts** tab) - per stick group, set the same way as spacing on the 2D tab: the boxes edit the group shown, or every group under **All materials**. Kerf is the material lost to each cut (default 0.125"). Min cut length is the length reserved at the end of each stick that cannot be used - the tube laser's clamp (default 4.5") - so it is subtracted from every stick; it is ignored for groups cut on the saw, and 0 is allowed.
- **Nest 1D Layouts** - a graphic of how sticks are cut, one material group at a time (pick which from the dropdown at the top, or **All materials**). Ctrl + mouse wheel zooms; hover a part or stick for its details. A group bought by **QTY** has no sticks to draw.
- **Nest 2D Layouts** - a graphic of how sheets are laid out, one material group at a time (pick which from the dropdown at the top). Mouse wheel zooms, middle-drag pans. Check this before exporting - it's the easiest way to catch a weird edge case in the nest.

### Tube End Features

The **Tube End Features** tab lists the stick parts of the loaded BOM (pick a material group, or **All materials**) with a dropdown for **End A** and **End B** on each:

- **Unreviewed** - nobody has looked. Kept out of the tube laser's clamp zone (the **Min cut length** at the end of each stick).
- **Clear** - reviewed, nothing within the clamp length of this end. The only end that may sit in the clamp zone; one clear end is enough for the part to use it.
- **Miter 45** - a 45 degree miter. Two mitered ends next to each other on a stick share one cut, and the tube's face width is credited back (square and round tube only - the width cannot be read from other descriptions). A miter is never in the clamp zone.
- **Miter 45 opposed** - a 45 degree miter that runs the opposite way to the miter on the other end, so the piece is a parallelogram rather than a taper ("45x45"). Set it on either end of a part whose other end is also mitered; on its own it is just a miter. It is recorded and saved, but nests the same as Miter 45 today.
- **Has features** - reviewed, and there is something (a hole, a slot) within the clamp length of this end. Nests exactly like Unreviewed, but says the part was looked at.

Changing an end re-nests that group straight away, so the effect on the stick count shows on the 1D tab. **Set every part shown** applies chosen ends to a whole group at once. The **Effect on nesting** column says in words what each combination does.

**Save** writes the choices to a separate **End Features** sheet in the BOM (one row per stick part, matched back by item number, description and length), so the printed BOM is unchanged. A sheet row that no longer matches the BOM (the part was edited since) is ignored, with a note in the notification log. Setting everything back to Unreviewed and saving removes the sheet.

### Saving and exporting

- **Save** - writes the current nest results into the bill of materials (the Purchase sheet: sheet/stick counts, hardware, finishes, costs). Unit costs typed into the Purchase sheet are kept when it is rebuilt. Saving back into the same file requires it to be closed first.
- **Nest to DXF** - writes a DXF per distinct sheet layout, for the shop to cut from.
- **SW Parts** - builds one plain, featureless SolidWorks part (`.SLDPRT`) per unique part number in the loaded BOM's parts table, for starting assemblies from. Needs SolidWorks on the computer running it (everyone else gets a message saying so) and your part templates, profile library and bend table set up in the settings file - see **SolidWorks export settings** below.
  - **Sheet parts** (a Length and a Width) are sheet metal: a Length x Width base flange at the thickness the description calls out. Gauges use the real gauge thickness for the material (steel, stainless and aluminum each have their own table), and bends use the shop bend table, falling back to a 0.42 K-factor if it doesn't cover the thickness.
  - **Stick parts** (a Length only) are a weldment member, from the shop profile library on an exact size, else the stock SolidWorks ANSI library when its wall is within 0.010" of the callout, else the cross-section sketched from the callout (tube corners at 2x/1x wall outside/inside, angle with a sharp heel, a thickness-radius root and each leg's inside tip rounded over at 0.0001" under the thickness).
  - **Material** comes from the description - "Alum"/"Aluminum", "SS"/"Stainless", "HDPE" or "UHMW" anywhere, otherwise hot-rolled steel - and picks the shop part template. HDPE and UHMW have no template of their own: they start from the steel one and take their material from your material library (`MaterialLibrary` in the settings file)), which Cutwright adds to SolidWorks' Material Databases folders for the length of the export and then removes again. Plastic is never gauged (a gauge callout on it is flagged), and plastic stick parts are always sketched, since the profile libraries are metal stock.
  - Files are named by part number, or `Item#<DET>` when there isn't one. Rows with the same part number are built once. Existing files are overwritten. Each part gets Description and LENGTH custom properties, plus WIDTH on sheet parts.
  - The first export on a computer reads the ANSI profile sizes out of SolidWorks (they are stored inside the profile files) and caches them in `%LOCALAPPDATA%\Cutwright`.

### The BOM workbook

A Cutwright bill of materials is an ordinary Excel workbook with no merged cells. Everything is found by name rather than by cell position, so you can insert rows, sort the table, add your own columns or move things around and Cutwright still reads it. Design notes: [`docs/bom-format-v2.md`](docs/bom-format-v2.md).

| Sheet | What it holds |
| --- | --- |
| **BOM** | The job block at the top (customer, job/est, units, and so on - each a named cell such as `Job_Units`) and the parts table `BomParts`: Item, Per unit, Units, Total, Description, Length, Width, Part number. A row with no length is a purchased item (hardware). |
| **Finishes** | One row per treatment - powder, wet paint, #4 polish, stencil, stickers - in the `FinishLines` table. Each row's **Basis** decides how the quantity is worked out: **Coverage** (area x (1 + waste) / coverage: powder in lb, paint in gal), **Per area** (area x (1 + waste): polishing, plating) or **Each** (stickers, stencils). Area is typed in per unit built. |
| **Purchase** | Written by Save: what to buy from the nest results, hardware, and a line per Finishes row that has something to buy (its quantity and cost follow the Finishes sheet), with total and per-unit cost. |
| **End Features** | The stick-end review above. |

Coverage rates and waste depend on your product and shop, so Cutwright has none built in. New workbooks start the Finishes coverage and waste from the optional settings below; left blank, the cell is highlighted for you to fill.

### Settings

Settings live in a small file, `%LOCALAPPDATA%\Cutwright\settings.json` (see below for how it is created).

| Setting | What it is |
| --- | --- |
| `PowderCoverageSqFtPerLb` | Optional. Square feet one lb of powder covers; starts the Powder coat row of a new workbook's Finishes sheet. |
| `WetPaintCoverageSqFtPerGal` | Optional. Square feet one gal of wet paint covers, for a wet paint row you add. |
| `FinishWastePercent` | Optional. Overspray and rework allowance in percent (`15` means 15%). |

### SolidWorks export settings

Where your SolidWorks templates and libraries live is machine-specific, so it is set in a small file rather than built in: `%LOCALAPPDATA%\Cutwright\settings.json`. The first time **SW Parts** is used without it, Cutwright creates a blank one and tells you where it is. Set what you need and restart Cutwright:

| Setting | What it is |
| --- | --- |
| `TemplateFolder` | **Required.** Folder holding `STEEL.prtdot`, `STAINLESS.prtdot` and `ALUMINUM.prtdot`. |
| `ShopProfileFolder` | Your own weldment profiles (one `.sldlfp` per size, named like the BOM: `2 x 2 x 11GA`, in a subfolder per tube type). Optional - without it stick parts use the ANSI profiles or a sketched section. |
| `BendTable` | Sheet metal bend allowance table (`.xls`). Optional - without it a 0.42 K-factor is used. |
| `MaterialLibrary` | A `.sldmat` library holding HDPE and UHMW. Optional - without it those parts keep the template's material. |
| `AnsiProfileFolder` | SolidWorks' own ANSI weldment profiles. Optional - found automatically in the standard install folders. |

```json
{
  "TemplateFolder": "D:\\SolidWorks\\Templates",
  "BendTable": "D:\\SolidWorks\\Templates\\bend allowance.xls"
}
```

