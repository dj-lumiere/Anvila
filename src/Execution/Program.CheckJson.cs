using System.Text.Json;
using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Parser;
using Builder.Verification;
using Builder.Verification.Results;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary><c>check --json</c>: the check's diagnostics as one JSON array on standard output.</summary>
internal partial class Program
{
    /// <summary>
    /// Checks <paramref name="entryFile"/> like <c>check</c> and writes every diagnostic of the user's files as a JSON
    /// array: file, line, column, severity (<c>error</c>, <c>warning</c> or <c>note</c>), code, message, the last line
    /// when it covers several, and the fix it proposes (a title and edits with 1-based lines and columns), so a tool
    /// applies a fix without reading the message. Returns 1 when there is an error.
    /// </summary>
    private static int CheckJson(string entryFile, string? projectRoot, IReadOnlyList<string>? libraryRoots)
    {
        var diagnostics = new List<Dictionary<string, object?>>();
        Language language = SourceLanguage(path: entryFile);
        string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();
        var driver = new BuildDriver(projectRoot: projectRoot ?? Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".",
            stdlibRoot: stdlibRoot,
            language: language,
            libraryRoots: libraryRoots);
        BuildResult built = driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));
        diagnostics.AddRange(collection: built.Errors.Select(selector: e => Entry(diagnostic: e)));
        diagnostics.AddRange(collection: built.Warnings.Select(selector: w => new Dictionary<string, object?>
        {
            [key: "file"] = ShownFile(path: w.FileName), [key: "line"] = w.Line, [key: "column"] = w.Column,
            [key: "severity"] = "warning", [key: "code"] = w.WarningCode, [key: "message"] = w.Message
        }));
        if (built.Errors.Count == 0)
        {
            var analyzer = new SemanticVerifier(language: language);
            analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: OrderUserFiles(
                userUnits: FilterUserUnits(buildResult: built, stdlibRoot: stdlibRoot),
                initializationOrder: built.InitializationOrder));
            diagnostics.AddRange(collection: result.Errors.Select(selector: e => Entry(diagnostic: e)));
            diagnostics.AddRange(collection: result.Warnings.Select(selector: w => Entry(diagnostic: w)));
            diagnostics.AddRange(collection: result.Notices.Select(selector: n => Entry(diagnostic: n)));
        }

        Console.WriteLine(value: JsonSerializer.Serialize(value: diagnostics,
            options: new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        return diagnostics.Any(predicate: d => (string?)d[key: "severity"] == "error") ? 1 : 0;
    }

    /// <summary>A file as the entry names it: relative to the working folder when it is inside it.</summary>
    private static string ShownFile(string path)
    {
        if (string.IsNullOrEmpty(value: path))
        {
            return path;
        }

        string relative = Path.GetRelativePath(relativeTo: Directory.GetCurrentDirectory(), path: Path.GetFullPath(path: path));
        return relative.StartsWith(value: "..", comparisonType: StringComparison.Ordinal) ? path : relative;
    }

    private static Dictionary<string, object?> Entry(SemanticError diagnostic)
    {
        return Entry(location: diagnostic.Location, severity: "error", code: diagnostic.CodeString,
            message: diagnostic.SurfaceMessage, endLine: diagnostic.EndLine, fix: diagnostic.Fix);
    }

    private static Dictionary<string, object?> Entry(SemanticWarning diagnostic)
    {
        return Entry(location: diagnostic.Location, severity: diagnostic.IsNotice ? "note" : "warning",
            code: diagnostic.CodeString, message: diagnostic.SurfaceMessage, endLine: diagnostic.EndLine,
            fix: diagnostic.Fix);
    }

    private static Dictionary<string, object?> Entry(SyntaxTree.SourceLocation location, string severity, string code,
        string message, int? endLine, SuggestedFix? fix)
    {
        var entry = new Dictionary<string, object?>
        {
            [key: "file"] = ShownFile(path: location.FileName), [key: "line"] = location.Line, [key: "column"] = location.Column,
            [key: "severity"] = severity, [key: "code"] = code, [key: "message"] = message
        };
        if (endLine is { } last)
        {
            entry[key: "endLine"] = last;
        }

        if (fix != null)
        {
            entry[key: "fix"] = new Dictionary<string, object?>
            {
                [key: "title"] = fix.Title,
                [key: "edits"] = fix.Edits.Select(selector: e => new Dictionary<string, object?>
                {
                    [key: "startLine"] = e.StartLine, [key: "startColumn"] = e.StartColumn,
                    [key: "endLine"] = e.EndLine, [key: "endColumn"] = e.EndColumn, [key: "newText"] = e.NewText
                }).ToList()
            };
        }

        return entry;
    }
}
