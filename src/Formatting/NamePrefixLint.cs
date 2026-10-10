using System.Text.RegularExpressions;
using SyntaxTree;

namespace Builder.Formatting;

/// <summary>
/// A name that carries a prefix for its kind or its scope, which a name here does not: the scope shows in the code
/// (<c>me.pos</c>, <c>global</c>), the type in its declaration and in the editor. Reported by <c>lint</c> as a hint
/// with the name without the prefix: <c>m_pos</c> → <c>pos</c>, <c>_count</c> → <c>count</c>, <c>p_node</c> →
/// <c>node</c>, <c>bDone</c> → <c>done</c>, a protocol <c>IComparable</c> → <c>Comparable</c>. A generic type parameter's
/// <c>T</c> (<c>TKey</c>) is a name of its own and is left alone.
/// </summary>
internal static partial class NamePrefixLint
{
    /// <summary>A prefixed name in <paramref name="program"/>: where, the name, and the name without the prefix.</summary>
    internal static List<(SourceLocation At, string Name, string Plain)> Find(SyntaxTree.Program program)
    {
        var found = new List<(SourceLocation, string, string)>();
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            Visit(node: node, found: found);
        }

        return found;
    }

    private static void Visit(ISyntaxTreeNode node, List<(SourceLocation, string, string)> found)
    {
        switch (node)
        {
            case RoutineDeclaration routine:
                Check(name: routine.Name.Contains(value: '.') ? routine.Name[(routine.Name.LastIndexOf(value: '.') + 1)..] : routine.Name,
                    at: routine.Location, found: found);
                foreach (Parameter parameter in routine.Parameters)
                {
                    Check(name: parameter.Name, at: parameter.Location, found: found);
                }

                AstWalker.Walk(root: routine.Body, visit: inner =>
                {
                    if (inner is VariableDeclaration local)
                    {
                        Check(name: local.Name, at: local.Location, found: found);
                    }
                });
                break;
            case RecordDeclaration record:
                VisitMembers(members: record.Members, found: found);
                break;
            case EntityDeclaration entity:
                VisitMembers(members: entity.Members, found: found);
                break;
            case ProtocolDeclaration protocol when InterfacePrefix().Match(input: protocol.Name) is { Success: true } i:
                found.Add(item: (protocol.Location, protocol.Name, protocol.Name[1..]));
                break;
            case VariableDeclaration { IsGlobal: true } global:
                Check(name: global.Name, at: global.Location, found: found);
                break;
        }
    }

    private static void VisitMembers(List<SyntaxTree.Declaration> members, List<(SourceLocation, string, string)> found)
    {
        foreach (SyntaxTree.Declaration member in members)
        {
            if (member is VariableDeclaration field)
            {
                Check(name: field.Name, at: field.Location, found: found);
            }
            else
            {
                Visit(node: member, found: found);
            }
        }
    }

    private static void Check(string name, SourceLocation at, List<(SourceLocation, string, string)> found)
    {
        if (Plain(name: name) is { } plain && plain != name)
        {
            found.Add(item: (at, name, plain));
        }
    }

    /// <summary>The name without its prefix, or null when it carries none.</summary>
    internal static string? Plain(string name)
    {
        // A builder name (`__ret_0`) and the hole `_` are not written by anyone.
        if (name.StartsWith(value: "__", comparisonType: StringComparison.Ordinal) || name == "_")
        {
            return null;
        }

        if (SnakePrefix().Match(input: name) is { Success: true } snake)
        {
            return name[snake.Length..];
        }

        if (CamelPrefix().Match(input: name) is { Success: true } camel)
        {
            string rest = name[camel.Groups[1].Length..];
            return SnakeCase(name: rest);
        }

        return null;
    }

    private static string SnakeCase(string name)
    {
        return CamelBoundary().Replace(input: name, replacement: "$1_$2").ToLowerInvariant();
    }

    /// <summary>A scope prefix (<c>m_</c>, <c>s_</c>, <c>g_</c>), a leading underscore, or a kind prefix (<c>i_</c>,
    /// <c>p_</c>, <c>b_</c>, <c>sz_</c>, <c>str_</c>, <c>ptr_</c>), before a lowercase letter.</summary>
    [GeneratedRegex(pattern: "^(?:m_|s_|g_|_|i_|p_|b_|sz_|str_|ptr_)(?=[a-z])")]
    private static partial Regex SnakePrefix();

    /// <summary>A Hungarian kind prefix glued to a capitalized name: <c>iCount</c>, <c>pNode</c>, <c>bDone</c>.</summary>
    [GeneratedRegex(pattern: "^(i|p|b|sz|str|ptr)(?=[A-Z][a-z])")]
    private static partial Regex CamelPrefix();

    [GeneratedRegex(pattern: "([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundary();

    /// <summary>An interface-style <c>I</c> before a capitalized name: <c>IComparable</c>.</summary>
    [GeneratedRegex(pattern: "^I(?=[A-Z][a-z])")]
    private static partial Regex InterfacePrefix();
}
