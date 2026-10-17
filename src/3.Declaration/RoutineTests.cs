using System.Globalization;
using System.Text;
using SyntaxTree;

namespace Builder.Declaration;

/// <summary>One <c>@case</c> of a test routine: the arguments it is called with, and the value it must give back when
/// one is written.</summary>
public sealed record RoutineTestCase(List<string> Arguments, string? Output);

/// <summary>A routine marked <c>@test</c>, with what its other test annotations say.</summary>
/// <param name="Name">The routine's name.</param>
/// <param name="Cases">Its <c>@case</c>s, in the order written. Empty for a routine called once with no arguments.</param>
/// <param name="Skip">Whether it is marked <c>@skip</c>, and the reason when one is given.</param>
/// <param name="TimeLimit">Its <c>@time_limit</c>, when it has one.</param>
/// <param name="MemoryLimit">Its <c>@memory_limit</c> in bytes, when it has one.</param>
/// <param name="Problem">Why it can't be run as written, or null.</param>
public sealed record RoutineTest(
    string Name,
    List<RoutineTestCase> Cases,
    (bool Skipped, string? Reason) Skip,
    TimeSpan? TimeLimit,
    ulong? MemoryLimit,
    string? Problem);

/// <summary>
/// The routines a program marks <c>@test</c>, and the <c>start()</c> the builder puts in place of the program's own to
/// run one of them: it calls the routine once per <c>@case</c> (or once, with no arguments, when there is none),
/// compares each result with the case's output, reports every case that differs on standard error and ends
/// with exit status 1 when any did.
/// </summary>
public static class RoutineTests
{
    /// <summary>The <c>@test</c> routines declared at the top level of <paramref name="program"/>.</summary>
    public static List<RoutineTest> Find(Program program)
    {
        var tests = new List<RoutineTest>();
        foreach (RoutineDeclaration routine in program.Declarations.OfType<RoutineDeclaration>())
        {
            if (!routine.Annotations.Contains(item: "test"))
            {
                continue;
            }

            tests.Add(item: Describe(routine: routine));
        }

        return tests;
    }

    private static RoutineTest Describe(RoutineDeclaration routine)
    {
        var cases = new List<RoutineTestCase>();
        (bool, string?) skip = (false, null);
        TimeSpan? timeLimit = null;
        ulong? memoryLimit = null;
        string? problem = null;
        foreach (string annotation in routine.Annotations)
        {
            (string name, List<(string? Key, string Value)> args) = Split(annotation: annotation);
            switch (name)
            {
                case "case":
                    string? input = args.FirstOrDefault(predicate: a => a.Key == "input").Value;
                    string? output = args.FirstOrDefault(predicate: a => a.Key == "output").Value;
                    cases.Add(item: new RoutineTestCase(Arguments: input == null
                            ? []
                            : TupleItems(value: input),
                        Output: output));
                    break;
                case "skip":
                    skip = (true, args.Count > 0
                        ? Unquote(text: args[0].Value)
                        : null);
                    break;
                case "memory_limit":
                    memoryLimit = args.Count > 0
                        ? ParseByteSize(text: args[0].Value)
                        : null;
                    problem ??= memoryLimit is null or 0
                        ? "@memory_limit needs a size, such as @memory_limit(1mib)"
                        : null;
                    break;
                case "time_limit":
                    timeLimit = args.Count > 0
                        ? ParseDuration(text: args[0].Value)
                        : null;
                    problem ??= timeLimit == null
                        ? "@time_limit needs a duration, such as @time_limit(2s)"
                        : null;
                    break;
            }
        }

        if (routine.GenericParameters is { Count: > 0 })
        {
            problem ??= "a @test routine can't have type parameters";
        }

        if (cases.Count == 0 && routine.Parameters.Count > 0)
        {
            problem ??= "a @test routine with parameters needs a @case for each call";
        }

        foreach (RoutineTestCase testCase in cases)
        {
            if (testCase.Arguments.Count != routine.Parameters.Count)
            {
                problem ??= $"a @case gives {testCase.Arguments.Count} argument(s), and '{routine.Name}' takes " +
                            $"{routine.Parameters.Count}";
            }

            if (testCase.Output != null && routine.ReturnType == null)
            {
                problem ??= $"a @case expects an output, and '{routine.Name}' gives nothing back";
            }
        }

        return new RoutineTest(Name: routine.Name,
            Cases: cases,
            Skip: skip,
            TimeLimit: timeLimit,
            MemoryLimit: memoryLimit,
            Problem: problem);
    }

    /// <summary>
    /// The source of the <c>start()</c> that runs <paramref name="test"/>: one call per case, each result compared
    /// with the case's output, a line on standard error for every case that differs, and exit status 1 at the
    /// end when any did.
    /// </summary>
    public static string StartSource(RoutineTest test, RoutineDeclaration routine)
    {
        var sb = new StringBuilder();
        sb.AppendLine(value: "routine start()");
        if (test.Cases.Count == 0)
        {
            sb.AppendLine(value: routine.ReturnType == null
                ? $"    {test.Name}()"
                : $"    drop {test.Name}()");
            sb.AppendLine(value: "    return");
            return sb.ToString();
        }

        sb.AppendLine(value: "    var __failed: S64 = 0");
        for (int i = 0; i < test.Cases.Count; i++)
        {
            RoutineTestCase testCase = test.Cases[index: i];
            string call = $"{test.Name}(" + string.Join(separator: ", ",
                values: routine.Parameters.Select(selector: (p, at) => $"{p.Name}: {testCase.Arguments[index: at]}")) + ")";
            if (testCase.Output == null)
            {
                sb.AppendLine(value: routine.ReturnType == null
                    ? $"    {call}"
                    : $"    drop {call}");
                continue;
            }

            string actual = $"__case_{i + 1}";
            string written = string.Join(separator: ", ", values: testCase.Arguments);
            sb.AppendLine(value: $"    var {actual} = {call}");
            sb.AppendLine(value: $"    if {actual} != {testCase.Output}");
            sb.AppendLine(value: $"        alert(f\"case {i + 1} ({Escape(text: written)}): expected " +
                                 $"{Escape(text: testCase.Output)}, got {{{actual}}}\")");
            sb.AppendLine(value: "        __failed += 1");
        }

        sb.AppendLine(value: "    if __failed > 0");
        sb.AppendLine(value: "        set_exit_code(1)");
        sb.AppendLine(value: "    return");
        return sb.ToString();
    }

    /// <summary>The name of an annotation and its arguments, each with its name when it has one
    /// (<c>case(input=(1, 2), output=3)</c> as the parser keeps it).</summary>
    private static (string Name, List<(string? Key, string Value)> Arguments) Split(string annotation)
    {
        int open = annotation.IndexOf(value: '(');
        if (open < 0 || !annotation.EndsWith(value: ')'))
        {
            return (annotation, []);
        }

        var arguments = new List<(string?, string)>();
        foreach (string written in TopLevel(text: annotation[(open + 1)..^1]))
        {
            string part = written.Trim();
            int equals = part.IndexOf(value: '=');
            bool named = equals > 0 && part[..equals]
                                      .All(predicate: c => char.IsLetterOrDigit(c: c) || c == '_');
            arguments.Add(item: named
                ? (part[..equals].Trim(), part[(equals + 1)..].Trim())
                : (null, part.Trim()));
        }

        return (annotation[..open], arguments);
    }

    /// <summary>The items of a written tuple <c>(a, b)</c>, or the value itself when it is not one.</summary>
    private static List<string> TupleItems(string value)
    {
        string trimmed = value.Trim();
        return trimmed.StartsWith(value: '(') && trimmed.EndsWith(value: ')')
            ? TopLevel(text: trimmed[1..^1]).Select(selector: s => s.Trim())
                                           .Where(predicate: s => s.Length > 0)
                                           .ToList()
            : [trimmed];
    }

    /// <summary>The comma-separated parts of <paramref name="text"/> that are not inside brackets or quotes.</summary>
    private static List<string> TopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        bool quoted = false;
        int from = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[index: i];
            if (c == '"' && (i == 0 || text[index: i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '(' or '[')
            {
                depth++;
            }
            else if (!quoted && c is ')' or ']')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && c == ',')
            {
                parts.Add(item: text[from..i]);
                from = i + 1;
            }
        }

        if (text[from..].Trim().Length > 0)
        {
            parts.Add(item: text[from..]);
        }

        return parts;
    }

    /// <summary>A duration written as an annotation argument, with the units a duration literal takes (<c>ns</c>,
    /// <c>us</c>, <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>, <c>w</c>), or null.</summary>
    private static TimeSpan? ParseDuration(string text)
    {
        string t = text.Replace(oldValue: "_", newValue: "").Trim();
        int unitAt = 0;
        while (unitAt < t.Length && (char.IsDigit(c: t[index: unitAt]) || t[index: unitAt] == '.'))
        {
            unitAt++;
        }

        if (unitAt == 0 || !double.TryParse(s: t[..unitAt], style: NumberStyles.Float,
                provider: CultureInfo.InvariantCulture, result: out double amount))
        {
            return null;
        }

        return t[unitAt..] switch
        {
            "ns" => TimeSpan.FromTicks(value: (long)(amount / 100)),
            "us" => TimeSpan.FromTicks(value: (long)(amount * 10)),
            "ms" => TimeSpan.FromMilliseconds(value: amount),
            "s" => TimeSpan.FromSeconds(value: amount),
            "m" => TimeSpan.FromMinutes(value: amount),
            "h" => TimeSpan.FromHours(value: amount),
            "d" => TimeSpan.FromDays(value: amount),
            "w" => TimeSpan.FromDays(value: amount * 7),
            _ => null
        };
    }

    /// <summary>A size written as an annotation argument, with the units a size literal takes (<c>b</c>, <c>kb</c>,
    /// <c>mb</c>, <c>gb</c>, <c>kib</c>, <c>mib</c>, <c>gib</c>), in bytes, or null.</summary>
    private static ulong? ParseByteSize(string text)
    {
        string t = text.Replace(oldValue: "_", newValue: "").Trim();
        int unitAt = 0;
        while (unitAt < t.Length && char.IsDigit(c: t[index: unitAt]))
        {
            unitAt++;
        }

        if (unitAt == 0 || !ulong.TryParse(s: t[..unitAt], style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture, result: out ulong amount))
        {
            return null;
        }

        ulong? unit = t[unitAt..] switch
        {
            "b" => 1UL,
            "kb" => 1_000UL,
            "mb" => 1_000_000UL,
            "gb" => 1_000_000_000UL,
            "kib" => 1_024UL,
            "mib" => 1_048_576UL,
            "gib" => 1_073_741_824UL,
            _ => null
        };
        return unit * amount;
    }

    private static string Unquote(string text)
    {
        string t = text.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"'
            ? t[1..^1]
            : t;
    }

    /// <summary>Written source text made safe inside an f-text: quotes and braces escaped.</summary>
    private static string Escape(string text)
    {
        return text.Replace(oldValue: "\\", newValue: "\\\\")
                   .Replace(oldValue: "\"", newValue: "\\\"")
                   .Replace(oldValue: "{", newValue: "{{")
                   .Replace(oldValue: "}", newValue: "}}");
    }
}
