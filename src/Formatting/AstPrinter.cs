using System.Text;
using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Formatting;

/// <summary>
/// Prints a parsed file in the canonical layout. The tree carries no end positions and drops comments, so the
/// printer works in two passes over the same traversal. The first pass only records where each printed unit (a
/// declaration, a statement, a header line such as <c>else</c> or a <c>when</c> arm) starts in the source. The
/// comments are then placed by position: a comment alone on its line leads the next unit, a comment after code
/// trails the unit its line belongs to. The second pass prints, emitting each unit's comments with it, so a
/// comment moves with its declaration when declarations are reordered. Anything the printer cannot reproduce
/// exactly raises <see cref="FormatRefusedException"/>.
/// </summary>
internal sealed partial class AstPrinter
{
    private const int IndentWidth = 4;

    private readonly SourceIndex _index;

    /// <summary>True in the first pass: units are recorded and nothing is written.</summary>
    private bool _collecting;

    /// <summary>The printed depth of each unit start (first pass).</summary>
    private readonly Dictionary<int, int> _unitDepths = [];

    private readonly Dictionary<int, List<CommentTrivia>> _leading = [];
    private readonly Dictionary<int, CommentTrivia> _trailing = [];
    private readonly List<(CommentTrivia Comment, int Depth, int PreviousStart)> _blockTrailing = [];
    private readonly List<CommentTrivia> _endOfFile = [];
    private readonly HashSet<CommentTrivia> _emitted = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The start of the unit printed last (second pass), for placing comments that close a block.</summary>
    private int _lastPrintedStart = -1;

    /// <summary>The lexer's block depth at each doc comment, by position.</summary>
    private Dictionary<int, int>? _docDepths;

    private readonly List<string> _lines = [];

    /// <summary>The depth of the unit whose head is being built (a <c>when</c> expression's arms go one deeper).</summary>
    private int _currentDepth;

    /// <summary>Blocks a head expression defers until after its line (the arms of a <c>when</c> expression).</summary>
    private readonly List<Action> _deferredBlocks = [];

    /// <summary>Whether top-level declarations stay in source order instead of being grouped by kind.</summary>
    private readonly bool _keepOrder;

    /// <summary>
    /// Creates a printer over the indexed source of one file. With <paramref name="keepOrder"/> the top-level
    /// declarations keep their source order (a code example in documentation reads in the order it was written).
    /// </summary>
    public AstPrinter(SourceIndex index, bool keepOrder = false)
    {
        _index = index;
        _keepOrder = keepOrder;
    }

    /// <summary>
    /// The top-level nodes in the order they were printed, so the round-trip check can compare the reparsed tree
    /// with the original in that order. Null until <see cref="Print"/> runs.
    /// </summary>
    public IReadOnlyList<ISyntaxTreeNode>? TopLevelOrder { get; private set; }

    /// <summary>Prints <paramref name="program"/> and returns the formatted text.</summary>
    public string Print(SyntaxTree.Program program)
    {
        _collecting = true;
        PrintProgram(program: program);

        PlaceComments();

        _collecting = false;
        _lines.Clear();
        _lastPrintedStart = -1;
        PrintProgram(program: program);
        EmitEndOfFileComments();

        CommentTrivia? unplaced = _index.Comments.FirstOrDefault(predicate: c => !_emitted.Contains(item: c));
        if (unplaced != null)
        {
            throw Refuse(line: unplaced.Line,
                reason: $"the comment '{Shorten(text: unplaced.Text)}' sits where the formatter cannot keep it");
        }

        while (_lines.Count > 0 && _lines[^1].Length == 0)
        {
            _lines.RemoveAt(index: _lines.Count - 1);
        }

        var text = new StringBuilder();
        foreach (string line in _lines)
        {
            text.Append(value: line.TrimEnd(trimChar: ' '))
                .Append(value: '\n');
        }

        return text.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // REFUSAL
    // ═══════════════════════════════════════════════════════════════════════════

    private FormatRefusedException Refuse(SourceLocation? at, string reason)
    {
        return Refuse(line: at?.Line ?? 0, reason: reason);
    }

    private FormatRefusedException Refuse(int line, string reason)
    {
        return new FormatRefusedException(reason: line > 0
            ? $"line {line}: {reason}"
            : reason);
    }

    private FormatRefusedException Unsupported(object node, SourceLocation? at)
    {
        return Refuse(at: at, reason: $"the formatter does not print a {node.GetType().Name}");
    }

    private static string Shorten(string text)
    {
        return text.Length <= 40
            ? text
            : text[..40] + "...";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UNITS AND COMMENTS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The source start of the logical line holding <paramref name="location"/>.</summary>
    private int LineStart(SourceLocation location)
    {
        return _index.LineStartPosition(position: location.Position);
    }

    /// <summary>The source start of a declaration's unit (its annotation lines included).</summary>
    private int DeclarationStart(SourceLocation keyword)
    {
        return _index.Tokens[index: _index.DeclarationStartIndex(keywordPosition: keyword.Position)].Position;
    }

    /// <summary>Records a unit (first pass).</summary>
    private void RegisterUnit(int start, int depth)
    {
        if (_collecting)
        {
            _unitDepths.TryAdd(key: start, value: depth);
        }
    }

    /// <summary>
    /// Assigns every comment to a unit. A comment alone on its line leads the next unit, except one that closes a
    /// block: a doc comment written deeper than that unit (the lexer reads a dedented <c>###</c> as closing
    /// blocks), and any comment written after a block's last statement at that block's indent. Those stay at the
    /// end of their block. A comment after code trails the unit it follows.
    /// </summary>
    private void PlaceComments()
    {
        int[] starts = _unitDepths.Keys.Order()
                                  .ToArray();
        foreach (CommentTrivia comment in _index.Comments)
        {
            int next = Array.BinarySearch(array: starts, value: comment.Position);
            int nextIndex = next >= 0
                ? next
                : ~next;
            if (_index.IsOwnLine(comment: comment))
            {
                if (comment.IsDoc && nextIndex > 0 && nextIndex < starts.Length &&
                    DocDepth(comment: comment) > _unitDepths[key: starts[nextIndex]])
                {
                    _blockTrailing.Add(item: (comment, DocDepth(comment: comment), starts[nextIndex - 1]));
                    continue;
                }

                if (!comment.IsDoc && BlockOwning(comment: comment, starts: starts, nextIndex: nextIndex) is { } depth)
                {
                    _blockTrailing.Add(item: (comment, depth, starts[nextIndex - 1]));
                    continue;
                }

                if (nextIndex >= starts.Length)
                {
                    _endOfFile.Add(item: comment);
                    continue;
                }

                if (!_leading.TryGetValue(key: starts[nextIndex], value: out List<CommentTrivia>? list))
                {
                    list = [];
                    _leading[key: starts[nextIndex]] = list;
                }

                list.Add(item: comment);
                continue;
            }

            int previousIndex = nextIndex - 1;
            if (previousIndex < 0)
            {
                throw Refuse(line: comment.Line,
                    reason: $"the comment '{Shorten(text: comment.Text)}' follows code the formatter does not print");
            }

            if (!_trailing.TryAdd(key: starts[previousIndex], value: comment))
            {
                throw Refuse(line: comment.Line,
                    reason: "several comments sit inside one statement or header that the formatter prints as one line");
            }
        }
    }

    /// <summary>
    /// For a comment-only line written after the last statement of a block, at that block's indent: the printed
    /// depth of the block, so the comment stays in it rather than taking the indent of the code that follows.
    /// Null when the comment leads the next unit (the next code line is at its indent or deeper, or no open block
    /// is written at the comment's column).
    /// </summary>
    private int? BlockOwning(CommentTrivia comment, int[] starts, int nextIndex)
    {
        if (nextIndex == 0)
        {
            return null;
        }

        int nextColumn = nextIndex < starts.Length
            ? ColumnOf(start: starts[nextIndex])
            : 1;
        if (nextColumn >= comment.Column)
        {
            return null;
        }

        for (int k = nextIndex - 1; k >= 0; k--)
        {
            int column = ColumnOf(start: starts[k]);
            if (column == comment.Column)
            {
                return _unitDepths[key: starts[k]];
            }

            if (column < comment.Column)
            {
                return null;
            }
        }

        return null;
    }

    private int ColumnOf(int start)
    {
        return _index.Tokens[index: _index.TokenIndexAt(position: start)].Column;
    }

    /// <summary>The block depth the lexer gives a doc comment (it reads <c>###</c> lines for indentation).</summary>
    private int DocDepth(CommentTrivia comment)
    {
        if (_docDepths == null)
        {
            _docDepths = [];
            int depth = 0;
            foreach (Token token in _index.Tokens)
            {
                switch (token.Type)
                {
                    case TokenType.Indent:
                        depth++;
                        break;
                    case TokenType.Dedent:
                        depth--;
                        break;
                    case TokenType.DocComment:
                        _docDepths.TryAdd(key: token.Position, value: depth);
                        break;
                }
            }
        }

        return _docDepths.TryGetValue(key: comment.Position, value: out int found)
            ? found
            : 0;
    }

    /// <summary>How blank lines around an item are decided.</summary>
    private enum BlankPolicy
    {
        /// <summary>A blank line where the source had one or more (never first in a block).</summary>
        Preserve,

        /// <summary>No blank line before the item's comments, the source's blank lines kept among them.</summary>
        NoneBefore,

        /// <summary>No blank line at all (a header that continues a statement: <c>else</c>, <c>needs</c>).</summary>
        None,

        /// <summary>Exactly one blank line before the item and its comments.</summary>
        One
    }

    /// <summary>
    /// Writes what comes before a unit: the blank-line separator and the unit's leading comments, each comment at
    /// the unit's indent (doc comments where the lexer reads them).
    /// </summary>
    /// <param name="start">The unit's source start.</param>
    /// <param name="depth">The unit's printed depth.</param>
    /// <param name="first">Whether the unit is first in its block (no blank line before it).</param>
    /// <param name="policy">How blank lines are decided before the comments and the unit.</param>
    private void EmitPrefix(int start, int depth, bool first, BlankPolicy policy)
    {
        _currentDepth = depth;
        RegisterUnit(start: start, depth: depth);
        if (_collecting)
        {
            return;
        }

        _leading.TryGetValue(key: start, value: out List<CommentTrivia>? comments);
        comments = comments?.Where(predicate: c => !_emitted.Contains(item: c))
                            .ToList();
        int unitLine = _index.Tokens[index: _index.TokenIndexAt(position: start)].Line;
        int firstLine = comments is { Count: > 0 }
            ? comments[index: 0].Line
            : unitLine;

        switch (policy)
        {
            case BlankPolicy.One when !first:
                WriteBlank();
                break;
            case BlankPolicy.Preserve when !first && _index.IsBlankLine(line: firstLine - 1):
                WriteBlank();
                break;
        }

        if (comments is not { Count: > 0 })
        {
            return;
        }

        for (int i = 0; i < comments.Count; i++)
        {
            CommentTrivia comment = comments[index: i];
            if (i > 0 && policy != BlankPolicy.None && _index.IsBlankLine(line: comment.Line - 1))
            {
                WriteBlank();
            }

            WriteLine(indent: depth * IndentWidth, text: CommentText.Normalize(text: comment.Text));
            _emitted.Add(item: comment);
        }

        CommentTrivia last = comments[^1];
        if (!last.IsDoc && policy != BlankPolicy.None && _index.IsBlankLine(line: unitLine - 1))
        {
            WriteBlank();
        }
    }

    /// <summary>
    /// Writes a unit's head: <paramref name="head"/> laid out at <paramref name="depth"/>, the trailing comment of
    /// the unit on its last line, then any block the head deferred (a <c>when</c> expression's arms).
    /// </summary>
    private void EmitHead(int start, int depth, Doc head)
    {
        RegisterUnit(start: start, depth: depth);
        List<Action> deferred = [.. _deferredBlocks];
        _deferredBlocks.Clear();

        if (!_collecting)
        {
            List<string> rendered = DocRenderer.Render(doc: head,
                indent: depth * IndentWidth,
                width: SourceFormatter.MaxLineWidth);
            WriteLine(indent: depth * IndentWidth, text: rendered[index: 0]);
            for (int i = 1; i < rendered.Count; i++)
            {
                _lines.Add(item: rendered[index: i]);
            }

            if (_trailing.TryGetValue(key: start, value: out CommentTrivia? trailing) &&
                _emitted.Add(item: trailing))
            {
                _lines[^1] = _lines[^1]
                            .TrimEnd() + "  " + CommentText.Normalize(text: trailing.Text);
            }

            _lastPrintedStart = start;
        }

        foreach (Action block in deferred)
        {
            block();
        }
    }

    /// <summary>
    /// Writes the doc comments that close a block at <paramref name="depth"/>: written after its last statement
    /// and before code at a lower indent.
    /// </summary>
    private void EmitBlockTrailing(int depth)
    {
        if (_collecting)
        {
            return;
        }

        foreach ((CommentTrivia comment, int commentDepth, int previousStart) in _blockTrailing)
        {
            if (commentDepth < depth || _emitted.Contains(item: comment) || previousStart != _lastPrintedStart)
            {
                continue;
            }

            if (_index.IsBlankLine(line: comment.Line - 1))
            {
                WriteBlank();
            }

            WriteLine(indent: depth * IndentWidth, text: CommentText.Normalize(text: comment.Text));
            _emitted.Add(item: comment);
        }
    }

    private void EmitEndOfFileComments()
    {
        var depths = new Dictionary<CommentTrivia, int>(comparer: ReferenceEqualityComparer.Instance);
        foreach ((CommentTrivia comment, int depth, int _) in _blockTrailing.Where(predicate: c => !_emitted.Contains(item: c.Comment)))
        {
            _endOfFile.Add(item: comment);
            depths[key: comment] = depth;
        }

        foreach (CommentTrivia comment in _endOfFile.OrderBy(keySelector: c => c.Position))
        {
            if (_index.IsBlankLine(line: comment.Line - 1))
            {
                WriteBlank();
            }

            int depth = depths.TryGetValue(key: comment, value: out int blockDepth)
                ? blockDepth
                : comment.IsDoc
                    ? Math.Max(val1: 0, val2: DocDepth(comment: comment))
                    : 0;
            WriteLine(indent: depth * IndentWidth, text: CommentText.Normalize(text: comment.Text));
            _emitted.Add(item: comment);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OUTPUT
    // ═══════════════════════════════════════════════════════════════════════════

    private void WriteLine(int indent, string text)
    {
        if (_collecting)
        {
            return;
        }

        _lines.Add(item: new string(c: ' ', count: indent) + text);
    }

    private void WriteBlank()
    {
        if (_collecting || _lines.Count == 0 || _lines[^1].Length == 0)
        {
            return;
        }

        _lines.Add(item: "");
    }

    /// <summary>Writes source lines exactly as they are (an <c>@target</c> directive).</summary>
    private void WriteVerbatim(string text)
    {
        if (_collecting)
        {
            return;
        }

        foreach (string line in text.Split(separator: '\n'))
        {
            _lines.Add(item: line.TrimEnd());
        }
    }
}
