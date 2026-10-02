using System.Text;

namespace Builder.Formatting;

/// <summary>
/// A layout document for one logical line of output (a statement, a header), rendered at a fixed indent within the
/// line limit. Text never breaks. A <see cref="Group"/> is laid out flat when it fits on the rest of the line, and
/// otherwise every <see cref="Line"/> and <see cref="SoftLine"/> directly inside it breaks onto a new line at the
/// current nesting (Wadler's pretty printer, as Prettier implements it).
/// </summary>
internal abstract record Doc
{
    /// <summary>The empty document.</summary>
    public static readonly Doc Empty = new TextDoc(Value: "");

    /// <summary>A space when flat, a line break when the group breaks.</summary>
    public static readonly Doc Line = new LineDoc(Soft: false);

    /// <summary>Nothing when flat, a line break when the group breaks.</summary>
    public static readonly Doc SoftLine = new LineDoc(Soft: true);

    /// <summary>Literal text (never contains a line break).</summary>
    public static Doc Text(string text)
    {
        return new TextDoc(Value: text);
    }

    /// <summary>The documents one after another.</summary>
    public static Doc Concat(params Doc[] parts)
    {
        return new ConcatDoc(Parts: parts);
    }

    /// <summary>The documents one after another.</summary>
    public static Doc Concat(IEnumerable<Doc> parts)
    {
        return new ConcatDoc(Parts: parts.ToArray());
    }

    /// <summary>Lines inside <paramref name="content"/> break to the current nesting plus <paramref name="indent"/>.</summary>
    public static Doc Nest(int indent, Doc content)
    {
        return new NestDoc(Indent: indent, Content: content);
    }

    /// <summary>A unit laid out flat when it fits, broken otherwise.</summary>
    public static Doc Group(Doc content)
    {
        return new GroupDoc(Content: content);
    }

    /// <summary>A unit that is always laid out broken, and so breaks every group around it.</summary>
    public static Doc BrokenGroup(Doc content)
    {
        return new GroupDoc(Content: content, AlwaysBroken: true);
    }

    /// <summary><paramref name="broken"/> inside a broken group, <paramref name="flat"/> inside a flat one.</summary>
    public static Doc IfBreak(Doc broken, Doc flat)
    {
        return new IfBreakDoc(Broken: broken, WhenFlat: flat);
    }

    /// <summary>The flat text of this document (every group flat).</summary>
    public string Flat()
    {
        var text = new StringBuilder();
        AppendFlat(doc: this, text: text);
        return text.ToString();
    }

    private static void AppendFlat(Doc doc, StringBuilder text)
    {
        switch (doc)
        {
            case TextDoc t:
                text.Append(value: t.Value);
                break;
            case ConcatDoc c:
                foreach (Doc part in c.Parts)
                {
                    AppendFlat(doc: part, text: text);
                }

                break;
            case NestDoc n:
                AppendFlat(doc: n.Content, text: text);
                break;
            case GroupDoc g:
                AppendFlat(doc: g.Content, text: text);
                break;
            case LineDoc l:
                if (!l.Soft)
                {
                    text.Append(value: ' ');
                }

                break;
            case IfBreakDoc i:
                AppendFlat(doc: i.WhenFlat, text: text);
                break;
        }
    }
}

/// <summary>Literal text.</summary>
internal sealed record TextDoc(string Value) : Doc;

/// <summary>A sequence.</summary>
internal sealed record ConcatDoc(Doc[] Parts) : Doc;

/// <summary>Extra indentation for the breaks inside.</summary>
internal sealed record NestDoc(int Indent, Doc Content) : Doc;

/// <summary>A unit that breaks as a whole.</summary>
internal sealed record GroupDoc(Doc Content, bool AlwaysBroken = false) : Doc;

/// <summary>A possible line break.</summary>
internal sealed record LineDoc(bool Soft) : Doc;

/// <summary>Text that depends on whether the enclosing group broke.</summary>
internal sealed record IfBreakDoc(Doc Broken, Doc WhenFlat) : Doc;

/// <summary>Renders a <see cref="Doc"/> into lines within a width.</summary>
internal static class DocRenderer
{
    private enum Mode
    {
        Flat,
        Break
    }

    private readonly record struct Command(int Indent, Mode Mode, Doc Doc);

    /// <summary>
    /// The lines of <paramref name="doc"/> laid out at <paramref name="indent"/> spaces within
    /// <paramref name="width"/> columns. The first line is returned without its indent (the caller writes it), and
    /// every following line carries its full indent.
    /// </summary>
    public static List<string> Render(Doc doc, int indent, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        int column = indent;
        var stack = new List<Command> { new(Indent: indent, Mode: Mode.Break, Doc: Doc.Group(content: doc)) };

        while (stack.Count > 0)
        {
            Command command = stack[^1];
            stack.RemoveAt(index: stack.Count - 1);
            switch (command.Doc)
            {
                case TextDoc t:
                    current.Append(value: t.Value);
                    column += t.Value.Length;
                    break;
                case ConcatDoc c:
                    for (int i = c.Parts.Length - 1; i >= 0; i--)
                    {
                        stack.Add(item: command with { Doc = c.Parts[i] });
                    }

                    break;
                case NestDoc n:
                    stack.Add(item: new Command(Indent: command.Indent + n.Indent, Mode: command.Mode, Doc: n.Content));
                    break;
                case GroupDoc { AlwaysBroken: true } forced:
                    stack.Add(item: command with { Mode = Mode.Break, Doc = forced.Content });
                    break;
                case GroupDoc g:
                    if (command.Mode == Mode.Flat)
                    {
                        stack.Add(item: command with { Doc = g.Content });
                    }
                    else
                    {
                        var flat = new Command(Indent: command.Indent, Mode: Mode.Flat, Doc: g.Content);
                        stack.Add(item: Fits(next: flat, rest: stack, room: width - column)
                            ? flat
                            : command with { Doc = g.Content });
                    }

                    break;
                case LineDoc l:
                    if (command.Mode == Mode.Flat)
                    {
                        if (!l.Soft)
                        {
                            current.Append(value: ' ');
                            column++;
                        }
                    }
                    else
                    {
                        lines.Add(item: current.ToString());
                        current.Clear();
                        current.Append(value: ' ', repeatCount: command.Indent);
                        column = command.Indent;
                    }

                    break;
                case IfBreakDoc i:
                    stack.Add(item: command with
                    {
                        Doc = command.Mode == Mode.Break
                            ? i.Broken
                            : i.WhenFlat
                    });
                    break;
            }
        }

        lines.Add(item: current.ToString());
        return lines;
    }

    /// <summary>
    /// Whether <paramref name="next"/> laid out flat, followed by the commands still waiting (in their own modes) up
    /// to the next line break, fits in <paramref name="room"/> columns.
    /// </summary>
    private static bool Fits(Command next, List<Command> rest, int room)
    {
        var stack = new List<Command> { next };
        int restIndex = rest.Count;
        while (room >= 0)
        {
            if (stack.Count == 0)
            {
                if (restIndex == 0)
                {
                    return true;
                }

                stack.Add(item: rest[--restIndex]);
                continue;
            }

            Command command = stack[^1];
            stack.RemoveAt(index: stack.Count - 1);
            switch (command.Doc)
            {
                case TextDoc t:
                    room -= t.Value.Length;
                    break;
                case ConcatDoc c:
                    for (int i = c.Parts.Length - 1; i >= 0; i--)
                    {
                        stack.Add(item: command with { Doc = c.Parts[i] });
                    }

                    break;
                case NestDoc n:
                    stack.Add(item: command with { Doc = n.Content });
                    break;
                case GroupDoc g:
                    if (g.AlwaysBroken)
                    {
                        // A group that must break cannot be part of a flat layout. In a broken rest it ends the line.
                        if (command.Mode == Mode.Flat)
                        {
                            return false;
                        }
                    }

                    stack.Add(item: command with { Doc = g.Content });
                    break;
                case LineDoc l:
                    if (command.Mode == Mode.Break)
                    {
                        return true;
                    }

                    if (!l.Soft)
                    {
                        room--;
                    }

                    break;
                case IfBreakDoc i:
                    stack.Add(item: command with
                    {
                        Doc = command.Mode == Mode.Break
                            ? i.Broken
                            : i.WhenFlat
                    });
                    break;
            }
        }

        return false;
    }
}
