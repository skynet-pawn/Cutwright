namespace Cutwright
{
    // One physical part instance placed on a sheet by SheetNestEngine: which BOM row it came
    // from, its true local-space geometry, and the position/rotation it was nested at.
    internal class PlacedPart
    {
        public Part SourcePart { get; set; } = null!;
        public PartGeometry Geometry { get; set; } = null!;
        public double X { get; set; }
        public double Y { get; set; }
        public double RotationDegrees { get; set; }

        public (List<(double X, double Y)> Outer, List<List<(double X, double Y)>> Holes) ToWorld() =>
            Geometry.ToWorld(X, Y, RotationDegrees);
    }
}
