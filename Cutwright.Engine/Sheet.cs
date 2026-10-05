namespace Cutwright
{
    internal class Sheet
    {
        public Sheet()
        {
            this.SheetWidth = 60.0f;
            this.SheetLength = 120.0f;

            this.NestedParts = new List<PlacedPart>();

        }

        public Sheet(float w, float l)
        {
            this.SheetWidth = w;
            this.SheetLength = l;
            this.NestedParts = new List<PlacedPart>();

        }



        public float SheetWidth { get; set; }
        public float SheetLength { get; set; }
        public float NestedArea { get; set; }
        public List<PlacedPart> NestedParts { get; set; }
        public int SheetID { get; set; }
    }
}
