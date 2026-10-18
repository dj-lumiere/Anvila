namespace Builder.Diagnostics;

/// <summary>One replacement in a source file: the text from the start position up to (not including) the end position
/// becomes <see cref="NewText"/>. Lines and columns are 1-based. An insertion has the same start and end.</summary>
public sealed record TextEdit(int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText)
{
    /// <summary>Inserts <paramref name="text"/> at a position.</summary>
    public static TextEdit Insert(int line, int column, string text)
    {
        return new TextEdit(StartLine: line, StartColumn: column, EndLine: line, EndColumn: column, NewText: text);
    }
}

/// <summary>
/// The change a diagnostic proposes, which the language server offers as a quick fix and <c>check --json</c> writes out:
/// a title for the editor's menu and the edits, all in the file the diagnostic points at.
/// </summary>
public sealed record SuggestedFix(string Title, IReadOnlyList<TextEdit> Edits)
{
    /// <summary>Two fixes are the same when their titles and edits are, so a diagnostic reported twice is kept once.</summary>
    public bool Equals(SuggestedFix? other)
    {
        return other != null && Title == other.Title && Edits.SequenceEqual(second: other.Edits);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return Edits.Aggregate(seed: Title.GetHashCode(), func: (hash, edit) => HashCode.Combine(value1: hash, value2: edit));
    }
}
