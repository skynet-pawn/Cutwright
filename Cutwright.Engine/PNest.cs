using System.Globalization;

namespace Cutwright
{
    internal class PNest
    {
        public PNest()
        {
            this.Sheets = new List<Sheet>();
            this.Parts = new List<Part>();
            this.PartList = new List<Part>();
            this.UnnestedList = new List<Part>();
        }

        public PNest(float SheetWidth, float SheetLength, float SheetSpacing, float PartSpacing)
        {
            this.SheetWidth = SheetWidth;
            this.SheetLength = SheetLength;
            this.SheetSpacing = SheetSpacing;
            this.PartSpacing = PartSpacing;

            this.Sheets = new List<Sheet>();
            this.Parts = new List<Part>();
            this.PartList = new List<Part>();
            this.UnnestedList = new List<Part>();

            this.Description = "";
            this.StatusString = "";



        }

        // Nests this group's parts onto sheets. Deterministic and fast - the same BOM and settings
        // always produce the same sheet count, which is what makes the number quotable.
        public void Nest()
        {
            var engine = this.SizeToSmallestDrop ? NestAsSmallestDrop() : NestAtFixedSize();

            // Nesting parts onto sheets is what buying sheets means, so this is also the way back
            // from a by-the-piece group: the nest, the count, the utilisation and the purchase
            // unit are all set together and cannot end up describing different things.
            this.Purchase = SheetPurchase.Sheets;

            this.Sheets = engine.Sheets;
            this.UnnestedList = engine.UnnestedParts;
            this.SheetCount = engine.SheetCount;
            this.Efficiency = engine.MaterialUtilizationPercent.ToString("F1", CultureInfo.InvariantCulture);
            this.UnnestedPartCount = engine.UnnestedParts.Count;
            this.Warnings = engine.Warnings;
        }

        private SheetNestEngine NestAtFixedSize()
        {
            var engine = new SheetNestEngine();
            engine.Nest(this.Parts, this.SheetWidth, this.SheetLength, this.PartSpacing, this.SheetSpacing, this.Policy);
            return engine;
        }

        // How many times each binary search below halves its bracket. 14 halvings brings even a
        // bracket hundreds of inches wide down to a hundredth of an inch - far finer than the
        // question "does this fit" needs - while staying a fixed, deterministic, and (see
        // quickFeasibilityOnly below) cheap amount of work: three searches at 14 probes apiece is
        // the difference between a re-nest that runs while the estimator keeps working and one
        // that makes them wait.
        private const int DropSearchIterations = 14;

        // The widest sheet this shop ever nests a Smallest Drop onto - a 60in-wide sheet exists as
        // a stock size, but only as a deliberate pick for a job it happens to suit well, never
        // something an automatic "smallest possible" search should reach for on its own.
        private const double MaxSmallestDropWidth = 48.0;

        // Sizes this group's own drop to the smallest near-square sheet its parts fit on, rather
        // than adopting whatever footprint a single unconstrained packing pass happened to use.
        // An unconstrained pass has nothing telling it to fill a sheet's width before running its
        // length out, so it can turn a job that would nest comfortably on a near-square sheet into
        // one long, mostly-empty strip - correct, but not a buyable size.
        //
        // Finds it the same way the estimator would by hand: shrink a square down until the parts
        // stop fitting on it, then, since a real BOM is not always square-shaped, let one axis
        // shrink further on its own - which is what recovers a fair rectangle instead of a bloated
        // square when one long part is what set the square's size in the first place.
        //
        // Every candidate size is a real, complete Nest() at that exact size, not a size adopted
        // and packed into afterward - so unlike a shrink-after-the-fact footprint, the answer this
        // returns is always the size it was actually proven to hold everything on.
        private SheetNestEngine NestAsSmallestDrop()
        {
            var engine = new SheetNestEngine();

            int totalParts = this.Parts.Sum(p => p.quantity);
            if (totalParts <= 0)
            {
                engine.Nest(this.Parts, this.SheetWidth, this.SheetLength, this.PartSpacing, this.SheetSpacing, this.Policy);
                return engine;
            }

            bool Fits(double width, double length)
            {
                engine.Nest(this.Parts, (float)width, (float)length, this.PartSpacing, this.SheetSpacing, this.Policy);
                return engine.SheetCount == 1 && engine.UnnestedParts.Count == 0;
            }

            // The search below only ever needs a yes/no answer, dozens of times over - so it packs
            // with one ordering instead of PackBest's five (see quickFeasibilityOnly on Nest). The
            // size this converges on is always re-nested for real by the final Fits call below
            // before it is adopted, so the one-ordering answer only ever costs the search a little
            // slack, never a wrong final result.
            bool QuickFits(double width, double length)
            {
                engine.Nest(this.Parts, (float)width, (float)length, this.PartSpacing, this.SheetSpacing, this.Policy,
                    quickFeasibilityOnly: true);
                return engine.SheetCount == 1 && engine.UnnestedParts.Count == 0;
            }

            // Smaller sizes are assumed to hold less than larger ones, so each search below can
            // bisect rather than hunt - the packer is not guaranteed perfectly monotonic, but close
            // enough that any rare exception just costs a little slack, never a dropped part: the
            // final size is always re-proven with one last Fits call before it is adopted.
            double MinimalFeasible(double feasibleHi, Func<double, bool> fits)
            {
                double lo = 0.0;
                double hi = feasibleHi;

                for (int i = 0; i < DropSearchIterations; i++)
                {
                    double mid = (lo + hi) / 2.0;

                    if (fits(mid))
                        hi = mid;
                    else
                        lo = mid;
                }

                return hi;
            }

            // Lining every part up in a single row uses at most this much of either axis, however
            // this material's own shapes end up arranged - generous enough that a square this big
            // is guaranteed to hold everything, which is what every search below bisects down from.
            double bound = 1.0;
            foreach (var part in this.Parts)
                bound += part.quantity * (double)(part.width + part.length);

            // Should not happen given the bound above; report whatever this found rather than
            // adopt a footprint that does not actually hold everything.
            if (!Fits(bound, bound))
                return engine;

            // Shrink the guaranteed-good square down to the smallest square that still holds every
            // part - "48 x 96" brought down toward "48 x 48" and then smaller still, rather than
            // stopping at the first size that happens to fit.
            double square = MinimalFeasible(bound, side => QuickFits(side, side));

            // With the tightest square in hand, let width and then length shrink further on their
            // own. A job that is naturally square-ish barely moves from here; a job dominated by
            // one long part - the one genuinely bound to be rectangular - gets its other axis
            // trimmed back down instead of staying bloated out to match the square the long part
            // forced. Width first, then length against that narrower width, so a second axis is
            // never trimmed against a wider-than-necessary first one.
            //
            // Width's own search never climbs past 48in - the widest drop this shop nests one
            // onto automatically, a 60in sheet being a deliberate pick for a specific job rather
            // than something this search should reach for. A job whose true shape does not want
            // anywhere near that much width - a single long, narrow part is the case that matters -
            // is unaffected: only the ceiling on the search moved, not the shape the parts
            // actually need, so it still comes back narrow. A job that does want the width
            // saturates the ceiling instead of climbing past it, and length is searched against
            // the full proven-good bound rather than square, so it is free to grow past square to
            // make up for whatever width could not.
            double width = MinimalFeasible(Math.Min(square, MaxSmallestDropWidth), w => QuickFits(w, square));
            double length = MinimalFeasible(bound, l => QuickFits(width, l));

            // Leaves the engine holding the actual nest at (width, length) - the size about to be
            // adopted - rather than whatever the last binary search probe left behind.
            Fits(width, length);

            this.SheetWidth = (float)width;
            this.SheetLength = (float)length;
            return engine;
        }

        // Buy the parts by the piece rather than nesting them: some materials - dunnage is the
        // everyday case - arrive from the supplier already cut to size, so no sheet is consumed
        // and there is nothing to lay out.
        //
        // Clearing the nest is part of the meaning, not tidying up. The removed "QTY" option left
        // the previous nest in place, so the drawing went on showing sheets the group was no
        // longer buying while the count claimed something else. Utilisation is blanked for the
        // same reason: there is no sheet to use well or badly, and a stale percentage reads as
        // one that was measured.
        public void BuyByThePiece()
        {
            this.Purchase = SheetPurchase.Pieces;

            this.Sheets = new List<Sheet>();
            this.UnnestedList = new List<Part>();
            this.UnnestedPartCount = 0;
            this.SheetCount = 0;
            this.Efficiency = "";
            this.Warnings = new List<string>();
        }




        public List<Sheet> Sheets { get; set; }
        public List<Part> Parts { get; set; }
        public List<Part> PartList { get; set; }
        public List<Part> UnnestedList { get; set; }

        public float SheetWidth { get; set; }
        public float SheetLength { get; set; }

        // When set, Nest() first shrinks SheetWidth/SheetLength to the smallest single-sheet
        // footprint this group's own parts need, rather than nesting at whatever size was set
        // before. Sticky across re-nests - a later Units change or material change must still
        // land on a drop, not silently revert to the size this was before the estimator picked
        // "Smallest Drop" - so it is a mode on the group, not a one-off action.
        public bool SizeToSmallestDrop { get; set; }

        // Material kept clear at the sheet edge. Sheared stock already loses one long and one
        // short edge to squaring up (see MaterialPolicy); this applies on top of that, and the
        // larger of the two wins per axis.
        public float SheetSpacing { get; set; }

        public float PartSpacing { get; set; }

        // How this material cuts, and therefore how it nests. Defaulted from the BOM description
        // when the group is created; overridable per group in the UI, since descriptions are free
        // text and a wrong guess silently changes the rules.
        public MaterialPolicy Policy { get; set; } = MaterialPolicy.SheetMetal;

        // What this group buys, and therefore what its purchase quantity counts.
        //
        // An explicit property rather than Part.PartUOM, which is what the linear side reads: both
        // Part constructors default PartUOM to UOM.EA, and for sheets "EA" already means one whole
        // sheet - so a sheet export branching on it would read every group as by-the-piece. The
        // linear side gets away with it because there EA means one whole stick, which is its
        // default behaviour anyway.
        public SheetPurchase Purchase { get; set; } = SheetPurchase.Sheets;

        // The number that belongs in the bill of materials' purchase quantity cell, in whatever
        // this group is bought by.
        //
        // Derived rather than assigned so the count cannot disagree with the unit it is quoted in.
        // The old "QTY" option assigned a piece count into SheetCount, which the export wrote
        // under a "(Length x Width)" description - so it was quoted as that many sheets of that
        // size. It also used Parts.Count, the number of BOM lines, rather than the parts those
        // lines call for.
        public int PurchaseQuantity => this.Purchase == SheetPurchase.Pieces
            ? this.Parts.Sum(p => p.quantity)
            : this.SheetCount;

        public string Efficiency { get; set; } = "";
        public int SheetCount { get; set; }
        public int UnnestedPartCount { get; set; }
        public List<string> Warnings { get; set; } = new();
        public string? Description { get; set; }

        public string StatusString { get; set; } = "";

    }

    // How a sheet group is bought. The distinction has to survive as far as the export, because
    // the two quantities are counts of different things and a BOM row shows only the number.
    internal enum SheetPurchase
    {
        // Nest the parts and buy sheets of SheetWidth x SheetLength to cut them from.
        Sheets = 0,

        // Buy the parts themselves, each, already cut to size by the supplier. Nothing is nested.
        Pieces = 1
    }
}
