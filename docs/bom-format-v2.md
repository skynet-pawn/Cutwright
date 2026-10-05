# Cutwright BOM format v2 (design)

Status: implemented. Cutwright generates the template and reads only that.

## Why

The current bill of materials sheet is laid out in code as a fixed form: a title block at fixed cells,
19 merged ranges, hard-coded widths, a purchase section that is rebuilt below the parts on every save,
and a parser that reads fixed cell positions. Merged cells make it awkward to sort, filter, copy or
insert rows, and the origin of the form is uncertain. v2 is designed from scratch.

## Ground rules

1. **Old-layout files are not supported.** There is no upgrade path from them.
2. **New-format files are not readable by earlier versions.**
3. No merged cells anywhere. Real Excel tables. Values found by name, never by fixed cell address.

## Workbook layout

| Sheet | Contents |
| --- | --- |
| `BOM` | Job block at the top, then the parts table |
| `Finishes` | Finish, surface treatment, stencil and sticker lines, with calculated quantities |
| `Purchase` | What to buy, written from the nest results on Save (created by the first Save, not in a new template) |
| `End Features` | Per-end miter/clear/features data for stick parts (unchanged) |

### `BOM`

- Title in `A1` (plain cell, no merge).
- Job block, label / value pairs on rows 3 to 6, each value a named cell:
  `Job_Customer`, `Job_Est`, `Job_PrevEst`, `Job_Units`, `Job_By`, `Job_Drawing`, `Job_CheckedBy`,
  `Job_Description`, `Job_Date`, `Job_Revision`.
- Parts table `BomParts` from row 9:
  `Item | Per unit | Units | Total | Description | Length | Width | Part number`.
  `Units` and `Total` are formulas (`=Job_Units`, `=[@[Per unit]]*[@Units]`).
  Description is the whole material callout. A row with no Length and no Width is a purchased item
  (hardware), as today.
- The old `TUBE END` column is gone; the per-end review lives only on the `End Features` sheet.
- Print setup: landscape, fit to one page wide, header row repeated, frozen header.
- A workbook-level name `Cutwright_BomFormat` holds `2` so Cutwright can tell the format apart from the old one.

### `Finishes`

Table `FinishLines`, one row per treatment. This replaces the two hard-coded `Finish:` and `Stencil:`
rows of the old form.

| Column | Meaning |
| --- | --- |
| Item | Free text: "Powder coat", "Wet paint", "#4 polish", "Passivate", "Stencil", "Stickers" ... |
| Specification | Colour / gloss / grade / finish standard, e.g. "RAL 9005 semi-gloss", "#4 brushed" |
| Basis | Dropdown: `Coverage`, `Per area`, `Each` |
| Area per unit (sq ft) | Surface area to treat, per unit built (an input; see "Surface area") |
| Coverage (sq ft per unit of material) | Only for `Coverage`: how far one lb / gal goes |
| Waste % | Overspray, rework, setup |
| Each per unit | Only for `Each`: stickers, stencils, cans |
| Qty per unit | Formula, by basis (below) |
| Unit | Dropdown: lb, gal, sq ft, each, can |
| Total qty | `Qty per unit x Job_Units` |
| Unit cost | Input |
| Extended cost | `Total qty x Unit cost` |
| Notes | Free text |

Qty per unit:

- `Coverage`: `Area x (1 + Waste) / Coverage` (powder in lb, wet paint in gal).
- `Per area`: `Area x (1 + Waste)` (polishing, plating, anything priced by the square foot).
- `Each`: `Each per unit` (stickers, stencils, labels).

Coverage rates and waste are product- and shop-specific, so **no numbers are built in**. Cutwright fills the
starter rows (Powder coat, Stencil, Stickers) from `settings.json` (`PowderCoverageSqFtPerLb`,
`FinishWastePercent`; `WetPaintCoverageSqFtPerGal` is read for a wet paint row you add) and leaves the
cell blank, highlighted, when a shop has not set them.

### Surface area (decided: manual for now)

Area is typed in by the estimator. Not built: Cutwright could later pre-fill a starting estimate from the
parts: sheet and plate parts `2 x Width x Length`, stick parts `perimeter x Length` where the section
can be read from the callout. It says plainly that it is an estimate (edges, holes and cut-outs are
ignored). Later, a host program could supply true surface area from a model.

### `Purchase`

Table `PurchaseLines`: `Line | Qty | Unit | Description | Unit cost | Extended cost`, followed by
`Total cost` and `Per unit cost`. Rewritten on every Save from the nest results; unit costs typed in are
carried over to lines with the same description. A `Finishes` row with something to buy (its total
quantity above zero at save time) becomes a purchase line whose quantity and unit cost are live
formulas into the `Finishes` sheet, so changing a coverage or an area updates the purchase quantity.
Things bought whole (each, can) are rounded up.

## Reading

Cutwright reads only its own format. `Cutwright_BomFormat` present: read the job block by name and the tables by
name; the reader tolerates inserted rows, sorting, moved blocks and extra columns. Absent: Cutwright says the
file is not a Cutwright BOM and points to **New BOM Template**. There is no guessing at other layouts.

## Generation

- **New BOM Template** writes a blank v2 workbook (job block, empty `BomParts`, `Finishes` and
  `Purchase` tables, dropdowns, a short "How to fill this out" sheet) for the user to fill in.
- New BOMs from every source (SolidWorks export, CSV, PDF, Type In) are written straight into the v2
  layout, so a parsed file never needs hand-editing.
- A customer's own spreadsheet is handled by pasting its rows into the template's parts table. A
  column-mapping import dialog is a possible later addition, not part of the first version.

## Old format (decided: no upgrade path)

There is no Upgrade Old BOM and no legacy import. The old-layout reader and writer are removed from
the code.

## Open questions

Decided: the `Finishes` columns and three bases as above; area is manual; no Upgrade Old BOM; the
old-layout reader and writer are removed.

Known limit: ClosedXML cannot insert rows into a workbook holding the `Cutwright_BomFormat` constant
(Excel can). Cutwright never inserts rows itself, so this only affects code that edits a Cutwright workbook
that way.
