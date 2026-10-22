using System.Text.RegularExpressions;

namespace Builder.TesseraEmit;

/// <summary>
/// The rewrites this backend puts its output through: it still writes a few older spellings (`Void`, `#name`
/// annotations, `require`). Kept next to the writer that needs them.
/// </summary>
internal static class TesseraOldSpellings
{
    private static readonly Regex AnnotationLine = new(@"^(\s*)#(\[)?(.*)$", RegexOptions.Compiled);
    private static readonly Regex Keyword = new(@"^(\s*)(require|conform|concept)\b", RegexOptions.Compiled);
    private static readonly Regex Branch = new(@"^(\s*)branch\s+(.+?)\s*\?\s*(.+?)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Return = new(@"\breturn\(([^()]*(?:\([^()]*\))*[^()]*)\)", RegexOptions.Compiled);

    /// The old spellings outside routine bodies (and inside assembly routines) rewritten line by line.
    public static string Spellings(string text)
    {
        var result = new List<string>();
        bool inAsm = false;
        // An annotation list over several lines (`#[external("llvm"),` / `    template(...)]`) is read as one.
        string? openList = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            if (openList is not null)
            {
                openList += " " + line.Trim();
                if (!line.TrimEnd().EndsWith("]")) continue;
                line = openList;
                openList = null;
            }
            string trimmed = line.TrimStart();
            // A comment line: `//` in a source not moved to `#` comments yet, `# ...` or `###` in one that is. An old
            // annotation (`@inline`, `#[a, b]`) is not one.
            if (trimmed.StartsWith("//") || trimmed == "#" || trimmed.StartsWith("# ") || trimmed.StartsWith("##"))
            {
                result.Add(line);
                continue;
            }
            if (trimmed.StartsWith("#[") && !trimmed.TrimEnd().EndsWith("]"))
            {
                openList = line.TrimEnd();
                continue;
            }
            if (AnnotationLine.Match(line) is { Success: true } a && !trimmed.StartsWith("#["))
            {
                result.Add($"{a.Groups[1].Value}@{a.Groups[3].Value}");
                inAsm = trimmed.Contains("external(\"asm\")");
                continue;
            }
            if (trimmed.StartsWith("#[") && trimmed.TrimEnd().EndsWith("]"))
            {
                trimmed = trimmed.TrimEnd();
                string indent = line[..(line.Length - trimmed.Length)];
                foreach (string item in SplitTopLevel(trimmed[2..^1])) result.Add($"{indent}@{item.Trim()}");
                inAsm = trimmed.Contains("external(\"asm\")");
                continue;
            }
            // An annotation inside a Callable type (`Callable[@callconv("stdcall"), ...]`) is written @ too.
            line = line.Replace("Callable[#", "Callable[@");
            if (Keyword.Match(line) is { Success: true } k)
                line = k.Groups[1].Value + k.Groups[2].Value switch { "require" => "needs", "conform" => "obeys", _ => "protocol" } + line[k.Length..];
            if (line.StartsWith("routine ") || line.StartsWith("private routine ") || line.StartsWith("secret routine ") || line.StartsWith("internal routine "))
            {
                // the annotation lines above said whether this routine is assembly
                result.Add(line);
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith("needs") && !line.StartsWith("obeys")) inAsm = false;
            if (inAsm)
            {
                line = Return.Replace(line, m => m.Groups[1].Value.Trim().Length == 0 ? "return" : $"return {m.Groups[1].Value.Trim()}");
                if (Branch.Match(line) is { Success: true } b)
                {
                    string indent = b.Groups[1].Value;
                    string cond = b.Groups[2].Value, yes = b.Groups[3].Value, no = b.Groups[4].Value;
                    string Arm(string arm) => arm == "continue" ? "" : arm.StartsWith("return") || arm == "unreachable" ? arm : $"jump {arm}";
                    if (no == "continue")
                    {
                        result.Add($"{indent}if {cond}");
                        result.Add($"{indent}    {Arm(yes)}");
                    }
                    else if (yes == "continue")
                    {
                        result.Add($"{indent}if not {cond}");
                        result.Add($"{indent}    {Arm(no)}");
                    }
                    else
                    {
                        result.Add($"{indent}if {cond}");
                        result.Add($"{indent}    {Arm(yes)}");
                        result.Add($"{indent}else");
                        result.Add($"{indent}    {Arm(no)}");
                    }
                    continue;
                }
                line = Regex.Replace(line, @"^(\s*)claim\b", "$1local");
            }
            result.Add(line);
        }
        return string.Join("\n", result);
    }

    private static IEnumerable<string> SplitTopLevel(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return list[start..i];
                start = i + 1;
            }
        }
        yield return list[start..];
    }

    private static readonly Regex VoidOrUnit = new(@"(?<![\w`])(Void|Unit)(?![\w`])", RegexOptions.Compiled);

    /// <summary>`Void` and `Unit` are `None`.</summary>
    public static string NoneSpelling(string text) => VoidOrUnit.Replace(input: text, replacement: "None");
}
