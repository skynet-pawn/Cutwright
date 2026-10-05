using System.IO;
using ClosedXML.Excel;
using Cutwright;

namespace Cutwright.Tests
{
    // Builds a Cutwright bill of materials file for a test to read back or write results into, through
    // the same writer the program uses - so a test never depends on where a cell happens to sit.
    internal static class TestBom
    {
        // A part as BillOfMaterials takes it: quantity is the per-unit figure, the file scales it by
        // the job's units.
        public static Part Line(int item, int perUnit, string description, string partNumber, float length, float width = 0f) =>
            new Part(item, perUnit, description, partNumber, width, length, 0f) { PerUnitQuantity = perUnit };

        public static string Write(string path, int units, params Part[] parts)
        {
            var bom = new BillOfMaterials { Units = units };
            bom.parts.AddRange(parts);

            bom.WriteToFile(path);
            if (bom.Errors.Count > 0)
                throw new IOException(string.Join("; ", bom.Errors));

            return bom.Filename;
        }

        // Puts a value into one of the job block's named cells of an existing file.
        public static void SetJobCell(string path, string name, object? value)
        {
            using var workbook = new XLWorkbook(path);
            IXLCell cell = BomWorkbook.NamedCell(workbook, name)!;

            if (value is null)
                cell.Clear(XLClearOptions.Contents);
            else
                cell.Value = XLCellValue.FromObject(value);

            workbook.Save();
        }
    }
}
