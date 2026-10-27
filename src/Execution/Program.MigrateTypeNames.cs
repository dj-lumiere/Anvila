using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The type names that changed, each with the name it has now: a value changed in one unbroken step is
    /// an <c>Unbroken</c>, a list that adds and removes at both ends and rotates is a <c>RotatableList</c>, and the failure
    /// of an allocation that gets no memory is an <c>OutOfMemoryError</c>, and the arbitrary-precision integer is an
    /// <c>Int</c>.</summary>
    private static readonly Dictionary<string, string> RenamedTypeNames = new(comparer: StringComparer.Ordinal)
    {
        ["Atomic"] = "Unbroken",
        ["AtomicType"] = "UnbrokenType",
        ["CircularList"] = "RotatableList",
        ["CircularListEmittable"] = "RotatableListEmitter",
        ["MemoryAllocationError"] = "OutOfMemoryError",
        ["Integer"] = "Int",
        ["Array"] = "Items",
        ["ArrayEmitter"] = "ItemsEmitter",
        ["SplitArray"] = "SplitItems",
        ["SplitArrayEmitter"] = "SplitItemsEmitter",
        ["BitArray"] = "Bits",
        ["BitArrayEmitter"] = "BitsEmitter"
    };

    /// <summary>An old type name written as a whole word, also as a module path part
    /// (<c>Collections.CircularList</c>).</summary>
    private static readonly Regex OldTypeName =
        new(pattern: @"(?<![\w`$])(?:AtomicType|Atomic|CircularListEmittable|CircularList|MemoryAllocationError|Integer|SplitArrayEmitter|SplitArray|BitArrayEmitter|BitArray|ArrayEmitter|Array)(?![\w`])",
            options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step type-names</c>: <c>Atomic</c> becomes <c>Unbroken</c>, <c>AtomicType</c> becomes
    /// <c>UnbrokenType</c>, <c>CircularList</c> (with its iterator) becomes <c>RotatableList</c>, and
    /// <c>MemoryAllocationError</c> becomes <c>OutOfMemoryError</c>, <c>Integer</c> becomes <c>Int</c>, and the fixed runs
    /// <c>Array</c>, <c>SplitArray</c> and <c>BitArray</c> (with their emitters) become <c>Items</c>, <c>SplitItems</c> and
    /// <c>Bits</c>, in code only
    /// (never inside a string or a comment).
    /// </summary>
    private static string RewriteTypeNames(string file, string source, Language language, int lineOffset)
    {
        return ReplaceInCode(text: source, pattern: OldTypeName, replacement: m => RenamedTypeNames[key: m.Value]);
    }
}
