namespace Builder.Documentation;

/// <summary>
/// A <c>###</c> doc comment parsed into its summary prose and the reStructuredText-style field lists the docs
/// use: <c>:param name:</c>, <c>:typeparam Name:</c>, <c>:returns:</c>, <c>:throws:</c>, <c>:absent:</c>,
/// <c>:note:</c>, <c>:see:</c>. The editor hover and the API reference pages both read doc comments through it.
/// </summary>
public sealed record DocComment(
    string Summary,
    List<(string Name, string Desc)> Params,
    List<(string Name, string Desc)> TypeParams,
    string? Returns,
    List<string> Throws,
    List<string> Absent,
    List<string> Notes,
    List<string> Sees)
{
    /// <summary>
    /// Parses the text of a doc comment (its lines without the <c>###</c> marker). Lines before the first field
    /// are the summary. A line that does not open a new <c>:field:</c> continues the previous field's text, or
    /// the summary's. An unknown field stays in the summary as written, so nothing is lost.
    /// </summary>
    public static DocComment Parse(string doc)
    {
        var summary = new List<string>();
        var pars = new List<(string Name, string Desc)>();
        var typePars = new List<(string Name, string Desc)>();
        string? returns = null;
        var throws = new List<string>();
        var absent = new List<string>();
        var notes = new List<string>();
        var sees = new List<string>();

        // Where the last field's continuation text goes. Null while still in the summary.
        Action<string>? append = null;

        foreach (string raw in doc.Replace(oldValue: "\r", newValue: "")
                                  .Split(separator: '\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(value: ':') && line.IndexOf(value: ':', startIndex: 1) is var colon and > 0)
            {
                string spec = line[1..colon]
                   .Trim();
                string desc = line[(colon + 1)..]
                   .Trim();
                string[] parts = spec.Split(separator: ' ', count: 2, options: StringSplitOptions.RemoveEmptyEntries);
                string kind = parts.Length > 0
                    ? parts[0]
                       .ToLowerInvariant()
                    : "";
                string? name = parts.Length > 1
                    ? parts[1]
                    : null;

                switch (kind)
                {
                    case "param" when name != null:
                        append = AppendTo(list: pars, entry: (name, desc));
                        break;
                    case "typeparam" when name != null:
                        append = AppendTo(list: typePars, entry: (name, desc));
                        break;
                    case "returns":
                        returns = desc;
                        append = s => returns = $"{returns} {s}".Trim();
                        break;
                    case "throws":
                        append = AppendTo(list: throws, entry: desc);
                        break;
                    case "absent":
                        append = AppendTo(list: absent, entry: desc);
                        break;
                    case "note":
                        append = AppendTo(list: notes, entry: desc);
                        break;
                    case "see":
                        append = AppendTo(list: sees, entry: desc);
                        break;
                    default:
                        summary.Add(item: line);
                        append = null;
                        break;
                }
            }
            else if (append != null && line.Length > 0)
            {
                append(obj: line);
            }
            else
            {
                append = null;
                summary.Add(item: line);
            }
        }

        return new DocComment(Summary: string.Join(separator: "\n", values: summary)
                                             .Trim(),
            Params: pars,
            TypeParams: typePars,
            Returns: returns,
            Throws: throws,
            Absent: absent,
            Notes: notes,
            Sees: sees);
    }

    private static Action<string> AppendTo(List<string> list, string entry)
    {
        list.Add(item: entry);
        int at = list.Count - 1;
        return s => list[index: at] = $"{list[index: at]} {s}".Trim();
    }

    private static Action<string> AppendTo(List<(string Name, string Desc)> list, (string Name, string Desc) entry)
    {
        list.Add(item: entry);
        int at = list.Count - 1;
        return s => list[index: at] = (list[index: at].Name, $"{list[index: at].Desc} {s}".Trim());
    }
}
