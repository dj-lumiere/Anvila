using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using SyntaxTree;

namespace Builder.Formatting;

/// <summary>
/// Compares two parsed trees structurally: node kinds, names, operators, literal values and types, flags and
/// lists, ignoring source locations. It is the formatter's round-trip guard, so two trees that compare equal are
/// the same program to every later phase. The spellings the formatter canonicalizes compare equal by construction
/// (the tree does not record them), with one exception that the tree does record: the text of a hexadecimal
/// number, compared in its canonical spelling.
/// </summary>
internal static class AstComparer
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    /// <summary>
    /// The first difference between <paramref name="expected"/> (its top level taken in
    /// <paramref name="expectedTopLevelOrder"/>, the order it was printed in) and <paramref name="actual"/>, or null
    /// when they are the same tree.
    /// </summary>
    public static string? Compare(SyntaxTree.Program expected, SyntaxTree.Program actual,
        IReadOnlyList<ISyntaxTreeNode>? expectedTopLevelOrder)
    {
        IReadOnlyList<ISyntaxTreeNode> order = expectedTopLevelOrder ?? expected.Declarations;
        if (order.Count != actual.Declarations.Count)
        {
            return $"{order.Count} top-level declarations became {actual.Declarations.Count}";
        }

        for (int i = 0; i < order.Count; i++)
        {
            string? difference = CompareValues(expected: order[index: i],
                actual: actual.Declarations[index: i],
                path: $"top-level[{i}]({Describe(node: order[index: i])})",
                depth: 0);
            if (difference != null)
            {
                return difference;
            }
        }

        return null;
    }

    private static string Describe(object node)
    {
        return node switch
        {
            RoutineDeclaration routine => "routine " + routine.QualifiedName,
            SyntaxTree.Declaration declaration => declaration.GetType()
                                                             .Name + " at line " + declaration.Location.Line,
            ISyntaxTreeNode syntax => syntax.GetType()
                                            .Name + " at line " + syntax.Location.Line,
            _ => node.GetType()
                     .Name
        };
    }

    private static string? CompareValues(object? expected, object? actual, string path, int depth)
    {
        if (depth > 400)
        {
            return $"{path}: the tree is too deep to compare";
        }

        if (expected == null || actual == null)
        {
            return expected == null && actual == null
                ? null
                : $"{path}: {Show(value: expected)} became {Show(value: actual)}";
        }

        Type type = expected.GetType();
        if (type != actual.GetType())
        {
            return $"{path}: a {type.Name} became a {actual.GetType().Name}";
        }

        if (expected is SourceLocation)
        {
            return null;
        }

        if (expected is string text)
        {
            return text == (string)actual || SameNumber(a: text, b: (string)actual)
                ? null
                : $"{path}: '{text}' became '{actual}'";
        }

        if (type.IsPrimitive || type.IsEnum || expected is decimal or Half or Int128 or UInt128)
        {
            return expected.Equals(obj: actual)
                ? null
                : $"{path}: {expected} became {actual}";
        }

        if (expected is ITuple tuple)
        {
            var other = (ITuple)actual;
            for (int i = 0; i < tuple.Length; i++)
            {
                string? difference = CompareValues(expected: tuple[index: i], actual: other[index: i],
                    path: $"{path}.Item{i + 1}", depth: depth + 1);
                if (difference != null)
                {
                    return difference;
                }
            }

            return null;
        }

        if (expected is IEnumerable<string> set && type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(HashSet<>) || expected is IReadOnlySet<string>))
        {
            var left = new HashSet<string>(collection: set);
            return left.SetEquals(other: (IEnumerable<string>)actual)
                ? null
                : $"{path}: the set {{{string.Join(separator: ", ", values: left)}}} changed";
        }

        if (expected is IList list)
        {
            var other = (IList)actual;
            if (list.Count != other.Count)
            {
                return $"{path}: {list.Count} items became {other.Count}";
            }

            for (int i = 0; i < list.Count; i++)
            {
                string? difference = CompareValues(expected: list[index: i], actual: other[index: i],
                    path: $"{path}[{i}]", depth: depth + 1);
                if (difference != null)
                {
                    return difference;
                }
            }

            return null;
        }

        foreach (PropertyInfo property in PropertiesOf(type: type))
        {
            if (expected is ExpressionPart part && property.Name == nameof(ExpressionPart.SourceText) &&
                part.FormatSpec is not ("=" or "=?"))
            {
                continue;
            }

            // Generic constraints are a set: the layout lists the kind classifiers first and the bracket
            // constraints as `needs` clauses, which can reorder them.
            string? difference = property.Name is "GenericConstraints" or "AssociatedTypes"
                ? CompareUnordered(expected: property.GetValue(obj: expected) as IList,
                    actual: property.GetValue(obj: actual) as IList,
                    path: $"{path}.{property.Name}",
                    depth: depth + 1)
                : CompareValues(expected: property.GetValue(obj: expected),
                    actual: property.GetValue(obj: actual),
                    path: $"{path}.{property.Name}",
                    depth: depth + 1);
            if (difference != null)
            {
                return difference;
            }
        }

        return null;
    }

    /// <summary>Compares two lists as multisets: each expected item must match a distinct actual item.</summary>
    private static string? CompareUnordered(IList? expected, IList? actual, string path, int depth)
    {
        if (expected == null || actual == null)
        {
            return CompareValues(expected: expected, actual: actual, path: path, depth: depth);
        }

        if (expected.Count != actual.Count)
        {
            return $"{path}: {expected.Count} items became {actual.Count}";
        }

        var used = new bool[actual.Count];
        for (int i = 0; i < expected.Count; i++)
        {
            int match = -1;
            for (int k = 0; k < actual.Count && match < 0; k++)
            {
                if (!used[k] && CompareValues(expected: expected[index: i], actual: actual[index: k],
                        path: path, depth: depth + 1) == null)
                {
                    match = k;
                }
            }

            if (match < 0)
            {
                return $"{path}[{i}]: no matching item in the formatted tree";
            }

            used[match] = true;
        }

        return null;
    }

    /// <summary>The properties compared on a node: public, readable, not indexed, not a source location.</summary>
    private static PropertyInfo[] PropertiesOf(Type type)
    {
        return Properties.GetOrAdd(key: type, valueFactory: t => t.GetProperties(bindingAttr: BindingFlags.Public |
                                                                         BindingFlags.Instance)
                                                                    .Where(predicate: p =>
                                                                         p.CanRead &&
                                                                         p.GetIndexParameters()
                                                                          .Length == 0 &&
                                                                         p.PropertyType != typeof(SourceLocation) &&
                                                                         p.Name != "EqualityContract")
                                                                    .OrderBy(keySelector: p => p.Name,
                                                                         comparer: StringComparer.Ordinal)
                                                                    .ToArray());
    }

    /// <summary>Two spellings of one number: the formatter canonicalizes a hexadecimal literal.</summary>
    private static bool SameNumber(string a, string b)
    {
        return Literals.LooksNumeric(name: a) && Literals.LooksNumeric(name: b) &&
               Literals.CanonicalNumber(text: a) == Literals.CanonicalNumber(text: b);
    }

    private static string Show(object? value)
    {
        return value switch
        {
            null => "nothing",
            string s => $"'{s}'",
            _ => value.GetType()
                      .Name
        };
    }
}
