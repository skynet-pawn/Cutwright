namespace Cutwright
{
    // One length of stock - tube, angle, bar - that parts get cut from.
    internal class Stick
    {
        public Stick(float length, float kerf, float miterCreditWidth = 0f)
        {
            this.StickLength = length;
            this.Kerf = kerf;
            this.MiterCreditWidth = miterCreditWidth;
            this.NestedParts = new List<Part>();
            this.RemainingLength = length;
        }

        // Material the saw turns into chips, one cut per part. Carried on the stick rather than
        // hardcoded here, so the setting on TNest is the only place it is defined.
        public float Kerf { get; }

        // Tube cross-sectional width credited back when a part's miter end shares a single cut
        // with whatever was placed just before it on this stick, instead of each end reserving its
        // own long-point length independently - the saving a tube laser actually gets from cutting
        // two facing miters at once. Zero when the width could not be resolved for this stock (see
        // TNest.ResolveMiterCreditWidth), in which case CanFit/AddPart behave exactly as before.
        public float MiterCreditWidth { get; }

        // Miter ends still open on the part most recently added - how many more times this stick's
        // trailing end can share a cut with whatever gets added next. Zero on a fresh stick, and
        // whenever the trailing part is square or has already used up its miter ends.
        public int OpenMiterTokens { get; private set; }

        public bool HasOpenMiterEnd => this.OpenMiterTokens > 0;

        // Whether a part of this length still fits, with the clamped end of the stick set aside.
        //
        // Inclusive on purpose: a part that exactly fills what is left does fit, and rejecting it
        // used to leave a length that was neither placeable nor rejectable.
        //
        // clampEligible lets a part with a reviewed, feature-free square end reach into that
        // reserved length - the clamp only stops the laser from machining a feature there, not
        // from making the plain parting cut that frees the piece. sharedCut, when true, checks
        // against the length after crediting back MiterCreditWidth for sharing a miter cut with
        // this stick's trailing part - see OpenMiterTokens.
        public bool CanFit(float length, float clampAllowance, bool clampEligible, bool sharedCut) =>
            EffectiveLength(length, sharedCut) <= this.RemainingLength - (clampEligible ? 0f : clampAllowance);

        public void AddPart(Part part, bool sharedCut)
        {
            this.RemainingLength -= EffectiveLength(part.length, sharedCut) + this.Kerf;
            this.NestedParts.Add(part);

            int miteredEnds = TubeEndStateText.MiteredEndCount(part.EndState);
            this.OpenMiterTokens = sharedCut ? miteredEnds - 1 : miteredEnds;
        }

        private float EffectiveLength(float length, bool sharedCut) =>
            sharedCut ? length - this.MiterCreditWidth : length;

        public List<Part> NestedParts { get; set; }

        public float StickLength { get; set; }

        public float RemainingLength;
    }
}
