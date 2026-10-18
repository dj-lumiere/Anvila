using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification.Results;

/// <summary>
/// Represents a semantic error during analysis.
/// </summary>
/// <param name="Code">The diagnostic code for this error.</param>
/// <param name="Message">The error message.</param>
/// <param name="Location">The source location of the error.</param>
public sealed record SemanticError(
    SemanticDiagnosticCode Code,
    string Message,
    SourceLocation Location)
{
    /// <summary>The last line of the code it covers, when it covers more than its first line (the body of a loop
    /// that never runs), or null.</summary>
    public int? EndLine { get; init; }

    /// <summary>Whether the code it covers never runs: an editor shows that code faded.</summary>
    public bool NeverRuns { get; init; }

    /// <summary>The change it proposes, offered as a quick fix, or null.</summary>
    public SuggestedFix? Fix { get; init; }

    /// <summary>
    /// The code as the user sees it, spelled in the language of the file the error points at
    /// (RF-S### for a .rf file, SF-S### for a .sf file).
    /// </summary>
    public string CodeString => GrammarCode ?? Code.ToCodeString(language: DiagnosticLanguage.OfFile(fileName: Location.FileName));

    /// <summary>
    /// The grammar code (RF-G###) of a parse error carried as a build error, shown in place of the semantic code.
    /// </summary>
    public string? GrammarCode { get; init; }

    /// <summary>The message in the words of the language of the file it points at (see
    /// <see cref="DiagnosticLanguage.Surface"/>).</summary>
    public string SurfaceMessage => DiagnosticLanguage.Surface(message: Message, fileName: Location.FileName);

    /// <summary>
    /// Gets the formatted error message including diagnostic code and location.
    /// Format: error[RF-S###]: filename:line:column: message (SF-S### for a Suflae file)
    /// </summary>
    public string FormattedMessage =>
        $"error[{CodeString}]: {Location.FileName}:{Location.Line}:{Location.Column}: {SurfaceMessage}";
}
