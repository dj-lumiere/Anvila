using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification.Results;

/// <summary>
/// Represents a semantic warning during analysis.
/// </summary>
/// <param name="Code">The diagnostic code for this warning.</param>
/// <param name="Message">The warning message.</param>
/// <param name="Location">The source location of the warning.</param>
public sealed record SemanticWarning(
    SemanticWarningCode Code,
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

    /// <summary>Whether this is a notice (a likely slip that builds as written) rather than a warning.</summary>
    public bool IsNotice { get; init; }

    /// <summary>
    /// The code as the user sees it, spelled in the language of the file the warning points at
    /// (RF-W### for a .rf file, SF-W### for a .sf file).
    /// </summary>
    public string CodeString => Code.ToCodeString(language: DiagnosticLanguage.OfFile(fileName: Location.FileName));

    /// <summary>The message in the words of the language of the file it points at (see
    /// <see cref="DiagnosticLanguage.Surface"/>).</summary>
    public string SurfaceMessage => DiagnosticLanguage.Surface(message: Message, fileName: Location.FileName);

    /// <summary>
    /// Gets the formatted warning message including diagnostic code and location.
    /// Format: warning[RF-W###]: filename:line:column: message (SF-W### for a Suflae file)
    /// </summary>
    public string FormattedMessage =>
        $"{(IsNotice ? "note" : "warning")}[{CodeString}]: {Location.FileName}:{Location.Line}:{Location.Column}: {SurfaceMessage}";
}
