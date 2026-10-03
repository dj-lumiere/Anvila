using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Lowers the language's view of a caught error, <c>Crashables</c>:
/// <list type="bullet">
/// <item>the error arm of a <c>when</c> on a <c>Check</c>/<c>Lookup</c> carrier (<c>is Crashables e</c>, or an
/// <c>else e</c> only the error reaches) becomes one <see cref="CrashablePattern"/>, which
/// <see cref="PatternLoweringPass"/> turns into the error-arm test and the binding of <c>e</c>;</item>
/// <item>a call of a <c>Crashables</c> routine the builder supplies (<c>@innate</c>: crash_title, crash_message,
/// represent, diagnose, crash_type_id, free) becomes a <see cref="CrashableDispatchExpression"/>, which a backend
/// translates into a call through the mold of the object the error lives in.</item>
/// </list>
/// </summary>
internal sealed class CrashableExpansionPass(PostprocessingContext ctx)
{
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = Lower(body: r.Body);
                    if (!ReferenceEquals(objA: newBody, objB: r.Body))
                    {
                        program.Declarations[index: i] = r with { Body = newBody };
                    }

                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(members: e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(members: rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Lowers the error-handling variant bodies (<see cref="PostprocessingContext.VariantBodies"/>): a non-tail
    /// failable call in a <c>grab</c>/<c>lookup</c> variant propagates through an <c>is Crashables e</c> arm.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            Statement body = ctx.VariantBodies[key: key];
            Statement lowered = Lower(body: body);
            if (!ReferenceEquals(objA: lowered, objB: body))
            {
                ctx.VariantBodies[key: key] = lowered;
            }
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }

            Statement newBody = Lower(body: m.Body);
            if (!ReferenceEquals(objA: newBody, objB: m.Body))
            {
                members[index: j] = m with { Body = newBody };
            }
        }
    }

    private static Statement Lower(Statement body)
    {
        return new CrashablesRewriter().VisitStatement(stmt: body);
    }

    private sealed class CrashablesRewriter : AstRewriter
    {
        protected override Statement VisitWhen(WhenStatement s)
        {
            var lowered = (WhenStatement)base.VisitWhen(s: s);
            if (!IsCheckOrLookup(type: lowered.Expression?.ResolvedType))
            {
                return lowered;
            }

            bool changed = false;
            var clauses = new List<WhenClause>(capacity: lowered.Clauses.Count);
            foreach (WhenClause clause in lowered.Clauses)
            {
                if (ErrorArmBinding(pattern: clause.Pattern) is { } binding && clause.Pattern is not CrashablePattern)
                {
                    clauses.Add(item: clause with
                    {
                        Pattern = new CrashablePattern(ErrorType: null,
                            VariableName: binding.Name,
                            Location: clause.Pattern.Location)
                    });
                    changed = true;
                }
                else
                {
                    clauses.Add(item: clause);
                }
            }

            return changed
                ? lowered with { Clauses = clauses }
                : lowered;
        }

        protected override Expression VisitCall(CallExpression e)
        {
            Expression lowered = base.VisitCall(e: e);
            if (lowered is not CallExpression { Callee: MemberExpression member, Arguments.Count: 0 } call ||
                !IsInnateCrashablesCall(call: call, member: member))
            {
                return lowered;
            }

            string dispatched = member.MemberName == RuntimeContract.CrashFree
                ? RuntimeContract.Destroy
                : member.MemberName;
            return new CrashableDispatchExpression(Carrier: member.Object,
                MemberName: dispatched,
                Location: call.Location) { ResolvedType = call.ResolvedType };
        }

        /// <summary>A call of a <c>Crashables</c> routine the builder supplies: resolved to one marked <c>@innate</c>, or
        /// (a call a later pass resolves, such as the <c>represent</c> an f-string part renders through) named like one
        /// on a <c>Crashables</c> receiver.</summary>
        private static bool IsInnateCrashablesCall(CallExpression call, MemberExpression member)
        {
            if (call.ResolvedRoutine is { OwnerType: { } owner } routine)
            {
                return owner.Name == RuntimeContract.Crashables && routine.Annotations.Contains(value: "innate");
            }

            return member.Object.ResolvedType?.Name == RuntimeContract.Crashables &&
                   (member.MemberName is RuntimeContract.CrashTypeId or RuntimeContract.CrashFree ||
                    RuntimeContract.CrashMoldMembers.Contains(value: member.MemberName));
        }

        /// <summary>The binding of a carrier's error arm: <c>is Crashables e</c>, an already lowered
        /// <see cref="CrashablePattern"/>, or an <c>else e</c> analysis proved only the error reaches.</summary>
        private static (string? Name, bool Is)? ErrorArmBinding(Pattern pattern)
        {
            return pattern switch
            {
                CrashablePattern cp => (cp.VariableName, true),
                TypePattern { Type.Name: RuntimeContract.Crashables } tp => (tp.VariableName, true),
                ElsePattern { BindsCarrierError: true } ep => (ep.VariableName, true),
                _ => null
            };
        }

        private static bool IsCheckOrLookup(TypeSymbol? type)
        {
            return type is RecordTypeSymbol
            {
                CarrierKind: TypeModel.Enums.CarrierKind.Result or TypeModel.Enums.CarrierKind.Lookup
            };
        }
    }
}
