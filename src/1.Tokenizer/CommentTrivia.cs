namespace Builder.Tokenizer;

/// <summary>
/// A comment as written: where it starts and its whole text from the first <c>#</c> to the end of the line. The
/// lexer drops a <c>#</c> comment from the token stream (a <c>###</c> doc comment stays as a token as well), so a
/// tool that reproduces the source, the formatter, reads them from here.
/// </summary>
/// <param name="Line">The 1-based line the comment is on.</param>
/// <param name="Column">The 1-based column of its first <c>#</c>.</param>
/// <param name="Text">The comment from its first <c>#</c> to the end of the line.</param>
/// <param name="IsDoc">Whether it is a <c>###</c> doc comment.</param>
/// <param name="Position">The offset of its first <c>#</c> in the lexed source.</param>
public sealed record CommentTrivia(int Line, int Column, string Text, bool IsDoc, int Position);
