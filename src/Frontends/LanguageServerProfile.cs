using TypeModel.Enums;

namespace Builder.Frontends;

/// <summary>
/// What one language's Language Server says and serves: the shared engine (<c>Builder.Execution.LspServer</c>)
/// does the protocol, the documents and the analysis, and asks the profile for everything that is the
/// language's own. RazorForge and Suflae each run their own server (<c>razorforge lsp</c>, <c>suflae lsp</c>), and
/// an editor routes each file to the server of its extension.
/// </summary>
public abstract class LanguageServerProfile
{
    /// <summary>The language this server serves.</summary>
    public abstract Language Language { get; }

    /// <summary>The server's name in the <c>initialize</c> reply and in diagnostics' <c>source</c>.</summary>
    public abstract string ServerName { get; }

    /// <summary>The fenced-code language id hover text uses, so an editor highlights it as this language.</summary>
    public abstract string CodeBlockLanguage { get; }

    /// <summary>The keywords completion offers: exactly the words the language's lexer reserves.</summary>
    public abstract IReadOnlyList<string> Keywords { get; }

    /// <summary>
    /// Whether this server serves the document: a file of its own language. A file of the other language
    /// belongs to the other server, so this one neither analyzes nor reports on it.
    /// </summary>
    public bool Serves(string uriOrFileName)
    {
        return uriOrFileName.EndsWith(value: Languages.For(language: Language).FileExtension,
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The document formatted in the language's canonical layout, or null when the server cannot format it (the
    /// formatter refuses a file it cannot reproduce exactly). Null until the language's formatter is wired in.
    /// </summary>
    public virtual string? Format(string text, string fileName)
    {
        return null;
    }
}
