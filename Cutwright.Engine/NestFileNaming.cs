namespace Cutwright
{
    // Turns a material group's free-text description into a filename component.
    //
    // Lives apart from the window that uses it because it is pure string policy with real teeth:
    // descriptions come straight off the BOM, full of fraction slashes and colons, and a slash
    // that survives to Path.Combine makes the rest of the name a directory that was never
    // created. Kept separate so it can be tested against real descriptions directly.
    internal static class NestFileNaming
    {
    //Turns a material group's description into something usable inside a filename. Every
    //character Windows rejects becomes an underscore, runs of them collapse, and the result is
    //capped so a long description plus a long save path can't push the whole thing past the
    //maximum path length.
    public static string SafeFileNamePart(string? description)
    {
        const int maxLength = 60;

        if (string.IsNullOrWhiteSpace(description))
            return "Sheet";

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(description.Length);

        foreach (char c in description)
        {
            //Periods are kept - a thickness reads better as ".25 X 48" than "_25_X_48", and
            //they're legal in a filename anywhere except the very end.
            bool illegal = c == ' ' || Array.IndexOf(invalid, c) >= 0;
            char mapped = illegal ? '_' : c;

            //Collapse runs, so "3/16 x 3" doesn't come out with a stretch of underscores.
            if (mapped == '_' && builder.Length > 0 && builder[^1] == '_')
                continue;

            builder.Append(mapped);
        }

        string result = builder.ToString().Trim('_').TrimEnd('.');

        if (result.Length > maxLength)
            result = result[..maxLength].TrimEnd('_', '.');

        //A description made up entirely of punctuation would scrub away to nothing.
        return result.Length == 0 ? "Sheet" : result;
    }

    //Two different descriptions can scrub down to the same name - "3/4 PLATE" and "3-4 PLATE"
    //both become "3_4_PLATE" - and without this the second group would quietly overwrite the
    //first group's files.
    public static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name))
            return name;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{name}_{suffix}";
            if (used.Add(candidate))
                return candidate;
        }
    }
    }
}
