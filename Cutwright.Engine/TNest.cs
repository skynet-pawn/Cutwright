namespace Cutwright
{
    // 1D nesting for linear stock: how many sticks of tube, angle or bar a cut list needs.
    //
    // First-Fit-Decreasing. Every piece is listed individually, longest first, and each goes into
    // the first stick with room for it. Taking the longest first is what earns the standard bound
    // on bin packing - placing one of each BOM line per pass instead forfeits it, because bin
    // packing is order sensitive - and checking every open stick rather than only the newest is
    // what lets a short piece drop back into an earlier offcut.
    //
    // Deterministic, and linear in the number of pieces times the number of sticks, so a full cut
    // list nests in no time at all.
    internal class TNest
    {
        // What a group starts with, and what a blank box in the window means. Named so the window and
        // the constructor cannot disagree about it.
        public const float DefaultKerf = 0.125f;
        public const float DefaultMinClampLength = 4.5f;

        public TNest()
        {
            this.Parts = new List<Part>();
            this.PartList = new List<Part>();
            this.NestedList = new List<Part>();
            this.UnnestedList = new List<Part>();

            this.Sticks = new List<Stick>();

            this.MinClampLength = DefaultMinClampLength;
            this.Kerf = DefaultKerf;
            this.StickLength = 240.0f;
        }

        public void Nest()
        {
            // Nesting parts onto sticks is what buying sticks means, so this is also the way back
            // from a by-the-piece group - the same rule PNest.Nest follows.
            this.Purchase = StickPurchase.Sticks;

            this.PartList.Clear();
            this.NestedList.Clear();
            this.UnnestedList.Clear();
            this.Sticks.Clear();

            if (this.Parts.Count == 0)
            {
                this.StickCount = 0;
                this.Efficiency = 0.0f;
                return;
            }

            this.Description = this.Parts.First().Description;

            if (this.SizeToSmallestDrop)
                this.StickLength = SmallestDropLength();

            // The saw has no clamp zone to protect - unlike the tube laser, it isn't holding the
            // stock in a fixture that a feature near the cut end could collide with, so a
            // saw-cut group never needs this clearance regardless of what any part's own
            // EndState says. See CutOnSaw.
            float clampAllowance = this.CutOnSaw ? 0f : this.MinClampLength;

            // The longest piece a stick can actually yield, once the clamped end is set aside.
            // Only binds parts that still need that clearance - see ClampZoneEligible below.
            float usableLength = this.StickLength - clampAllowance;

            // Tube cross-sectional width, credited back when two miter-needing ends share a single
            // cut instead of each independently reserving its own long-point length - see
            // ResolveMiterCreditWidth.
            float miterCreditWidth = ResolveMiterCreditWidth();

            // One entry per physical piece. The BOM's own quantities are left alone - FileWriter
            // reads them back for by-the-foot pricing - so this works on copies throughout.
            foreach (var part in this.Parts)
            {
                for (int i = 0; i < part.quantity; i++)
                {
                    this.PartList.Add(new Part(part.line, 1, part.Description, part.PartNumber,
                        part.width, part.length, part.surfacearea) { EndState = part.EndState });
                }
            }

            this.PartList = this.PartList
                .OrderByDescending(x => x.length)
                .ThenBy(x => x.line)
                .ToList();

            foreach (var part in this.PartList)
            {
                bool clampEligible = this.CutOnSaw || TubeEndStateText.ClampZoneEligible(part.EndState);

                // Longer than what a stick can actually yield, so there is nothing to be done with
                // it. A clamp-zone-eligible part gets the whole stick length to work with, since it
                // needs no clamp clearance of its own; anything else is capped at usableLength. It
                // must not cost a stick either - it is reported as unnested and the estimator deals
                // with it, rather than quietly adding stock that would hold nothing.
                float partLimit = clampEligible ? this.StickLength : usableLength;
                if (part.length > partLimit)
                {
                    this.UnnestedList.Add(part);
                    continue;
                }

                int miteredEnds = TubeEndStateText.MiteredEndCount(part.EndState);
                Stick target = null;
                bool sharedCut = false;

                // Prefer a stick whose trailing part left a miter end open to share with - one
                // diagonal cut then serves as both parts' end miter, the same saving a tube laser
                // gets from nesting two facing miters together, instead of each part reserving its
                // own long-point length as if it were cut alone.
                if (miteredEnds > 0 && miterCreditWidth > 0f)
                {
                    foreach (var stick in this.Sticks)
                    {
                        if (stick.HasOpenMiterEnd &&
                            stick.CanFit(part.length, clampAllowance, clampEligible, true))
                        {
                            target = stick;
                            sharedCut = true;
                            break;
                        }
                    }
                }

                if (target == null)
                {
                    foreach (var stick in this.Sticks)
                    {
                        if (stick.CanFit(part.length, clampAllowance, clampEligible, false))
                        {
                            target = stick;
                            break;
                        }
                    }
                }

                if (target == null)
                {
                    target = new Stick(this.StickLength, this.Kerf, miterCreditWidth);
                    this.Sticks.Add(target);
                }

                target.AddPart(part, sharedCut, clampAllowance);
                this.NestedList.Add(part);
            }

            this.StickCount = this.Sticks.Count;
            this.Efficiency = this.CalculateEfficiency();
        }

        // The tube's own cross-sectional width, read back out of the group's description (e.g.
        // "SQ Tube 2 x 2 x 11GA HR") the same way BomLineOrder already does for sorting. Resolved
        // only for square and round tube, where the face dimension is unambiguous from the
        // description alone - for rectangular tube, angle or bar, which face a miter runs across
        // cannot be told from the BOM, so those still get the clamp-zone rule above but never the
        // length credit, and CanFit/AddPart fall back to their un-credited behavior.
        private float ResolveMiterCreditWidth()
        {
            MaterialSpec? spec = CalloutTranslator.Read(this.Description ?? string.Empty, null);
            if (spec == null || spec.Section.Count == 0)
                return 0f;

            return spec.Form switch
            {
                StockForm.SquareTube => spec.Section[0],
                StockForm.RoundTube => spec.Section[0],
                _ => 0f,
            };
        }

        // The width a miter is drawn across in the 1D layout. The credited width where there is
        // one, so a shared cut lines up with what was charged; otherwise the largest section size
        // in the description, which is only a picture - which face the miter crosses is not known
        // (see ResolveMiterCreditWidth). Zero when the description gives no size.
        public float MiterDrawWidth()
        {
            float credit = ResolveMiterCreditWidth();
            if (credit > 0f)
                return credit;

            MaterialSpec? spec = CalloutTranslator.Read(this.Description ?? string.Empty, null);
            return spec == null || spec.Section.Count == 0 ? 0f : spec.Section.Max();
        }

        // The shortest single stick that holds every instance of this group's parts, reserving
        // the same clamp allowance and per-part kerf a full-length stick does.
        //
        // A plain sum rather than a trial pack: every part ends up on the one stick regardless of
        // cut order, so the total length it takes is order-independent - unlike the 2D case, there
        // is no arrangement to search for.
        private float SmallestDropLength() =>
            (this.CutOnSaw ? 0f : this.MinClampLength) +
            this.Parts.Sum(p => p.quantity * (p.length + this.Kerf));

        // Cut length delivered as parts, against the stock bought to get it.
        //
        // Kerf is deliberately not counted as used: it leaves as chips, not as part, and counting
        // it flatters the number. This is the same measure the sheet nester reports, so the two
        // views of a job can be read side by side.
        public float CalculateEfficiency()
        {
            if (this.Sticks.Count == 0 || this.StickLength <= 0)
                return 0.0f;

            float partLength = 0.0f;

            foreach (var stick in this.Sticks)
            {
                foreach (var part in stick.NestedParts)
                    partLength += part.length;
            }

            return partLength / (this.Sticks.Count * this.StickLength) * 100.0f;
        }

        // Switches the group to buying the parts themselves, each, already cut to length by the
        // supplier: nothing is nested, so nothing is left of the previous nest to disagree with the
        // count. The stick list, the unnested list and the utilisation are cleared together for the
        // same reason PNest.BuyByThePiece clears its own - a stale drawing or percentage reads as
        // one that was measured.
        public void BuyByThePiece()
        {
            this.Purchase = StickPurchase.Pieces;

            this.PartList.Clear();
            this.NestedList.Clear();
            this.UnnestedList.Clear();
            this.Sticks.Clear();
            this.StickCount = 0;
            this.Efficiency = 0.0f;
        }

        // How this group is bought. Not Part.PartUOM, which is by-the-stick versus by-the-foot: a
        // by-the-piece group is neither, and EA there already means one whole stick.
        public StickPurchase Purchase { get; set; } = StickPurchase.Sticks;

        // The number that belongs in the purchase quantity cell, in whatever this group is bought
        // by. Derived, like PNest.PurchaseQuantity, so the count cannot disagree with its unit.
        // Pieces is the parts the BOM asks for, straight from the DET quantities (scaled by Units).
        public int PurchaseQuantity => this.Purchase == StickPurchase.Pieces
            ? this.Parts.Sum(p => p.quantity)
            : this.StickCount;

        public string Description { get; set; }
        public int StickCount { get; set; }
        public float Kerf { get; set; }
        public float MinClampLength { get; set; }
        public float StickLength { get; set; }
        public float Efficiency { get; set; }

        // When set, Nest() first shrinks StickLength to the shortest single stick this group's
        // own parts need, rather than cutting from whatever length was set before. Sticky across
        // re-nests for the same reason as PNest.SizeToSmallestDrop: a later Units change must
        // still land on a drop sized for the new quantities, not the size picked before it.
        public bool SizeToSmallestDrop { get; set; }

        // Which machine this group's parts are cut on. The tube laser needs MinClampLength kept
        // clear so its clamp has plain stock to grip without landing on a feature; the saw just
        // cuts square through whatever is in the blade, so a saw-cut group ignores the clamp
        // allowance entirely, for every part regardless of its own EndState. Sticky across
        // re-nests the same way SizeToSmallestDrop is - a mode on the group, picked once and kept
        // until someone changes it, not recomputed from the parts each time.
        public bool CutOnSaw { get; set; }

        public List<Part> Parts { get; set; }
        public List<Part> PartList { get; set; }
        public List<Part> NestedList { get; set; }
        public List<Part> UnnestedList { get; set; }
        public List<Stick> Sticks { get; set; }

        public string StatusString { get; set; }
    }

    // How a linear-stock group is bought. As with SheetPurchase, the distinction has to survive to
    // the export, because a BOM row shows only the number.
    internal enum StickPurchase
    {
        // Nest the parts and buy sticks of StickLength to cut them from.
        Sticks = 0,

        // Buy the parts themselves, each, already cut to length. Nothing is nested.
        Pieces = 1
    }
}
