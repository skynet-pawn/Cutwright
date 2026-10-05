using System.Text.RegularExpressions;

namespace Cutwright
{
    // Turns a customer's material description into an stock callout.
    //
    // The point of this is consistency, not cleverness. A customer writes "HSS 8" X 3" X .125 X
    // 47 7/8"lg" or "TOP PLATE A 3/4 x 2 x 3-1/8" with a grade in a column of its own, and the same
    // material has to come out as one string every time or it nests as two different materials and
    // is bought twice.
    //
    // What it cannot recognise it hands back unchanged, so the estimator corrects it on the sheet.
    // That is deliberate: catching most of it and admitting the rest is worth more than a guess that
    // reads like a real callout.
    internal static class CalloutTranslator
    {
        // Purchased hardware. Nothing here is stock to be cut, and a thread reads far too much like
        // a dimension to risk it.
        private static readonly Regex Hardware = new(
            @"\b(HHCS|HCS|H\.H\.C\.S|F\.S\.H\.C\.S|NUT|WASHER|RIVET|BOLT|SCREW|NYLOCK|UNC|UNF|COTTER|PIN|LATCH|SHOCK|CLIP|STRAP|PLUG|CAP|GRADE\s*\d|Gr\.\s*\d)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Thread = new(
            @"\d+\s*/\s*\d+\s*-\s*\d+(?!\s*/)", RegexOptions.Compiled);

        // An AISC designation - C3 x 4.1, W6 x 15, S3 x 5.7. A letter, a depth, then a weight per
        // foot, which is a different naming convention from everything else and passes straight
        // through.
        private static readonly Regex Aisc = new(
            @"(?<![A-Za-z0-9])([CWSM]\d+(?:\.\d+)?)\s*[xX×]\s*(\d+(?:\.\d+)?)\b", RegexOptions.Compiled);

        private static readonly Regex Parenthetical = new(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex GaugeToken = new(@"(\d+)\s*(?:ga\b|gauge\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Expanded metal names its mesh - "3/4 #9" - rather than a thickness.
        private static readonly Regex Mesh = new(
            @"(\d+(?:[- ]\d+/\d+)?|\d+/\d+)\s*(?:x\s*)?#\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string Number = @"(?:\d+[- ]\d+/\d+|\d+/\d+|\d*\.\d+|\d+)";

        // The thickest flat product the shop buys. Past this the numbers were not a sheet or plate.
        private const float MaxFlatThickness = 1.0f;

        private static readonly Regex Group = new(
            @"(?<![A-Za-z0-9.])" + Number + @"(?:\s*[xX×]\s*" + Number + @")+",
            RegexOptions.Compiled);

        private static readonly Regex SingleNumber = new(Number, RegexOptions.Compiled);

        private static readonly Regex ScheduleToken = new(
            @"SCH(?:EDULE)?\.?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Reads StockCallout.Format's own Round Tube shape back - see the RoundTube branch of
        // Read() for why the generic Numbers() reader cannot.
        private static readonly Regex RoundTubeCanonical = new(
            @"(" + Number + @")\s*OD\s*[xX×]\s*(" + Number + @")\s*wall",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Returns the stock callout, or the description unchanged when it cannot be read.
        public static string Translate(string? description, string? material = null)
        {
            string text = (description ?? string.Empty).Trim();

            if (text.Length == 0)
                return text;

            MaterialSpec? spec = Read(text, material);

            return spec is null ? text : StockCallout.Format(spec);
        }

        // True when the description was recognised, which is what tells a caller whether the result
        // is an stock callout or the customer's own words.
        public static bool CanTranslate(string? description, string? material = null) =>
            Read((description ?? string.Empty).Trim(), material) is not null;

        // Named dimensions read from an estimator's own column-role mapping, one row at a time - a
        // drawing whose BOM table keeps Thickness/Width/Height in columns of their own rather than
        // embedded in the description text (customer drawing DRW-100: "SHEET STEEL (LASER)"/"RECT BAR"/
        // "ANGLE" carry no dimension in the text at all). null means "no column for that role, or
        // it was blank on this row" - mirrors how a missing float column already reads as 0 today.
        internal readonly record struct ColumnDimensions(float? Thickness, float? Width, float? Height);

        // TranslateFromColumns's relationship to ReadFromColumns mirrors Translate()'s to Read(),
        // except this returns null (not the original text) on failure - a caller needs to tell
        // "no column translation happened" apart from "translated to itself", which cannot occur
        // here since ReadFromColumns never echoes its input back.
        public static string? TranslateFromColumns(string? description, string? material, ColumnDimensions dims)
        {
            string text = (description ?? string.Empty).Trim();
            if (text.Length == 0)
                return null;

            MaterialSpec? spec = ReadFromColumns(text, material, dims);
            return spec is null ? null : StockCallout.Format(spec);
        }

        internal static MaterialSpec? Read(string text, string? material)
        {
            if (text.Length == 0)
                return null;

            // A thread is never a dimension, so that one is decided on its own.
            if (Thread.IsMatch(text))
                return null;

            // Hardware words are only believed when nothing names a stock form with dimensions
            // beside it. Several of them read as part names too - a BOLT PLATE is a plate, a PIN
            // PLATE is a plate - and "BOLT PLATE 1/4 x 4 x 5-1/8" was being thrown away because of
            // its first word.
            bool looksLikeStock = NamedForm(text) is not null && Group.IsMatch(text);

            if (!looksLikeStock && Hardware.IsMatch(text))
                return null;

            string grade = Grade(text, material);

            // AISC first: its designation contains an x and would otherwise be read as dimensions.
            var aisc = Aisc.Match(text);
            if (aisc.Success && NamesAnAiscSection(text))
            {
                return new MaterialSpec
                {
                    Form = StockForm.AiscSection,
                    Designation = $"{ChannelWord(text)} {aisc.Groups[1].Value.Replace(" ", string.Empty)} x {aisc.Groups[2].Value}".Trim()
                };
            }

            var mesh = Mesh.Match(text);
            if (Has(text, "EXP") && Has(text, "METAL") && mesh.Success)
            {
                return new MaterialSpec
                {
                    Form = StockForm.ExpandedMetal,
                    Mesh = $"{StockCallout.Measure(Value(mesh.Groups[1].Value))} x #{mesh.Groups[2].Value} Raised"
                };
            }

            var numbers = Numbers(text);
            StockForm? named = NamedForm(text);

            // Pipe and rod name one size and nothing else, so they are read before anything that
            // wants a list of dimensions.
            if (named is StockForm.Pipe or StockForm.PvcPipe or StockForm.Rod)
            {
                // The only forms named by one size, so they are the only ones allowed to read a
                // lone number. Everything else needs an x-joined run, which is what stops a part
                // name like CHECKERD_PLATE_1 becoming a material.
                float size = numbers.Count > 0 ? numbers[0] : SingleSize(text);

                // Schedule 40 unless the drawing said otherwise - not printed by StockCallout.Format
                // today, but needed to tell two pipes of the same size apart when ordering.
                var schedule = ScheduleToken.Match(text);
                int sch = schedule.Success && int.TryParse(schedule.Groups[1].Value, out int s) ? s : 40;

                return size > 0
                    ? new MaterialSpec { Form = named.Value, Section = new[] { size }, Grade = grade, Schedule = sch }
                    : null;
            }

            if (named == StockForm.RoundTube)
            {
                float diameter, wall;

                if (numbers.Count >= 2)
                {
                    diameter = numbers[0];
                    wall = numbers[1];
                }
                else
                {
                    // StockCallout.Format's own Round Tube callout - `2" OD x 11 GA wall HR` -
                    // separates the diameter and wall with "OD x ... wall" rather than joining
                    // them directly with an x, which is what the generic x-joined Numbers()
                    // reader (by design) requires. Read that one specific shape back so the
                    // app's own output round-trips, the same reason TryReadCanonicalFlatStock
                    // exists in BomLineOrder for Sheet/Plate's own single-number callout.
                    var canonical = RoundTubeCanonical.Match(Clean(text));
                    if (!canonical.Success)
                        return null;

                    diameter = Value(canonical.Groups[1].Value);
                    wall = Value(canonical.Groups[2].Value);
                }

                if (diameter <= 0 || wall <= 0)
                    return null;

                return new MaterialSpec
                {
                    Form = StockForm.RoundTube,
                    Section = new[] { diameter },
                    Thickness = wall,
                    Grade = grade
                };
            }

            // Tube and angle: two section sizes and a wall.
            if (named is StockForm.SquareTube or StockForm.RectangularTube or StockForm.Angle)
            {
                if (numbers.Count < 3)
                    return null;

                StockForm form = named.Value;

                // Square or rectangular is decided by the section, not by the word - a drawing that
                // says HSS says nothing about which.
                if (form is StockForm.SquareTube or StockForm.RectangularTube)
                {
                    form = Math.Abs(numbers[0] - numbers[1]) < 0.001f
                        ? StockForm.SquareTube
                        : StockForm.RectangularTube;
                }

                return new MaterialSpec
                {
                    Form = form,
                    Section = new[] { Math.Max(numbers[0], numbers[1]), Math.Min(numbers[0], numbers[1]) },
                    Thickness = numbers[2],
                    Grade = grade
                };
            }

            if (named == StockForm.SquareBar)
            {
                if (numbers.Count < 2)
                    return null;

                return new MaterialSpec
                {
                    Form = StockForm.SquareBar,
                    Section = new[] { numbers[0], numbers[1] },
                    Grade = grade
                };
            }

            // Flat bar: a thickness and a width, thickness first. Taken as the smaller and the
            // larger rather than by position, because drawings write them both ways round.
            if (named == StockForm.FlatBar)
            {
                if (numbers.Count < 2)
                    return null;

                return new MaterialSpec
                {
                    Form = StockForm.FlatBar,
                    Section = new[] { Math.Max(numbers[0], numbers[1]) },
                    Thickness = Math.Min(numbers[0], numbers[1]),
                    Grade = grade
                };
            }

            // Flat product. The thickness is the smallest of the numbers, and it decides on its own
            // whether this is sheet or plate - the cutoff is 7 GA, above which it is named as a
            // fraction and called plate.
            if (named is StockForm.Sheet or StockForm.Plate || numbers.Count == 3)
            {
                if (numbers.Count < 2)
                    return null;

                float thickness = numbers.Min();

                // Above this it is not flat product. A block 2.125 thick and a foam pad
                // 2.00x4.00x24.00 both used to come out as Plate 1, which is a material we do not
                // buy and a part nobody could cut - better to pass the description through.
                if (thickness > MaxFlatThickness)
                    return null;

                return new MaterialSpec
                {
                    Form = StockThickness.FormFor(thickness),
                    Thickness = thickness,
                    Grade = grade
                };
            }

            return null;
        }

        // Read()'s sibling for a drawing that carries its dimensions in columns of their own
        // instead of embedded in the description text - see ColumnDimensions. Deliberately not a
        // refactor of Read(): the whole existing test suite pins Read()/Translate() byte-for-byte
        // for the text-only path, so keeping that path completely untouched is the strongest
        // guarantee this addition cannot regress it. Every branch below mirrors Read()'s own
        // per-form requirement, just sourcing its numbers from dims instead of Numbers(text).
        internal static MaterialSpec? ReadFromColumns(string text, string? material, ColumnDimensions dims)
        {
            if (text.Length == 0)
                return null;

            StockForm? named = NamedForm(text);
            if (named is null)
                return null;

            string grade = Grade(text, material);

            float? t = dims.Thickness is > 0f ? dims.Thickness : null;
            float? w = dims.Width is > 0f ? dims.Width : null;
            float? h = dims.Height is > 0f ? dims.Height : null;

            if (named is StockForm.Pipe or StockForm.PvcPipe or StockForm.Rod)
            {
                if (w is null)
                    return null;

                var schedule = ScheduleToken.Match(text);
                int sch = schedule.Success && int.TryParse(schedule.Groups[1].Value, out int s) ? s : 40;

                return new MaterialSpec
                    { Form = named.Value, Section = new[] { w.Value }, Grade = grade, Schedule = sch };
            }

            if (named == StockForm.RoundTube)
            {
                if (w is null || t is null)
                    return null;

                return new MaterialSpec
                    { Form = StockForm.RoundTube, Section = new[] { w.Value }, Thickness = t.Value, Grade = grade };
            }

            // Tube and angle: two section sizes and a wall.
            if (named is StockForm.SquareTube or StockForm.RectangularTube or StockForm.Angle)
            {
                if (w is null || h is null || t is null)
                    return null;

                StockForm form = named.Value;

                // Square or rectangular is decided by the section, not by the word - same as Read().
                if (form is StockForm.SquareTube or StockForm.RectangularTube)
                {
                    form = Math.Abs(w.Value - h.Value) < 0.001f
                        ? StockForm.SquareTube
                        : StockForm.RectangularTube;
                }

                return new MaterialSpec
                {
                    Form = form,
                    Section = new[] { Math.Max(w.Value, h.Value), Math.Min(w.Value, h.Value) },
                    Thickness = t.Value,
                    Grade = grade
                };
            }

            if (named == StockForm.SquareBar)
            {
                if (w is null || h is null)
                    return null;

                return new MaterialSpec
                    { Form = StockForm.SquareBar, Section = new[] { w.Value, h.Value }, Grade = grade };
            }

            // Flat bar: a thickness and a width. Either dimension column may be the one that
            // actually carries the bar's face width - a drawing that only fills Width or only
            // fills Height for a flat bar is not distinguishing them the way it does for a tube.
            if (named == StockForm.FlatBar)
            {
                float? section = w ?? h;
                if (section is null || t is null)
                    return null;

                return new MaterialSpec
                    { Form = StockForm.FlatBar, Section = new[] { section.Value }, Thickness = t.Value, Grade = grade };
            }

            // Flat product: the Thickness column already says which number it is, so - unlike
            // Read(), which has to guess it as the smallest of an undifferentiated number list -
            // there is nothing to pick out of a list here.
            if (named is StockForm.Sheet or StockForm.Plate)
            {
                if (t is null || t.Value > MaxFlatThickness)
                    return null;

                return new MaterialSpec { Form = StockThickness.FormFor(t.Value), Thickness = t.Value, Grade = grade };
            }

            return null;
        }

        // Which stock form the words point at. Order matters: the more specific phrases are tested
        // before the words they contain.
        internal static StockForm? NamedForm(string text)
        {
            if (Has(text, "PVC") && Has(text, "PIPE")) return StockForm.PvcPipe;
            if (Has(text, "PIPE")) return StockForm.Pipe;

            if (Has(text, "ROUND TUBE") || Has(text, "DOM")) return StockForm.RoundTube;
            if (Has(text, "SQ TUBE") || Has(text, "SQUARE TUBE")) return StockForm.SquareTube;
            if (Has(text, "REC TUBE") || Has(text, "RECT TUBE") || Has(text, "RECTANGULAR TUBE"))
                return StockForm.RectangularTube;

            // HSS is hollow structural section - square or rectangular, decided by its dimensions.
            if (Has(text, "HSS") || Has(text, "TUBE") || Has(text, "TUBING"))
                return StockForm.RectangularTube;

            // A whole word, or "Laser HRS Triangle 3/16" reads as an angle.
            if (HasWord(text, "ANGLE")) return StockForm.Angle;

            // "Rect Bar" is a customer drawing's own term for the same stock our BOMs call Flat Bar
            // (drawing DRW-100-Q/P, a real customer drawing whose native BOM table used it).
            if (Has(text, "FLAT BAR") || Has(text, "FLATBAR") || Has(text, "RECT BAR") || HasWord(text, "FB"))
                return StockForm.FlatBar;

            if (Has(text, "SQ BAR") || Has(text, "SQUARE BAR") || Has(text, "BAR STOCK"))
                return StockForm.SquareBar;

            if (HasWord(text, "ROD")) return StockForm.Rod;

            if (Has(text, "SHEET")) return StockForm.Sheet;
            if (Has(text, "PLATE") || HasWord(text, "PL")) return StockForm.Plate;

            return null;
        }

        // Only these words mean an AISC section is what is being called out.
        private static bool NamesAnAiscSection(string text) =>
            Has(text, "CHANNEL") || Has(text, "SECTION") || Has(text, "BEAM");

        // The word a channel or beam callout leads with, kept as the drawing had it so a C stays a
        // C Channel and a W stays a W Section.
        private static string ChannelWord(string text)
        {
            if (Has(text, "U CHANNEL") || Has(text, "U-CHANNEL")) return "U Channel";
            if (Has(text, "CHANNEL")) return "C Channel";
            if (Has(text, "W SECTION") || Has(text, "W-SECTION")) return "W Section";
            if (Has(text, "S SECTION") || Has(text, "S-SECTION")) return "S Section";

            return "C Channel";
        }

        // What a material column says when it means ordinary mild steel, or nothing at all. Anything
        // else in that column is a grade the drawing asked for by name.
        private static readonly string[] MildSteel =
        {
            "HR", "HRS", "CR", "CRS", "A-36", "A36", "A 36", "MS", "STD", "-", "N/A"
        };

        // Mild steel unless the drawing says otherwise. Written on every callout, so the material is
        // never left to be inferred from silence.
        internal static string Grade(string description, string? material)
        {
            string both = description + " " + (material ?? string.Empty);

            if (Has(both, "STAINLESS") || HasWord(both, "SS") || HasWord(both, "304") || HasWord(both, "316"))
                return "Stainless";

            if (Has(both, "ALUM") || HasWord(both, "AL") || HasWord(both, "6061") || HasWord(both, "5052"))
                return "Alum";

            if (Has(both, "HDPE")) return "HDPE";
            if (Has(both, "UHMW")) return "UHMW";
            if (Has(both, "DELRIN")) return "Delrin";

            // A grade column naming something that is not mild steel is carried through as written.
            // One customer calls its wear pads S-7, which is tool steel - defaulting that to HR would have
            // quoted a hardened part as hot-rolled, which is a wrong material rather than a wrong
            // label. Better to pass an unfamiliar grade through than to bury it.
            string named = (material ?? string.Empty).Trim();

            if (named.Length > 0 && !MildSteel.Contains(named, StringComparer.OrdinalIgnoreCase))
                return named;

            return "HR";
        }

        // Strips parentheticals and inch marks, and resolves a gauge token to its decimal
        // thickness - the common cleanup every reader of embedded numbers needs first.
        private static string Clean(string text)
        {
            string cleaned = Parenthetical.Replace(text, " ").Replace("\"", " ");
            return GaugeToken.Replace(cleaned, m => " " + GaugeInches(m.Groups[1].Value) + " ");
        }

        // The longest run of x-joined numbers, with gauges resolved to their decimal and bracketed
        // counts thrown away. Same reasoning as CalloutDimensions, which reads sizes for nesting:
        // requiring the x is what stops a part name like CB_PLATE_4_LINES_31 reading as 4 by 31.
        private static List<float> Numbers(string text)
        {
            string cleaned = Clean(text);

            string best = string.Empty;
            foreach (Match match in Group.Matches(cleaned))
            {
                if (match.Value.Length > best.Length)
                    best = match.Value;
            }

            if (best.Length == 0)
                return new List<float>();

            var numbers = new List<float>();
            foreach (Match match in SingleNumber.Matches(best))
            {
                float value = Value(match.Value);
                if (value > 0)
                    numbers.Add(value);
            }

            return numbers;
        }

        private static List<float>? Section(string text) => Numbers(text) is { Count: > 0 } n ? n : null;

        // The first number in a description, for the two forms named by a single size.
        private static float SingleSize(string text)
        {
            var match = SingleNumber.Match(Parenthetical.Replace(text, " ").Replace("\"", " "));
            return match.Success ? Value(match.Value) : 0f;
        }

        // A gauge as its thickness in inches, so it travels through the same ladder as a decimal off
        // a drawing. Steel sheet gauges, which is what these drawings use.
        internal static string GaugeInches(string gauge) => gauge switch
        {
            "7" => "0.1793",
            "8" => "0.1644",
            "9" => "0.1495",
            "10" => "0.1345",
            "11" => "0.1196",
            "12" => "0.1046",
            "14" => "0.0747",
            "16" => "0.0598",
            "18" => "0.0478",
            _ => "0.1196"
        };

        internal static float Value(string text)
        {
            string trimmed = text.Trim();

            var mixed = Regex.Match(trimmed, @"^(\d+)[- ](\d+)/(\d+)$");
            if (mixed.Success && int.TryParse(mixed.Groups[3].Value, out int md) && md != 0)
            {
                return int.Parse(mixed.Groups[1].Value) + (float)int.Parse(mixed.Groups[2].Value) / md;
            }

            var fraction = Regex.Match(trimmed, @"^(\d+)/(\d+)$");
            if (fraction.Success && int.TryParse(fraction.Groups[2].Value, out int fd) && fd != 0)
                return (float)int.Parse(fraction.Groups[1].Value) / fd;

            return float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;
        }

        private static bool Has(string text, string phrase) =>
            text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

        // For short forms - FB, PL, SS, AL - which would otherwise match inside a longer word.
        private static bool HasWord(string text, string word) =>
            Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase);
    }
}
