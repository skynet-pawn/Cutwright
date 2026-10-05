using netDxf;
using netDxf.Entities;

namespace Cutwright
{
    class DXFWriter
    {
        public DXFWriter()
        {
            doc = new DxfDocument();

        }

        // Writes one sheet's nested layout to a DXF file: each placed part's true outer contour
        // and hole contours, rotated/translated to their nested position (rectangle-fallback parts
        // export as a plain rectangle, same as before).
        //
        // Parts only - no sheet outline. This file is imported into SigmaNest as the nest itself,
        // and a rectangle the size of the stock would just read as one more part to cut.
        // Placements are still in real sheet coordinates, so the edge collar is still there as
        // empty space around the parts.
        public void WriteDXF(Sheet sheet, string FileName)
        {
            doc = new DxfDocument();

            foreach (var part in sheet.NestedParts)
            {
                var (outer, holes) = part.ToWorld();

                doc.Entities.Add(new Polyline2D(ToVector2List(outer), true));

                foreach (var hole in holes)
                    doc.Entities.Add(new Polyline2D(ToVector2List(hole), true));
            }

            doc.Save(FileName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ? FileName : FileName + ".dxf");
        }

        private static List<Vector2> ToVector2List(List<(double X, double Y)> points) =>
            points.Select(p => new Vector2(p.X, p.Y)).ToList();

        DxfDocument doc;

    }
}
