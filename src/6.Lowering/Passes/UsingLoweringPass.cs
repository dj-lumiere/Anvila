using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Lowers <see cref="UsingStatement"/> to explicit <c>enter</c> / <c>exit</c> call sequences,
/// injecting <c>exit()</c> before every control-flow escape from the using body.
/// After this pass codegen sees only plain declarations, calls, and standard control flow.
///
/// <para>Transformation of <c>using x = resource { body }</c>:</para>
/// <code>
/// {
///   var __uf_N = resource
///   var x = __uf_N.enter()      // if enter returns a value
///   // OR: __uf_N.enter(); var x = __uf_N   // if enter is void
///   // OR: var x = __uf_N                     // if no enter
///   [body, with exit() injected before every escape]
///   __uf_N.exit()               // normal-path exit (unreachable if body always terminates)
/// }
/// </code>
///
/// <para>Escape injection rules (loopDepth = loops enclosing the point inside the using body):</para>
/// <list type="bullet">
///   <item><see cref="ReturnStatement"/>, <see cref="ThrowStatement"/>,
///         <see cref="VariantReturnStatement"/> — always inject.</item>
///   <item><see cref="BreakStatement"/>, <see cref="ContinueStatement"/> at loopDepth 0 — inject
///         (break/continue escapes the using's enclosing loop).</item>
///   <item>break/continue at loopDepth &gt; 0 — do not inject (exits a loop inside the using).</item>
/// </list>
///
/// <para>Nested usings are lowered bottom-up: the body is recursively lowered before the outer
/// using is processed, so inner <c>exit()</c> calls appear before outer ones in every escape path.</para>
/// </summary>
internal sealed class UsingLoweringPass(PostprocessingContext? ctx, TypeRegistry registry) : AstRewriter
{
    private int _tempCount;

    public UsingLoweringPass(PostprocessingContext ctx) : this(ctx: ctx, registry: ctx.Registry)
    {
    }

    /// <summary>
    /// Lowers every <c>using</c> of one routine body to its <c>enter</c>/<c>exit</c> calls ahead of the Phase-8 pass:
    /// Suflae does it before its entity lowering, so the scope's bindings are plain locals that lowering turns into
    /// shared handles like any other.
    /// </summary>
    public static Statement LowerBody(Statement body, TypeRegistry registry)
    {
        return new UsingLoweringPass(ctx: null, registry: registry).VisitStatement(stmt: body);
    }

    private string NextResTemp()
    {
        return $"__uf_{_tempCount++}";
    }

    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: registry, program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    public void RunOnVariantBodies()
    {
        BodyDispatch.RunOnVariantBodies(registry: registry, bodies: ctx!.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    /// <summary>
    /// The only node this pass rewrites: a <c>using</c> becomes its explicit enter/exit call sequence.
    /// All structural recursion (blocks, if/when/loop/danger bodies, nested usings) is supplied by
    /// <see cref="AstRewriter"/>.
    /// </summary>
    protected override Statement VisitUsing(UsingStatement s)
    {
        return LowerUsing(u: s);
    }

    private BlockStatement LowerUsing(UsingStatement u)
    {
        // Lower body first -> nested usings expand bottom-up.
        Statement loweredBody = VisitStatement(stmt: u.Body);

        TypeSymbol? resourceType = u.Resource.ResolvedType;
        SourceLocation loc = u.Location;

        string resTemp = NextResTemp();
        var resTempIdent = new IdentifierExpression(Name: resTemp, Location: loc)
        {
            ResolvedType = resourceType
        };

        RoutineInfo? enterMemberRoutine = resourceType != null
            ? registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "enter")
            : null;
        RoutineInfo? exitMemberRoutine = resourceType != null
            ? registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "exit")
            : null;

        var stmts = new List<Statement>();

        // var __uf_N = resource
        stmts.Add(item: MakeBinding(name: resTemp,
            value: u.Resource,
            type: resourceType,
            loc: loc));

        // Bind user's name via enter (or directly to the resource if no enter)
        EmitEnterBinding(stmts: stmts,
            u: u,
            enterMemberRoutine: enterMemberRoutine,
            resTempIdent: resTempIdent,
            resourceType: resourceType,
            loc: loc);
        EmitPartBindings(stmts: stmts, u: u, loc: loc);

        // Build the exit() call expression (reused for injection and normal exit)
        ExpressionStatement? exitCallStmt = MakeExitCallStmt(exitMemberRoutine: exitMemberRoutine,
            resTempIdent: resTempIdent,
            loc: loc);

        Statement body = exitCallStmt != null
            ? InjectExitBeforeEscapes(stmt: loweredBody, exitStmt: exitCallStmt, loopDepth: 0)
            : loweredBody;

        stmts.Add(item: body);

        // Normal-path exit — unreachable (and skipped by EmitBlock) if body always terminates.
        if (exitCallStmt != null)
        {
            stmts.Add(item: exitCallStmt);
        }

        return new BlockStatement(Statements: stmts, Location: loc);
    }

    // Bind user's name via enter (or directly to the resource if no enter).
    private static void EmitEnterBinding(List<Statement> stmts, UsingStatement u,
        RoutineInfo? enterMemberRoutine, IdentifierExpression resTempIdent, TypeSymbol? resourceType,
        SourceLocation loc)
    {
        if (enterMemberRoutine == null)
        {
            stmts.Add(item: MakeBinding(name: u.Name,
                value: resTempIdent,
                type: resourceType,
                loc: loc));
            return;
        }

        var enterCallee = new MemberExpression(
            Object: resTempIdent,
            MemberName: "enter",
            Location: loc);
        var enterCall = new CallExpression(Callee: enterCallee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = enterMemberRoutine, ResolvedType = enterMemberRoutine.ReturnType
        };

        bool returnsValue = enterMemberRoutine.ReturnType != null &&
                            enterMemberRoutine.ReturnType.Name != "None";

        if (returnsValue)
        {
            stmts.Add(item: MakeBinding(name: u.Name,
                value: enterCall,
                type: enterMemberRoutine.ReturnType,
                loc: loc));
        }
        else
        {
            stmts.Add(item: new ExpressionStatement(Expression: enterCall, Location: loc));
            stmts.Add(item: MakeBinding(name: u.Name,
                value: resTempIdent,
                type: resourceType,
                loc: loc));
        }
    }

    // `using r as (a, b)`: each name takes its item of the tuple the scope bound to the hidden name.
    private static void EmitPartBindings(List<Statement> stmts, UsingStatement u, SourceLocation loc)
    {
        if (u.Parts == null || stmts[^1] is not DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer.ResolvedType: TupleTypeSymbol tuple }
            })
        {
            return;
        }

        var whole = new IdentifierExpression(Name: u.Name, Location: loc) { ResolvedType = tuple };
        for (int i = 0; i < u.Parts.Count; i++)
        {
            TypeSymbol itemType = tuple.ElementTypes[index: i];
            var item = new MemberExpression(Object: whole, MemberName: $"item{i}", Location: loc)
            {
                ResolvedType = itemType
            };
            stmts.Add(item: MakeBinding(name: u.Parts[index: i], value: item, type: itemType, loc: loc));
        }
    }

    // Build the exit() call statement (reused for injection and normal exit), or null when the
    // resource type has no exit memberRoutine.
    private static ExpressionStatement? MakeExitCallStmt(RoutineInfo? exitMemberRoutine,
        IdentifierExpression resTempIdent, SourceLocation loc)
    {
        if (exitMemberRoutine == null)
        {
            return null;
        }

        var exitCallee = new MemberExpression(
            Object: resTempIdent,
            MemberName: "exit",
            Location: loc);
        var exitCall = new CallExpression(Callee: exitCallee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = exitMemberRoutine, ResolvedType = null
        };
        return new ExpressionStatement(Expression: exitCall, Location: loc);
    }

    /// <summary>
    /// Puts <paramref name="exitStmt"/> before every way out of <paramref name="stmt"/> that leaves the scope: every
    /// <c>return</c>, <c>throw</c> and variant return, and every <c>break</c> / <c>continue</c> that isn't inside a loop
    /// of the scope's own (<paramref name="loopDepth"/> counts those). The access blocks reuse it to give up their
    /// accesses.
    /// </summary>
    internal static Statement InjectExitBeforeEscapes(Statement stmt, Statement exitStmt, int loopDepth)
    {
        return InjectExit(stmt: stmt, exitStmt: exitStmt, loopDepth: loopDepth);
    }

    private static Statement InjectExit(Statement stmt, Statement exitStmt,
        int loopDepth)
    {
        switch (stmt)
        {
            case ReturnStatement:
            case ThrowStatement:
            case VariantReturnStatement:
                return MakeBlock(stmts: [exitStmt, stmt], loc: stmt.Location);

            case BreakStatement:
            case ContinueStatement:
                return loopDepth == 0
                    ? MakeBlock(stmts: [exitStmt, stmt], loc: stmt.Location)
                    : stmt;

            case BlockStatement b:
                return InjectIntoBlock(b: b, exitStmt: exitStmt, loopDepth: loopDepth);

            case IfStatement ifs:
                return InjectIntoIf(ifs: ifs, exitStmt: exitStmt, loopDepth: loopDepth);

            case WhenStatement w:
                return InjectIntoWhen(w: w, exitStmt: exitStmt, loopDepth: loopDepth);

            case LoopStatement loop:
            {
                Statement body = InjectExit(stmt: loop.Body,
                    exitStmt: exitStmt,
                    loopDepth: loopDepth + 1);
                return !ReferenceEquals(objA: body, objB: loop.Body)
                    ? loop with { Body = body }
                    : loop;
            }

            case DangerStatement d:
            {
                Statement body = InjectExit(stmt: d.Body,
                    exitStmt: exitStmt,
                    loopDepth: loopDepth);
                return !ReferenceEquals(objA: body, objB: d.Body)
                    ? d with { Body = (BlockStatement)body }
                    : d;
            }

            default:
                return stmt;
        }
    }

    private static BlockStatement InjectIntoBlock(BlockStatement b, Statement exitStmt,
        int loopDepth)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement n =
                InjectExit(stmt: s, exitStmt: exitStmt, loopDepth: loopDepth);
            stmts.Add(item: n);
            if (!ReferenceEquals(objA: n, objB: s))
            {
                changed = true;
            }
        }

        return changed
            ? b with { Statements = stmts }
            : b;
    }

    private static IfStatement InjectIntoIf(IfStatement ifs, Statement exitStmt,
        int loopDepth)
    {
        Statement then = InjectExit(stmt: ifs.ThenStatement,
            exitStmt: exitStmt,
            loopDepth: loopDepth);
        Statement? elseS = ifs.ElseStatement != null
            ? InjectExit(stmt: ifs.ElseStatement,
                exitStmt: exitStmt,
                loopDepth: loopDepth)
            : null;
        bool changed = !ReferenceEquals(objA: then, objB: ifs.ThenStatement) ||
                       !ReferenceEquals(objA: elseS, objB: ifs.ElseStatement);
        return changed
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private static WhenStatement InjectIntoWhen(WhenStatement w, Statement exitStmt,
        int loopDepth)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
        foreach (WhenClause c in w.Clauses)
        {
            Statement body =
                InjectExit(stmt: c.Body, exitStmt: exitStmt, loopDepth: loopDepth);
            clauses.Add(item: !ReferenceEquals(objA: body, objB: c.Body)
                ? c with { Body = body }
                : c);
            if (!ReferenceEquals(objA: body, objB: c.Body))
            {
                changed = true;
            }
        }

        return changed
            ? w with { Clauses = clauses }
            : w;
    }

    private static BlockStatement MakeBlock(IEnumerable<Statement> stmts, SourceLocation loc)
    {
        return new BlockStatement(Statements: stmts.ToList(), Location: loc);
    }

    private static DeclarationStatement MakeBinding(string name, Expression value, TypeSymbol? type,
        SourceLocation loc)
    {
        var decl = new VariableDeclaration(Name: name,
            Type: type != null
                ? TypeInfoToExpr(type: type, loc: loc)
                : null,
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        return new DeclarationStatement(Declaration: decl, Location: loc);
    }

    private static TypeExpression TypeInfoToExpr(TypeSymbol type, SourceLocation loc)
    {
        string baseName = type switch
        {
            RecordTypeSymbol { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeSymbol { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution
                ? type.BareName
                : type.Name
        };
        return new TypeExpression(Name: baseName, GenericArguments: [], Location: loc)
        {
            ResolvedType = type
        };
    }
}
