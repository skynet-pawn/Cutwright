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

        // clampAllowance is the clamped length at the end of the stick (0 when there is none). It
        // does not change what fits - CanFit already decided that - only which way round the part
        // goes: a part that reaches into the clamp zone has its clear square end there.
        public void AddPart(Part part, bool sharedCut, float clampAllowance = 0f)
        {
            bool intoClampZone = EffectiveLength(part.length, sharedCut) > this.RemainingLength - clampAllowance;

            this.RemainingLength -= EffectiveLength(part.length, sharedCut) + this.Kerf;
            StickPlacement placement = Place(part, sharedCut, intoClampZone);
            this.Placements.Add(placement);
            this.NestedParts.Add(part);

            int miteredEnds = TubeEndStateText.MiteredEndCount(part.EndState);
            this.OpenMiterTokens = placement.Trailing == MiterSlant.None ? 0
                : sharedCut ? miteredEnds - 1 : miteredEnds;
        }

        private float EffectiveLength(float length, bool sharedCut) =>
            sharedCut ? length - this.MiterCreditWidth : length;

        // Which way the part's miters run, decided here rather than by the drawing so the drawing
        // shows what the nest assumed. A shared cut is one diagonal, so the new part's leading
        // miter runs the opposite way to the trailing miter it shares. An unshared single miter
        // goes on the trailing end, which is the end OpenMiterTokens offers to the next part -
        // unless the part reaches into the clamp zone, which only a part with a clear square end
        // may do (TubeEndStateText.ClampZoneEligible): then the clear end is at the clamp and the
        // miter leads.
        private StickPlacement Place(Part part, bool sharedCut, bool intoClampZone)
        {
            int miteredEnds = TubeEndStateText.MiteredEndCount(part.EndState);
            bool opposed = part.EndState == TubeEndState.DoubleMiterOpposite;
            MiterSlant previous = this.Placements.Count > 0 ? this.Placements[^1].Trailing : MiterSlant.None;

            MiterSlant leading, trailing;

            if (sharedCut)
            {
                leading = previous == MiterSlant.LongBottom ? MiterSlant.LongTop : MiterSlant.LongBottom;
                trailing = miteredEnds < 2 ? MiterSlant.None : opposed ? Opposite(leading) : leading;
            }
            else if (miteredEnds == 2)
            {
                leading = MiterSlant.LongTop;
                trailing = opposed ? MiterSlant.LongBottom : MiterSlant.LongTop;
            }
            else if (miteredEnds == 1 && intoClampZone)
            {
                leading = previous == MiterSlant.LongBottom ? MiterSlant.LongTop : MiterSlant.LongBottom;
                trailing = MiterSlant.None;
            }
            else
            {
                leading = MiterSlant.None;
                trailing = miteredEnds == 1 ? MiterSlant.LongTop : MiterSlant.None;
            }

            return new StickPlacement(sharedCut, leading, trailing);
        }

        private static MiterSlant Opposite(MiterSlant slant) => slant switch
        {
            MiterSlant.LongTop => MiterSlant.LongBottom,
            MiterSlant.LongBottom => MiterSlant.LongTop,
            _ => MiterSlant.None,
        };

        public List<Part> NestedParts { get; set; }

        // One per part in NestedParts, same order, filled by AddPart. A stick filled some other
        // way (tests building one by hand) has none; read through PlacementAt.
        public List<StickPlacement> Placements { get; } = new();

        // Square and unshared when nothing was recorded.
        public StickPlacement PlacementAt(int index) =>
            index < this.Placements.Count ? this.Placements[index] : default;

        public float StickLength { get; set; }

        public float RemainingLength;
    }

    // Which edge of the bar, as drawn side-on, runs out to the long point at a mitered end. The
    // other edge is shorter by the tube's width, so the end is a diagonal.
    internal enum MiterSlant
    {
        None = 0,
        LongTop,
        LongBottom,
    }

    // How one part sits on its stick: whether its leading miter shares the cut with the part
    // before it (and so overlaps it by the miter credit), and which way each end runs.
    internal readonly record struct StickPlacement(bool SharedCut, MiterSlant Leading, MiterSlant Trailing);
}
