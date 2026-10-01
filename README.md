<p align="center">
  <img src="branding/anvila.svg" alt="Anvila logo" width="112">
</p>

<h1 align="center">Anvila</h1>

<p align="center"><strong>The builder core behind RazorForge and Suflae.</strong></p>

Anvila is the C# library that turns [RazorForge](https://github.com/dj-lumiere/RazorForge) and
[Suflae](https://github.com/dj-lumiere/Suflae) source into native executables: parsing, name and
type resolution, semantic analysis, lowering, monomorphization, LLVM IR emission (directly, or through
[Tessera](https://github.com/dj-lumiere/Tessera) source with `[target] backend = "tessera"`), and the build
driver. It also holds the warm-build daemon, the JIT dev loop, and the language server.

Anvila has no lexer and no command line of its own. Each language project registers its lexer and
language rules at startup and ships its own executable (`razorforge`, `suflae`).

## Pipeline

Source folders are numbered by the pipeline stage they mainly serve:

| Folder               | Stage                                                               |
|----------------------|---------------------------------------------------------------------|
| `src/1.Tokenizer`    | Token model shared by the language lexers                           |
| `src/2.Parser`       | Parsing                                                             |
| `src/3.Declaration`  | Declaration collection, name and type resolution                    |
| `src/4.Desugaring`   | Syntactic, type-independent desugaring (runs before analysis)       |
| `src/5.Verification` | Semantic analysis and diagnostics                                   |
| `src/6.Lowering`     | Type-aware lowering (runs after analysis)                           |
| `src/7.Instantiation`| Monomorphization and synthesized routines                           |
| `src/8.Collection`   | Demand collection: only code reachable from `start()` is built      |
| `src/9.LlvmEmit`     | LLVM IR emission                                                    |
| `src/9.TesseraEmit`  | Tessera backend: Tessera source for Tessera's builder to build      |

Unnumbered folders (`SyntaxTree`, `TypeModel`, `Diagnostics`, `BuildSystem`, `Execution`, …) support
every stage.

## Building

Anvila is built as part of the workspace: clone Anvila, [Ingrid](https://github.com/dj-lumiere/Ingrid),
RazorForge, and Suflae side by side and build one of the language projects. Building Anvila also
builds Ingrid's C APIs. The steps are in the
[RazorForge README](https://github.com/dj-lumiere/RazorForge#from-source).

CI for all four repositories runs from this one: `.github/workflows/workspace.yaml` checks out the
workspace, builds the runtime, and runs both languages' test suites on Linux, Windows, and macOS.

## License

MIT; see [`LICENSE`](LICENSE).
