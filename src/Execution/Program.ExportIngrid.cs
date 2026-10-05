using System.Text;
using Builder.Backends;
using Builder.Declaration;
using Builder.TesseraEmit;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The record an export file declares: its common routines are the exports.</summary>
    private const string IngridExportOwner = "Ingrid";

    /// <summary>What an export's C symbol is: this, then the routine's name (<c>Ingrid.d64_add</c> is
    /// <c>ingrid_d64_add</c>).</summary>
    private const string IngridExportPrefix = "ingrid_";

    /// <summary>Where a workspace keeps the export files (RazorForge's own source of Ingrid code).</summary>
    private static readonly string IngridExportSourceDir = Path.Combine(path1: "RazorForge", path2: "IngridExport");

    /// <summary>Where a workspace keeps the files the exports write (compiled with the rest of Ingrid's library).</summary>
    private static readonly string IngridGeneratedDir = Path.Combine(path1: "Ingrid", path2: "tessera", path3: "generated");

    /// <summary>
    /// Handles <c>export-ingrid [export files or directories] [--out dir]</c>: writes Ingrid library code from
    /// RazorForge source. Each export file is a RazorForge module that declares <c>record Ingrid</c>: its common
    /// routines are the exports (<c>Ingrid.d64_add</c> becomes the C symbol <c>ingrid_d64_add</c>), with the C ABI's
    /// shapes in their signatures (scalars, a 128-bit value as two U64 halves, a wide result in IngridWords). The
    /// record keeps them apart from the <c>C::ingrid_*</c> declarations the standard library calls them by. The file
    /// is built with the Tessera backend from a <c>start()</c> that calls each export, and the exports with every
    /// routine they reach become one Tessera file, <c>&lt;out&gt;/&lt;file stem&gt;.tess</c>: the exports under their
    /// C symbols, the rest private. With no file given, every export file of the workspace's
    /// <c>RazorForge/IngridExport</c> is written to <c>Ingrid/tessera/generated</c>.
    /// </summary>
    private static int ExportIngrid(string[] args)
    {
        var inputs = new List<string>();
        string? outDir = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
            {
                outDir = args[++i];
            }
            else if (args[i].StartsWith(value: '-'))
            {
                Console.Error.WriteLine(value: $"[export-ingrid] unknown option '{args[i]}'.");
                return 1;
            }
            else
            {
                inputs.Add(item: args[i]);
            }
        }

        if (inputs.Count == 0)
        {
            if (FindWorkspaceDir(relative: IngridExportSourceDir) is not { } sourceDir)
            {
                Console.Error.WriteLine(value: $"[export-ingrid] no '{IngridExportSourceDir}' in a directory above here.");
                return 1;
            }

            inputs.Add(item: sourceDir);
        }

        outDir ??= FindWorkspaceDir(relative: Path.GetDirectoryName(path: IngridGeneratedDir)!) is { } tesseraDir
            ? Path.Combine(path1: tesseraDir, path2: "generated")
            : null;
        if (outDir == null)
        {
            Console.Error.WriteLine(value: "[export-ingrid] no 'Ingrid/tessera' in a directory above here: pass --out.");
            return 1;
        }

        List<string> files = inputs.SelectMany<string, string>(selector: input => Directory.Exists(path: input)
                                       ? Directory.GetFiles(path: input, searchPattern: "*.rf")
                                                  .Order(comparer: StringComparer.Ordinal)
                                       : [input])
                                   .ToList();
        Directory.CreateDirectory(path: outDir);
        foreach (string file in files)
        {
            string target = Path.Combine(path1: outDir, path2: Path.GetFileNameWithoutExtension(path: file) + ".tess");
            try
            {
                File.WriteAllText(path: target, contents: ExportIngridFile(exportFile: file, outDir: outDir));
                Console.WriteLine(value: $"[export-ingrid] {Path.GetFullPath(path: target)}");
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
            {
                Console.Error.WriteLine(value: $"[export-ingrid] {file}: {ex.Message}");
                return 1;
            }
        }

        return 0;
    }

    /// <summary>The Tessera library one export file writes into <paramref name="outDir"/>.</summary>
    private static string ExportIngridFile(string exportFile, string outDir)
    {
        string source = File.ReadAllText(path: exportFile).Replace(oldValue: "\r\n", newValue: "\n");
        List<RoutineDeclaration> exports = ExportDeclarations(source: source, file: exportFile);
        if (exports.Count == 0)
        {
            throw new InvalidOperationException(message: $"it declares no common routine of a record {IngridExportOwner}.");
        }

        // The build reaches what start() calls: one call per export, each parameter a zero (the ABI's parameters
        // are numbers, Bools and pointers), in a danger block for the pointers.
        var driver = new StringBuilder(value: source.TrimEnd());
        driver.Append(value: "\n\nroutine start()\n    danger\n");
        foreach (RoutineDeclaration export in exports)
        {
            string arguments = string.Join(separator: ", ",
                values: export.Parameters.Select(selector: p => $"{p.Name}: {ZeroLiteral(type: p.Type)}"));
            driver.Append(value: $"        discard {IngridExportOwner}.{export.Name}({arguments})\n");
        }

        driver.Append(value: "    return\n");

        string workDir = Path.Combine(path1: Path.GetTempPath(), path2: $"rf-export-ingrid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path: workDir);
        try
        {
            string entry = Path.Combine(path1: workDir, path2: Path.GetFileName(path: exportFile));
            File.WriteAllText(path: entry, contents: driver.ToString());
            ResolvedEntry resolved = ResolveEntryFile(args: ["codegen", entry], needsOutputArg: false);
            if (resolved.EntryFile == null ||
                BuildToLazyJitInputs(entryFile: resolved.EntryFile, inputs: out LazyJitInputs? built,
                    config: resolved) != 0 || built == null)
            {
                throw new InvalidOperationException(message: "the export file does not build (see the errors above).");
            }

            List<(RoutineInfo, string)> roots = built.UserPrograms
                                                     .SelectMany(selector: p => ExportRoutines(program: p.Program))
                                                     .Select(selector: d => (d.ResolvedInfo ??
                                                                             throw new InvalidOperationException(
                                                                                 message: $"{d.Name} did not resolve."),
                                                          ExportSymbol(export: d)))
                                                     .ToList();
            var input = new BackendInput
            {
                UserPrograms = built.UserPrograms,
                StdlibPrograms = built.Result.Registry.StdlibPrograms,
                Registry = built.Result.Registry,
                Target = built.Target,
                BuildMode = built.BuildMode,
                SynthesizedBodies = built.Result.SynthesizedBodies,
                InstantiatedGenericBodies = built.Result.InstantiatedGenericBodies,
                LiveRoutineKeys = built.Result.LiveRoutineKeys,
                MaySuspendRoutineKeys = built.Result.MaySuspendRoutineKeys,
                EntryModule = built.EntryModule
            };
            string name = Path.GetFileName(path: exportFile);
            string header =
                $"// Generated by `RazorForge export-ingrid` from RazorForge/IngridExport/{name}. Do not edit: change the\n" +
                "// RazorForge source and export again. Each common routine of the file's record Ingrid is exported as\n" +
                "// ingrid_<its name>, and everything else is what they reach, private to this file.\n\n";
            return TesseraBackend.WriteLibrary(input: input,
                library: new TesseraLibrary(Exports: roots, Header: header,
                    SourceFile: file => ExportSourceName(file: file, entry: entry, exportFile: exportFile,
                        outDir: outDir)));
        }
        finally
        {
            try
            {
                Directory.Delete(path: workDir, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is harmless.
            }
        }
    }

    /// <summary>
    /// The name a generated file's <c>#source</c> lines give a source file of the export's build: its path from the
    /// output directory to the file in the workspace (the export file itself, or the standard library's source rather
    /// than the copy next to the builder), so debug information and crash places lead to the RazorForge source. A
    /// file the workspace doesn't hold is named alone.
    /// </summary>
    private static string ExportSourceName(string file, string entry, string exportFile, string outDir)
    {
        if (file.Length == 0)
        {
            return file;
        }

        string full = Path.GetFullPath(path: file);
        string? source = null;
        if (string.Equals(a: full, b: Path.GetFullPath(path: entry), comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            source = Path.GetFullPath(path: exportFile);
        }
        else if (FindWorkspaceDir(relative: Path.Combine(path1: "RazorForge", path2: "Standard")) is { } workspaceStdlib)
        {
            // The copy next to the builder keeps each language's library in a folder of its name
            // (Standard/RazorForge/Core/...), and the workspace keeps RazorForge's in RazorForge/Standard/Core/...
            string relative = Path.GetRelativePath(relativeTo: StdlibLoader.GetDefaultStdlibPath(), path: full);
            string[] parts = relative.Split(separator: [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                count: 2);
            if (!relative.StartsWith(value: "..", comparisonType: StringComparison.Ordinal) && parts.Length == 2 &&
                File.Exists(path: Path.Combine(path1: workspaceStdlib, path2: parts[1])))
            {
                source = Path.Combine(path1: workspaceStdlib, path2: parts[1]);
            }
        }

        return source == null
            ? Path.GetFileName(path: file)
            : Path.GetRelativePath(relativeTo: Path.GetFullPath(path: outDir), path: source)
                  .Replace(oldChar: '\\', newChar: '/');
    }

    /// <summary>The exports an export file declares: the common routines of its record Ingrid.</summary>
    private static List<RoutineDeclaration> ExportDeclarations(string source, string file)
    {
        List<Builder.Tokenizer.Token> tokens = Builder.Tokenizer.Lexers.Tokenize(source: source, fileName: file,
            language: Language.RazorForge);
        return ExportRoutines(program: new Builder.Parser.Parser(tokens: tokens, language: Language.RazorForge,
            fileName: file).Parse());
    }

    /// <summary>The common routines of a program's record Ingrid.</summary>
    private static List<RoutineDeclaration> ExportRoutines(SyntaxTree.Program program)
    {
        return program.Declarations
                      .OfType<RoutineDeclaration>()
                      .Where(predicate: d => d.IsCommon && d.OwnerName == IngridExportOwner)
                      .ToList();
    }

    /// <summary>An export's C symbol: <c>ingrid_</c> and the routine's name.</summary>
    private static string ExportSymbol(RoutineDeclaration export)
    {
        return IngridExportPrefix + export.Name;
    }

    /// <summary>A zero of a C ABI parameter type: <c>false</c>, <c>0.0</c> for a float, the null pointer for a
    /// <c>Hijacked[T]</c>, <c>0</c> otherwise.</summary>
    private static string ZeroLiteral(TypeExpression? type)
    {
        return type?.Name switch
        {
            "Bool" => "false",
            "B16" or "B32" or "B64" or "B128" => "0.0",
            "Hijacked" when type.GenericArguments is [var pointee] => $"hijacked_none[{TypeText(type: pointee)}]()",
            _ => "0"
        };
    }

    /// <summary>A type as it is written: its name, then its type arguments in brackets.</summary>
    private static string TypeText(TypeExpression type)
    {
        return type.GenericArguments is { Count: > 0 } arguments
            ? $"{type.Name}[{string.Join(separator: ", ", values: arguments.Select(selector: TypeText))}]"
            : type.Name;
    }

    /// <summary>The directory <paramref name="relative"/> names in the nearest directory above the builder or the
    /// current directory that has it, or null.</summary>
    private static string? FindWorkspaceDir(string relative)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (string? dir = start; dir != null; dir = Path.GetDirectoryName(path: dir))
            {
                string candidate = Path.Combine(path1: dir, path2: relative);
                if (Directory.Exists(path: candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
