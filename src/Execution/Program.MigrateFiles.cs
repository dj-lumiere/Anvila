using System.Text;
using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A call on the file manager type, <c>FileManager.name</c> or <c>OsFileManager.name</c>, up to the
    /// routine's name.</summary>
    private static readonly Regex FileManagerCall = new(
        pattern: @"(?<![\w.`$])(?:Os)?FileManager\.(?<name>[A-Za-z_]\w*)",
        options: RegexOptions.Compiled);

    /// <summary>The operating system services' names with <c>Os</c> in front: <c>OsFileManager</c>,
    /// <c>OsEnvironment</c>, <c>OsEntropy</c>.</summary>
    private static readonly Regex OsServiceName = new(
        pattern: @"(?<![\w.`$])Os(?<name>FileManager|Environment|Entropy)\b",
        options: RegexOptions.Compiled);

    /// <summary>A call of <c>subdir</c> on a directory, <c>.subdir(</c>.</summary>
    private static readonly Regex SubdirCall = new(pattern: @"\.subdir(?=\s*\()", options: RegexOptions.Compiled);

    /// <summary>The views a file was mapped as, <c>Exposed[T]</c> and <c>Wielded[T]</c>.</summary>
    private static readonly Regex OldMappedViewType = new(pattern: @"(?<![\w.`$])(?:Exposed|Wielded)(?=\[)",
        options: RegexOptions.Compiled);

    /// <summary>An import line, to find where a new one goes.</summary>
    private static readonly Regex ImportLine = new(pattern: @"^import[ \t]+\S", options: RegexOptions.Compiled);

    /// <summary>The file manager's directory routines that moved to the environment, by their new names.</summary>
    private static readonly Dictionary<string, string> MovedDirectoryRoutines = new(comparer: StringComparer.Ordinal)
    {
        ["current_directory"] = "working_directory",
        ["temp_directory"] = "temp_directory",
        ["home_directory"] = "home_directory"
    };

    /// <summary>The mapped views' routines, by the storage routine that replaces them.</summary>
    private static readonly Dictionary<string, string> MappedViewRoutines = new(comparer: StringComparer.Ordinal)
    {
        ["expose"] = "open_storage",
        ["wield"] = "open_storage",
        ["expose_range"] = "open_storage_range",
        ["wield_range"] = "open_storage_range"
    };

    /// <summary>
    /// <c>--step files</c>: the file batch. The operating system services are <c>FileManager</c>,
    /// <c>Environment</c> and <c>Entropy</c>, so <c>OsFileManager</c>, <c>OsEnvironment</c> and <c>OsEntropy</c> lose
    /// their <c>Os</c>. The working, temporary and home directories are
    /// <c>Environment.working_directory()</c> / <c>temp_directory()</c> / <c>home_directory()</c> (IO/Environment, its
    /// import added), and <c>set_current_directory</c> is gone: each call of it is listed. <c>Directory.subdir</c> is
    /// <c>go_to</c>. <c>FileManager.move(from:, to:)</c> is <c>FileManager.rename(at:, to: name)</c> when only the
    /// name changes and <c>FileManager.move(at:, into: directory)</c> when it keeps its name and goes elsewhere: a
    /// call whose two paths show which is rewritten, any other is listed. <c>expose</c> / <c>wield</c> (and their
    /// <c>_range</c> forms) are <c>open_storage</c> / <c>open_storage_range</c>, and <c>Exposed[T]</c> /
    /// <c>Wielded[T]</c> are <c>FileStorage[T]</c>: those lines are listed too, since a storage's cells are read with
    /// <c>s[i]</c> and written with <c>s[i] = v</c>. Strings and comments stay as they are.
    /// </summary>
    private static string RewriteFiles(string file, string source, Language language, int lineOffset)
    {
        bool needsEnvironmentImport = false;
        var result = new StringBuilder();
        int from = 0;
        foreach (Match call in FileManagerCall.Matches(input: source))
        {
            if (call.Index < from || !IsCode(text: source, at: call.Index))
            {
                continue;
            }

            string name = call.Groups[groupname: "name"].Value;
            int line = lineOffset + 1 + source.AsSpan(start: 0, length: call.Index).Count(value: '\n');
            string replacement;
            int end = call.Index + call.Length;
            if (MovedDirectoryRoutines.TryGetValue(key: name, value: out string? moved))
            {
                replacement = $"Environment.{moved}";
                needsEnvironmentImport = true;
            }
            else if (name == "set_current_directory")
            {
                Console.WriteLine(value: $"{file}:{line}: 'set_current_directory' is gone: keep the Directory and " +
                                         "build paths from it ('dir.file(name:)', 'dir.go_to(name:)'), and give a " +
                                         "child process its directory with 'run_program(..., working_directory:)'. " +
                                         "Rewrite it by hand");
                continue;
            }
            else if (name == "move" && RewriteMoveCall(source: source, nameEnd: end) is { } rewritten)
            {
                replacement = rewritten.Text;
                end = rewritten.End;
            }
            else
            {
                if (name == "move")
                {
                    Console.WriteLine(value: $"{file}:{line}: 'FileManager.move(from:, to:)' is " +
                                             "'FileManager.rename(at:, to: name)' when only the name changes and " +
                                             "'FileManager.move(at:, into: directory)' when it keeps its name and " +
                                             "goes into another directory. Rewrite it by hand");
                }

                if (MappedViewRoutines.TryGetValue(key: name, value: out string? storage))
                {
                    name = storage;
                    Console.WriteLine(value: $"{file}:{line}: a file opened as storage is a FileStorage[T] now: read " +
                                             "a cell with 's[i]' and write one with 's[i] = v' (in a viewing / " +
                                             "modifying block), it isn't 'dangerous' and needs no 'using'. Check " +
                                             "the lines that use it");
                }

                replacement = $"FileManager.{name}";
            }

            result.Append(value: source, startIndex: from, count: call.Index - from);
            result.Append(value: replacement);
            from = end;
        }

        result.Append(value: source, startIndex: from, count: source.Length - from);
        string text = ReplaceInCode(text: result.ToString(), pattern: SubdirCall, replacement: _ => ".go_to");
        text = ReplaceInCode(text: text, pattern: OldMappedViewType, replacement: _ => "FileStorage");
        text = ReplaceInCode(text: text, pattern: OsServiceName, replacement: m => m.Groups[groupname: "name"].Value);
        return needsEnvironmentImport
            ? WithImport(text: text, module: "IO/Environment")
            : text;
    }

    /// <summary>
    /// The new form of <c>FileManager.move(from: a, to: b)</c> when its two paths show what it does: the call from the
    /// routine's name to its closing parenthesis, as <c>FileManager.rename(at: a, to: name)</c> when <c>a</c> and
    /// <c>b</c> are <c>d.file(name: x)</c> and <c>d.file(name: y)</c> on the same <c>d</c>, or as
    /// <c>FileManager.move(at: a, into: e)</c> when they are <c>d.file(name: x)</c> and <c>e.file(name: x)</c>. Two
    /// <c>File(at: "...")</c> literals are told apart by their text the same way. Null when the paths don't show it.
    /// </summary>
    private static (string Text, int End)? RewriteMoveCall(string source, int nameEnd)
    {
        int open = nameEnd;
        while (open < source.Length && source[index: open] is ' ' or '\t')
        {
            open++;
        }

        if (open >= source.Length || source[index: open] != '(' ||
            ArgumentsOf(text: source, open: open) is not { } call)
        {
            return null;
        }

        if (!call.Named.TryGetValue(key: "from", value: out string? fromArgument) ||
            !call.Named.TryGetValue(key: "to", value: out string? toArgument))
        {
            return null;
        }

        string replacing = call.Named.TryGetValue(key: "replacing", value: out string? flag)
            ? $", replacing: {flag}"
            : "";
        if (PathParts(argument: fromArgument) is not { } source1 || PathParts(argument: toArgument) is not { } target)
        {
            return null;
        }

        if (source1.Parent == target.Parent && source1.Kind == target.Kind)
        {
            return ($"FileManager.rename(at: {fromArgument}, to: {target.Name}{replacing})", call.End);
        }

        if (source1.Name == target.Name && target.Parent.Length > 0)
        {
            string into = target.Kind == "literal"
                ? $"Directory(at: \"{target.Parent}\")"
                : target.Parent;
            return ($"FileManager.move(at: {fromArgument}, into: {into}{replacing})", call.End);
        }

        return null;
    }

    /// <summary>A path argument split into the directory it names and its last part: <c>d.file(name: x)</c> (or
    /// <c>go_to</c> / <c>subdir</c>) gives <c>d</c> and <c>x</c>, <c>File(at: "a/b")</c> (or <c>Directory</c>) gives
    /// <c>a</c> and <c>"b"</c>. Null for anything else.</summary>
    private static (string Parent, string Name, string Kind)? PathParts(string argument)
    {
        Match member = Regex.Match(input: argument.Trim(),
            pattern: @"^(?<parent>[A-Za-z_][\w.]*(?:\(\))?)\.(?<kind>file|go_to|subdir)\(\s*name:\s*(?<name>.+)\)$");
        if (member.Success)
        {
            string kind = member.Groups[groupname: "kind"].Value == "file" ? "file" : "directory";
            return (member.Groups[groupname: "parent"].Value, member.Groups[groupname: "name"].Value.Trim(), kind);
        }

        Match literal = Regex.Match(input: argument.Trim(),
            pattern: @"^(?:File|Directory)\(\s*at:\s*""(?<path>[^""\\{}]*)""\s*\)$");
        if (!literal.Success)
        {
            return null;
        }

        string path = literal.Groups[groupname: "path"].Value;
        int slash = path.LastIndexOf(value: '/');
        return slash < 0
            ? ("", $"\"{path}\"", "literal")
            : (path[..slash], $"\"{path[(slash + 1)..]}\"", "literal");
    }

    /// <summary>The named arguments of the call whose list opens at <paramref name="open"/>, each by its name, and
    /// the index just past its closing parenthesis. Null when the list doesn't close or holds an unnamed
    /// argument.</summary>
    private static (Dictionary<string, string> Named, int End)? ArgumentsOf(string text, int open)
    {
        var named = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        int depth = 0;
        int start = open + 1;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[index: i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0)
                    {
                        return AddArgument(named: named, argument: text[start..i])
                            ? (named, i + 1)
                            : null;
                    }

                    break;
                case ',' when depth == 1:
                    if (!AddArgument(named: named, argument: text[start..i]))
                    {
                        return null;
                    }

                    start = i + 1;
                    break;
            }
        }

        return null;
    }

    /// <summary>Adds one <c>name: value</c> argument to <paramref name="named"/>: false when it isn't named. An empty
    /// argument (after a trailing comma) adds nothing.</summary>
    private static bool AddArgument(Dictionary<string, string> named, string argument)
    {
        string trimmed = argument.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        Match match = Regex.Match(input: trimmed, pattern: @"^(?<name>[A-Za-z_]\w*)\s*:\s*(?<value>[\s\S]+)$");
        if (!match.Success)
        {
            return false;
        }

        named[key: match.Groups[groupname: "name"].Value] = match.Groups[groupname: "value"].Value.Trim();
        return true;
    }

    /// <summary>The source with <c>import module</c> added after its last import when it has imports and not this
    /// one. A source with no import (a code block showing a part of a file) is left as it is.</summary>
    private static string WithImport(string text, string module)
    {
        var lines = text.Split(separator: '\n').ToList();
        if (lines.Any(predicate: l => l.Trim() == $"import {module}"))
        {
            return text;
        }

        int last = lines.FindLastIndex(match: l => ImportLine.IsMatch(input: l));
        if (last < 0)
        {
            return text;
        }

        // Imports are kept in order: the new one goes before the first that sorts after it.
        int at = last + 1;
        for (int i = 0; i <= last; i++)
        {
            if (ImportLine.IsMatch(input: lines[index: i]) &&
                string.CompareOrdinal(strA: lines[index: i].Trim(), strB: $"import {module}") > 0)
            {
                at = i;
                break;
            }
        }

        lines.Insert(index: at, item: $"import {module}");
        return string.Join(separator: '\n', values: lines);
    }
}
