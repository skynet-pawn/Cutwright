namespace Cutwright
{
    internal class Part
    {
        public Part()
        {
            this.line = 0;
            this.quantity = 0;
            this.JobQuantity = 0;
            this.Description = null;
            this.PartNumber = null;
            this.width = 0;
            this.length = 0;
            this.nested = false;
            this.surfacearea = this.length * this.width;
            this.unnested = false;
            this.PartUOM = UOM.EA;

        }

        public Part(int ln, int qty, string dsc, string partno, float width, float length, float sa)
        {
            this.line = ln;
            this.quantity = qty;
            this.Description = dsc;
            this.PartNumber = partno;
            this.width = width;
            this.length = length;
            this.nested = false;
            this.surfacearea = this.length * this.width;
            this.PartUOM = UOM.EA;
        }

        //Check for unnested parts
        public bool nested { get; set; }
        public bool unnested { get; set; }

        //Part BOM Stats
        public int line { get; set; }
        public int quantity { get; set; }
        public int JobQuantity { get; set; }

        // How many of this part one unit (rack) needs. quantity is this times whatever unit count
        // was quoted, and changing the unit count re-derives quantity from this rather than
        // rescaling quantity directly - a job that had its units changed twice must land on the
        // same numbers as one changed straight to the second value.
        public int PerUnitQuantity { get; set; }
        public string Description { get; set; }
        public string PartNumber { get; set; }

        //Part Geometry
        public float width { get; set; }
        public float length { get; set; }
        public float surfacearea { get; set; }

        // Tube parts only. Set by hand once someone has actually looked at the cut program - see
        // TubeEndState for what each value means and why. Unreviewed (the default) is the safe
        // assumption for anything nobody has looked at.
        //
        // Setting it also sets the two ends to the matching canonical pair, so a part read from the
        // old column M codes still shows something sensible on the End Features tab. The nesting
        // engine reads only this; EndA/EndB are how the tab edits it - see SetEnds.
        private TubeEndState _endState = TubeEndState.Unreviewed;
        public TubeEndState EndState
        {
            get => _endState;
            set
            {
                _endState = value;
                (EndA, EndB) = TubeEndStateText.ToEnds(value);
            }
        }

        // What is known about each end, finer than EndState (which cannot say which end, or tell
        // "has features" from "unreviewed"). Saved on the BOM's End Features sheet.
        public TubeEndFeature EndA { get; private set; }
        public TubeEndFeature EndB { get; private set; }

        // Sets both ends and brings EndState - what the nest reads - into line with them.
        public void SetEnds(TubeEndFeature a, TubeEndFeature b)
        {
            EndA = a;
            EndB = b;
            _endState = TubeEndStateText.FromEnds(a, b);
        }

        //Identify Location on Sheet with SheetID
        public int SheetID { get; set; }
        public float x { get; set; }
        public float y { get; set; }
        public double RotationAngle { get; set; }

        public UOM PartUOM { get; set; }

        //DXF

        public string DXFPath = "";
        public bool hasDXF = false;


    }

    public enum UOM { None = 0, EA = 1, FT = 2 }
}
