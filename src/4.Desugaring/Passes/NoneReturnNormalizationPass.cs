using SyntaxTree;

namespace Builder.Desugaring.Passes;

/// <summary>
/// D-AST-0: Normalizes null return types and bare return statements.
/// <list type="bullet">
///   <item><see cref="RoutineDeclaration"/> with <c>ReturnType == null</c> (no <c>-&gt;</c> written)
///         -> <c>ReturnType = TypeExpression("None")</c>.</item>
///   <item><see cref="ReturnStatement"/> with <c>Value == null</c> (bare <c>return</c>)
///         -> <c>Value = IdentifierExpression("None")</c>.</item>
/// </list>
/// After this pass, <c>null</c> in either position is a genuine unresolved error.
/// Runs as the very first pass in <see cref="DesugaringPipeline.Run"/>.
/// Does NOT touch <see cref="ExternalDeclaration"/> (null ReturnType = void in C interop)
/// or <see cref="VariantReturnStatement"/> (null Value = FromAbsent, semantically meaningful).
/// </summary>
#pragma warning disable CS9113
internal sealed class NoneReturnNormalizationPass(DesugaringContext _)
    #pragma warning restore CS9113
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            if (program.Declarations[index: i] is SyntaxTree.Declaration decl)
            {
                program.Declarations[index: i] = NormalizeDeclaration(decl: decl);
            }
        }
    }

    private SyntaxTree.Declaration NormalizeDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration r:
                return NormalizeRoutine(r: r);

            case EntityDeclaration e:
                NormalizeMemberList(members: e.Members);
                return e;

            case RecordDeclaration rec:
                NormalizeMemberList(members: rec.Members);
                return rec;

            case CrashableDeclaration cr:
                NormalizeMemberList(members: cr.Members);
                return cr;

            default:
                return decl;
        }
    }

    private void NormalizeMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
        {
            if (members[index: i] is RoutineDeclaration r)
            {
                members[index: i] = NormalizeRoutine(r: r);
            }
        }
    }

    private RoutineDeclaration NormalizeRoutine(RoutineDeclaration r)
    {
        TypeExpression? returnType = r.ReturnType ??
                                     new TypeExpression(Name: "None",
                                         GenericArguments: null,
                                         Location: r.Location);
        Statement body = EndWithReturn(routine: r, returnType: returnType, body: NormalizeStatement(stmt: r.Body));
        if (ReferenceEquals(objA: returnType, objB: r.ReturnType) &&
            ReferenceEquals(objA: body, objB: r.Body))
        {
            return r;
        }

        return r with { ReturnType = returnType, Body = body };
    }

    /// <summary>
    /// In a language whose routines need no closing <c>return</c> (<c>LanguageRules.RoutinesEndWithReturn</c>
    /// false, decided by the routine's own file), a routine that returns nothing and whose body does not already
    /// end in <c>return</c> or <c>throw</c> gets <c>return</c> at its end: the one exit teardown is anchored at.
    /// A routine that returns a value still returns it on every path itself. A body that ends in a <c>loop</c> with no
    /// <c>break</c> of its own gets none either: only a <c>return</c> or <c>throw</c> leaves that loop, so a closing
    /// <c>return</c> after it could never run.
    /// </summary>
    private static Statement EndWithReturn(RoutineDeclaration routine, TypeExpression returnType, Statement body)
    {
        string? file = routine.Location.FileName;
        if (!Frontends.Languages.HasSourceExtension(fileName: file) ||
            Frontends.Languages.For(language: Frontends.Languages.OfFile(fileName: file)).RoutinesEndWithReturn ||
            returnType is not { Name: "None", GenericArguments: null or [] } ||
            body is not BlockStatement block ||
            block.Statements is [.., ReturnStatement or ThrowStatement] ||
            block.Statements is [.., LoopStatement lastLoop] && Verification.ControlFlowExits.NeverEndsByItself(loop: lastLoop))
        {
            return body;
        }

        SourceLocation end = block.Statements.Count > 0
            ? block.Statements[^1].Location
            : block.Location;
        var closing = new ReturnStatement(Value: new IdentifierExpression(Name: "None", Location: end),
            Location: end);
        return block with { Statements = [.. block.Statements, closing] };
    }

    private Statement NormalizeStatement(Statement stmt)
    {
        return stmt switch
        {
            ReturnStatement { Value: null } ret => NormalizeBareReturn(ret: ret),
            BlockStatement b => NormalizeBlock(b: b),
            IfStatement ifs => NormalizeIf(ifs: ifs),
            LoopStatement loop => NormalizeLoop(loop: loop),
            WhenStatement ws => NormalizeWhen(ws: ws),
            DeclarationStatement { Declaration: RoutineDeclaration r } ds => NormalizeRoutineDecl(
                ds: ds,
                r: r),
            _ => stmt
        };
    }

    private static Statement NormalizeBareReturn(ReturnStatement ret)
    {
        return ret with { Value = new IdentifierExpression(Name: "None", Location: ret.Location) };
    }

    private BlockStatement NormalizeBlock(BlockStatement b)
    {
        List<Statement> stmts = b.Statements;
        List<Statement>? replaced = null;
        for (int i = 0; i < stmts.Count; i++)
        {
            Statement lowered = NormalizeStatement(stmt: stmts[index: i]);
            if (ReferenceEquals(objA: lowered, objB: stmts[index: i]))
            {
                continue;
            }

            replaced ??= [.. stmts];
            replaced[index: i] = lowered;
        }

        return replaced != null
            ? b with { Statements = replaced }
            : b;
    }

    private IfStatement NormalizeIf(IfStatement ifs)
    {
        Statement thenN = NormalizeStatement(stmt: ifs.ThenStatement);
        Statement? elseN = ifs.ElseStatement != null
            ? NormalizeStatement(stmt: ifs.ElseStatement)
            : null;
        if (ReferenceEquals(objA: thenN, objB: ifs.ThenStatement) &&
            ReferenceEquals(objA: elseN, objB: ifs.ElseStatement))
        {
            return ifs;
        }

        return ifs with { ThenStatement = thenN, ElseStatement = elseN };
    }

    private LoopStatement NormalizeLoop(LoopStatement loop)
    {
        Statement bodyN = NormalizeStatement(stmt: loop.Body);
        if (ReferenceEquals(objA: bodyN, objB: loop.Body))
        {
            return loop;
        }

        return loop with { Body = bodyN };
    }

    private WhenStatement NormalizeWhen(WhenStatement ws)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: ws.Clauses.Count);
        foreach (WhenClause c in ws.Clauses)
        {
            Statement bodyN = NormalizeStatement(stmt: c.Body);
            changed |= !ReferenceEquals(objA: bodyN, objB: c.Body);
            clauses.Add(item: c with { Body = bodyN });
        }

        return changed
            ? ws with { Clauses = clauses }
            : ws;
    }

    private DeclarationStatement NormalizeRoutineDecl(DeclarationStatement ds,
        RoutineDeclaration r)
    {
        RoutineDeclaration rN = NormalizeRoutine(r: r);
        if (ReferenceEquals(objA: rN, objB: r))
        {
            return ds;
        }

        return ds with { Declaration = rN };
    }
}
