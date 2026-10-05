using System.IO;

namespace Cutwright
{
    // 2D sheet nesting for estimating. Deterministic and fast (sub-second on a full BOM) rather
    // than search-based: the deliverable is a defensible sheet count in seconds, and the same BOM
    // must always produce the same answer. The nest it produces is also what gets exported to DXF
    // and cut, so every placement has to be geometrically real, not just plausible.
    //
    // Almost every part in a sheet-metal BOM is its own bounding box. Most of the rest are right-
    // triangle gussets, which pair into a rectangle with no waste. What is left is genuinely
    // concave - brackets, channels, big-notch parts - and those get interleaved into each other by
    // search. So nesting reduces to rectangle packing plus a clustering pre-pass, under
    // per-material cutting rules.
    internal class SheetNestEngine
    {
        public double MaterialUtilizationPercent { get; private set; }
        public int SheetCount { get; private set; }
        public List<Sheet> Sheets { get; private set; } = new();
        public List<Part> UnnestedParts { get; private set; } = new();

        // Notes worth showing the estimator: DXFs that couldn't be read (so the part nested as a
        // rectangle from its BOM dimensions), parts too big for the selected sheet, and how much
        // pairing and interleaving actually did.
        public List<string> Warnings { get; } = new();

        // quickFeasibilityOnly runs the packer with a single ordering instead of PackBest's five,
        // for callers that only need a yes/no answer - a Smallest Drop search calls this dozens of
        // times over, and paying for all five orderings' worth of packing on every probe would
        // make that search too slow to be interactive. One ordering can occasionally call a size
        // infeasible that PackBest's best-of-five would have placed, which only ever costs the
        // search a little slack, never a wrong answer - the caller always re-nests its adopted
        // size for real, with every ordering in play, before showing it to anyone.
        public void Nest(List<Part> parts, float sheetWidth, float sheetLength, float partSpacing,
            float sheetEdgeMargin, MaterialPolicy policy, bool quickFeasibilityOnly = false)
        {
            Sheets = new List<Sheet>();
            UnnestedParts = new List<Part>();
            MaterialUtilizationPercent = 0;
            SheetCount = 0;
            Warnings.Clear();

            // Material kept clear at the sheet edge: sheared stock is squared up first, costing
            // one long and one short edge, and the Sheet Spacing setting reserves an edge inset
            // on top of that (whichever is larger wins per axis).
            double edgeWidth = Math.Max(policy.EdgeAllowanceWidth, sheetEdgeMargin);
            double edgeLength = Math.Max(policy.EdgeAllowanceLength, sheetEdgeMargin);
            double usableWidth = sheetWidth - edgeWidth;
            double usableLength = sheetLength - edgeLength;
            double spacing = Math.Max(partSpacing, policy.MinimumPartSpacing);

            var units = BuildUnits(parts, policy, spacing, usableWidth, usableLength);
            if (units.Count == 0)
                return;

            var packItems = units.Select(u => new PackItem
            {
                Width = u.FootprintWidth,
                Length = u.FootprintLength,
                AllowRotation = policy.AllowRotation,
                Tag = u
            });

            var packed = quickFeasibilityOnly
                ? SheetPacker.Pack(packItems, usableWidth, usableLength, spacing, policy.CutMethod)
                : SheetPacker.PackBest(packItems, usableWidth, usableLength, spacing, policy.CutMethod);

            var sheetsByIndex = new Dictionary<int, Sheet>();

            foreach (var placement in packed.Placements)
            {
                var unit = (NestUnit)placement.Tag!;

                if (!sheetsByIndex.TryGetValue(placement.SheetIndex, out var sheet))
                {
                    sheet = new Sheet(sheetWidth, sheetLength) { SheetID = placement.SheetIndex };
                    sheetsByIndex[placement.SheetIndex] = sheet;
                    Sheets.Add(sheet);
                }

                // The packer works in usable-area coordinates; shift back onto the real sheet so
                // the trimmed collar shows up in the drawing and the exported DXF.
                double originX = placement.X + edgeWidth;
                double originY = placement.Y + edgeLength;

                unit.EmitPlacements(sheet, originX, originY, placement.Rotated);
            }

            foreach (var item in packed.Unplaced)
            {
                var unit = (NestUnit)item.Tag!;
                foreach (var member in unit.Members)
                    UnnestedParts.Add(member.SourcePart);

                Warnings.Add($"Part {unit.Members[0].SourcePart.PartNumber} ({unit.FootprintWidth:0.###} x {unit.FootprintLength:0.###}) does not fit on a {sheetWidth:0.###} x {sheetLength:0.###} sheet.");
            }

            Sheets.Sort((a, b) => a.SheetID.CompareTo(b.SheetID));
            SheetCount = Sheets.Count;

            if (SheetCount > 0)
            {
                // True outline area, not bounding boxes - paired gussets share one rectangle and
                // interleaved parts share one cluster, so charging each of them its full box would
                // double-count the material.
                double partArea = Sheets.Sum(s => s.NestedParts.Sum(p => p.Geometry.PolygonArea));

                // Measured against purchased material, not usable area - what fraction of what
                // you buy leaves as parts.
                MaterialUtilizationPercent = partArea / (SheetCount * sheetWidth * sheetLength) * 100.0;
            }
        }

        // One physical part inside a unit: where its bounding box sits in the unit's own footprint,
        // and how far it is turned.
        private class NestMember
        {
            public Part SourcePart = null!;
            public PartGeometry Geometry = null!;
            public double DX;
            public double DY;
            public double Rotation;
        }

        // One thing to place: a single part, two paired gussets sharing a rectangle, or a cluster
        // of interleaved concave parts. The packer only ever sees the footprint.
        private class NestUnit
        {
            public List<NestMember> Members { get; } = new();
            public double FootprintWidth { get; set; }
            public double FootprintLength { get; set; }

            // Turns this unit's placement into one PlacedPart per physical part.
            //
            // Members carry their own offset and rotation rather than having it derived here, so
            // gusset pairs and interleaved clusters go through exactly the same path - only
            // whoever built the unit knows why a member sits where it does.
            public void EmitPlacements(Sheet sheet, double x, double y, bool rotated)
            {
                foreach (var member in Members)
                {
                    var (memberWidth, memberLength) = Extents(member.Geometry, member.Rotation);

                    // Turning the whole unit 90 degrees maps unit-local (u,v) to
                    // (footprintLength - v - height, u) and swaps each member's own box.
                    double placedX, placedY, rotation;

                    if (rotated)
                    {
                        placedX = x + (FootprintLength - member.DY - memberLength);
                        placedY = y + member.DX;
                        rotation = member.Rotation + 90.0;
                    }
                    else
                    {
                        placedX = x + member.DX;
                        placedY = y + member.DY;
                        rotation = member.Rotation;
                    }

                    sheet.NestedParts.Add(new PlacedPart
                    {
                        SourcePart = member.SourcePart,
                        Geometry = member.Geometry,
                        X = placedX,
                        Y = placedY,
                        RotationDegrees = rotation
                    });
                }
            }

            private static (double Width, double Length) Extents(PartGeometry geometry, double rotationDegrees) =>
                geometry.RotatedExtents(PartGeometry.QuarterTurns(rotationDegrees));
        }

        private List<NestUnit> BuildUnits(List<Part> parts, MaterialPolicy policy, double spacing,
            double usableWidth, double usableLength)
        {
            var geometryCache = new Dictionary<string, PartGeometry>();

            // Expand the BOM into individual instances up front, in BOM order. Everything after
            // this works off indices into that list, which is what keeps the nest reproducible -
            // no pass depends on the enumeration order of a dictionary.
            var instances = new List<(Part Part, PartGeometry Geometry)>();

            foreach (var part in parts)
            {
                if (part.quantity <= 0)
                    continue;

                var geometry = ResolveGeometry(part, geometryCache);

                for (int i = 0; i < part.quantity; i++)
                    instances.Add((part, geometry));
            }

            var units = new List<NestUnit>();
            var consumed = new bool[instances.Count];

            BuildInterleavedUnits(instances, consumed, units, policy, spacing, usableWidth, usableLength);
            BuildRemainingUnits(instances, consumed, units, policy);

            return units;
        }

        // Interleaving pass. Concave parts that fit into one another get grouped into clusters, so
        // a bracket costs the space it actually occupies rather than the box it happens to span.
        //
        // The geometry cache hands back one shared PartGeometry per distinct outline, so grouping
        // by reference groups by shape - and the search only runs once per shape however many of
        // them the BOM asks for.
        private void BuildInterleavedUnits(List<(Part Part, PartGeometry Geometry)> instances,
            bool[] consumed, List<NestUnit> units, MaterialPolicy policy, double spacing,
            double usableWidth, double usableLength)
        {
            if (!policy.AllowInterleaving)
                return;

            var shapeOrder = new List<PartGeometry>();
            var byShape = new Dictionary<PartGeometry, List<int>>();

            for (int i = 0; i < instances.Count; i++)
            {
                var geometry = instances[i].Geometry;
                if (!ClusterBuilder.IsCandidate(geometry))
                    continue;

                if (!byShape.TryGetValue(geometry, out var indices))
                {
                    indices = new List<int>();
                    byShape[geometry] = indices;
                    shapeOrder.Add(geometry);
                }

                indices.Add(i);
            }

            int interleavedParts = 0;
            int clusterCount = 0;
            double clusterArea = 0;
            double separateArea = 0;

            foreach (var geometry in shapeOrder)
            {
                var indices = byShape[geometry];
                if (indices.Count < 2)
                    continue;

                var template = ClusterBuilder.Build(geometry, spacing, usableWidth, usableLength,
                    policy.AllowRotation);

                if (template == null)
                    continue;

                double standalone = (geometry.Width + spacing) * (geometry.Length + spacing);
                int cursor = 0;

                // Full clusters first, then whatever is left as one shorter cluster. A prefix of a
                // growth sequence is itself a valid cluster, so the tail needs no second search.
                while (indices.Count - cursor >= 2)
                {
                    int take = Math.Min(template.Members.Count, indices.Count - cursor);
                    if (take < 2)
                        break;

                    var cluster = take == template.Members.Count
                        ? template
                        : ClusterBuilder.Prefix(template, geometry, take);

                    var unit = new NestUnit
                    {
                        FootprintWidth = cluster.FootprintWidth,
                        FootprintLength = cluster.FootprintLength
                    };

                    for (int m = 0; m < take; m++)
                    {
                        int index = indices[cursor + m];
                        consumed[index] = true;

                        unit.Members.Add(new NestMember
                        {
                            SourcePart = instances[index].Part,
                            Geometry = geometry,
                            DX = cluster.Members[m].DX,
                            DY = cluster.Members[m].DY,
                            Rotation = cluster.Members[m].QuarterTurns * 90.0
                        });
                    }

                    units.Add(unit);

                    cursor += take;
                    clusterCount++;
                    interleavedParts += take;
                    clusterArea += (cluster.FootprintWidth + spacing) * (cluster.FootprintLength + spacing);
                    separateArea += take * standalone;
                }
            }

            if (clusterCount > 0)
            {
                double saved = separateArea > 0 ? (1.0 - clusterArea / separateArea) * 100.0 : 0.0;
                Warnings.Add($"{interleavedParts} concave parts were interleaved into {clusterCount} cluster(s), taking {saved:0.#}% less room than nesting them separately.");
            }
        }

        // Everything the interleaving pass didn't take: gussets paired back-to-back, and single
        // parts on their own bounding box.
        private void BuildRemainingUnits(List<(Part Part, PartGeometry Geometry)> instances,
            bool[] consumed, List<NestUnit> units, MaterialPolicy policy)
        {
            // Right-triangle gussets waiting for a partner. A list rather than a dictionary: it
            // holds one entry per distinct gusset shape, so a linear scan costs nothing, and the
            // leftovers come out in a defined order at the end instead of whatever order a
            // dictionary happens to have after a run of removals.
            var pending = new List<(string Key, Part Part, PartGeometry Geometry)>();
            int pairedCount = 0;

            for (int i = 0; i < instances.Count; i++)
            {
                if (consumed[i])
                    continue;

                var (part, geometry) = instances[i];

                // Expanded metal is orientation-locked, so pairing (which relies on flipping one
                // triangle) isn't valid for it.
                if (geometry.Kind == PartShapeKind.RightTriangle && policy.AllowRotation &&
                    TryGetPairFootprint(geometry, out double pairW, out double pairL))
                {
                    // Only pair congruent gussets in matching orientation, so the flipped
                    // partner's right angle really does land on the opposite corner.
                    string key = $"{geometry.Width:F4}x{geometry.Length:F4}c{geometry.RightAngleCorner}";
                    int waiting = pending.FindIndex(p => p.Key == key);

                    if (waiting >= 0)
                    {
                        var partner = pending[waiting];
                        pending.RemoveAt(waiting);
                        pairedCount++;

                        var pair = new NestUnit
                        {
                            FootprintWidth = pairW,
                            FootprintLength = pairL
                        };

                        AddGussetMember(pair, partner.Part, partner.Geometry, false);
                        AddGussetMember(pair, part, geometry, true);
                        units.Add(pair);
                    }
                    else
                    {
                        pending.Add((key, part, geometry));
                    }

                    continue;
                }

                units.Add(SingleUnit(part, geometry));
            }

            // Odd triangles out nest alone, wasting their bounding box - unavoidable at odd counts.
            foreach (var leftover in pending)
                units.Add(SingleUnit(leftover.Part, leftover.Geometry));

            if (pairedCount > 0)
                Warnings.Add($"{pairedCount * 2} triangular parts were paired into {pairedCount} rectangles (gussets nest back-to-back).");
        }

        private static NestUnit SingleUnit(Part part, PartGeometry geometry)
        {
            var unit = new NestUnit
            {
                FootprintWidth = geometry.Width,
                FootprintLength = geometry.Length
            };

            unit.Members.Add(new NestMember { SourcePart = part, Geometry = geometry });
            return unit;
        }

        // A paired gusset rectangle holds two triangles, the second turned 180 degrees. Each is
        // anchored to the footprint corner its own right angle sits on - the second's right angle
        // lands on the diagonally opposite corner once flipped - so their hypotenuses meet along
        // the footprint diagonal. Anchoring by corner (rather than both sitting at the origin) is
        // what keeps a filleted gusset, whose own bounding box is smaller than the rectangle the
        // pair needs, from overlapping its partner.
        private static void AddGussetMember(NestUnit unit, Part part, PartGeometry geometry, bool second)
        {
            // Slack between the shape's own box and the shared rectangle.
            double slackX = unit.FootprintWidth - geometry.Width;
            double slackY = unit.FootprintLength - geometry.Length;

            // Flipping the shape moves its right angle to the opposite corner.
            int corner = (geometry.RightAngleCorner + (second ? 2 : 0)) % 4;

            unit.Members.Add(new NestMember
            {
                SourcePart = part,
                Geometry = geometry,

                // Corner 0 = (0,0), 1 = (W,0), 2 = (W,L), 3 = (0,L).
                DX = corner is 1 or 2 ? slackX : 0.0,
                DY = corner is 2 or 3 ? slackY : 0.0,
                Rotation = second ? 180.0 : 0.0
            });
        }

        // Works out the rectangle two of this gusset share. PartGeometry has already measured how
        // far the outline reaches past a corner-to-corner diagonal and sized the rectangle to
        // suit; the area check here is a last backstop against pairing two shapes that cannot
        // both fit, which would put them on top of each other.
        private static bool TryGetPairFootprint(PartGeometry geometry, out double width, out double length)
        {
            width = geometry.PairFootprintWidth;
            length = geometry.PairFootprintLength;

            return geometry.PolygonArea * 2.0 <= width * length * 1.0001;
        }

        // Uses the part's DXF outline when one is available - that's what distinguishes a gusset
        // from a rectangle of the same bounding box, tells the interleaver there's a pocket to
        // work with, and makes the drawing look real. Otherwise, and whenever a DXF can't be read,
        // falls back to the BOM's width x length, which is the correct answer for the great
        // majority of sheet metal parts anyway.
        private PartGeometry ResolveGeometry(Part part, Dictionary<string, PartGeometry> cache)
        {
            bool useDxf = part.hasDXF && !string.IsNullOrEmpty(part.DXFPath);
            string rectangleKey = $"RECT:{part.width:F6}x{part.length:F6}";

            if (useDxf)
            {
                string dxfKey = "DXF:" + Path.GetFullPath(part.DXFPath);
                if (cache.TryGetValue(dxfKey, out var cached))
                    return cached;

                try
                {
                    var geometry = PartGeometry.FromDxf(part.DXFPath);
                    cache[dxfKey] = geometry;
                    return geometry;
                }
                catch (Exception ex)
                {
                    Warnings.Add($"Part {part.PartNumber}: could not read DXF '{Path.GetFileName(part.DXFPath)}' ({ex.Message}); using its {part.width:0.###} x {part.length:0.###} BOM size instead.");
                }
            }

            if (!cache.TryGetValue(rectangleKey, out var rectangle))
            {
                rectangle = PartGeometry.FromRectangle(part.width, part.length);
                cache[rectangleKey] = rectangle;
            }

            return rectangle;
        }
    }
}
