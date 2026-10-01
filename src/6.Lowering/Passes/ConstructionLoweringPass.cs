using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Decides how every construction is built, so the emitter only translates the result.
///
/// <para>A construction call <c>Type(...)</c> becomes one of three things:</para>
/// <list type="bullet">
/// <item>A field-by-field <see cref="CreatorExpression"/>, with one value per member variable in declaration
/// order, when the call binds the type's synthesized memberwise creator (which has no body), when its
/// arguments name exactly the type's member variables (a record or crashable, or an entity whose call does
/// not bind a <c>create</c> the user wrote: inside a type's own <c>create</c> this is how the value is
/// built without calling <c>create</c> again), or when it builds a record without member variables.</item>
/// <item>A <see cref="BackendCastExpression"/> when it builds a backend-represented record
/// (<c>@llvm("...")</c>) from one value and only changes that value's representation: no creator was bound,
/// or the bound creator does not take the value's own type and the value is itself backend-represented
/// (a number, a choice, flags) or a pointer turning into a pointer record.</item>
/// <item>The call itself otherwise: it binds a creator with a body, which builds the value.</item>
/// </list>
/// <para>A creator expression that names a <c>create</c> overload becomes the call of that overload, and a
/// creator expression that gives a backend-represented record its one value becomes a
/// <see cref="BackendCastExpression"/>.</para>
///
/// <para>Runs at Phase 9 over each body that reaches the emitter, after monomorphization, so every
/// constructed type is concrete. It runs first there, so the use-after-steal marks see the final
/// argument positions. The rewrite is idempotent.</para>
/// </summary>
internal sealed class ConstructionLoweringPass : AstRewriter
{
    /// <summary>Lowers the constructions of one routine body in place.</summary>
    public static void Run(Statement body)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new ConstructionLoweringPass();
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Expression VisitCall(CallExpression e)
    {
        var call = (CallExpression)base.VisitCall(e: e);
        return call.Callee switch
        {
            IdentifierExpression => LowerConstructionCall(call: call) switch
            {
                CreatorExpression creator => LowerCreator(creator: creator),
                { } lowered => lowered,
                null => call
            },
            // A member conversion `x.Type()` with no creator bound converts the receiver itself (a bound
            // one was already rewritten to the construction `Type(x)` by ExpressionLoweringPass).
            MemberExpression member when call is
            {
                LoweringKind: CallLoweringKind.TypeConstructor, ResolvedRoutine: null,
                ConstructedType: RecordTypeSymbol { BackendType: not null } target
            } => Cast(value: member.Object, target: target, location: call.Location),
            _ => call
        };
    }

    /// <inheritdoc/>
    protected override Expression VisitCreator(CreatorExpression e)
    {
        return LowerCreator(creator: (CreatorExpression)base.VisitCreator(e: e));
    }

    /// <summary>The call of the <c>create</c> overload a creator expression names, the cast of a
    /// backend-represented record's one value, or the creator expression itself.</summary>
    private static Expression LowerCreator(CreatorExpression creator)
    {
        if (creator.ResolvedCreatorRoutine is { } routine)
        {
            return new CallExpression(
                Callee: new IdentifierExpression(Name: routine.Name, Location: creator.Location)
                {
                    ResolvedRoutine = routine
                },
                Arguments: creator.MemberVariables
                                  .Select(selector: mv => (Expression)new NamedArgumentExpression(Name: mv.Name,
                                       Value: mv.Value,
                                       Location: mv.Value.Location) { ResolvedType = mv.Value.ResolvedType })
                                  .ToList(),
                Location: creator.Location)
            {
                ResolvedRoutine = routine,
                ResolvedType = routine.ReturnType ?? creator.ConstructedType ?? creator.ResolvedType,
                LoweringKind = CallLoweringKind.DirectRoutine
            };
        }

        return creator is { MemberVariables: [(_, var value)] } &&
               (creator.ConstructedType ?? creator.ResolvedType) is RecordTypeSymbol
               {
                   BackendType: not null
               } target and not VariantTypeSymbol
            ? Cast(value: value, target: target, location: creator.Location)
            : creator;
    }

    /// <summary>The field-by-field construction or the cast a construction call stands for, or null when it
    /// stays a call.</summary>
    private static Expression? LowerConstructionCall(CallExpression call)
    {
        RoutineInfo? routine = call.ResolvedRoutine;
        TypeSymbol? constructed = call.ConstructedType;

        // The synthesized memberwise creator has no body: the construction is the field initialization.
        if (routine is { IsSynthesized: true, IsCreator: true, OwnerType: { } owner } &&
            MemberwiseCreatorMatchesFields(creator: routine, owner: owner))
        {
            return Memberwise(call: call, type: owner);
        }

        if (routine == null && call.Arguments.Count == 0 &&
            (constructed ?? call.ResolvedType) is RecordTypeSymbol { MemberVariables.Count: 0 } empty and
                not VariantTypeSymbol)
        {
            return Memberwise(call: call, type: empty);
        }

        if (call.LoweringKind is not (CallLoweringKind.TypeConstructor or CallLoweringKind.WrapperConstruction))
        {
            return null;
        }

        if (constructed is RecordTypeSymbol { BackendType: not null } backendRecord &&
            call.Arguments is [var only] &&
            IsRepresentationChange(record: backendRecord, value: Unnamed(argument: only), routine: routine))
        {
            return Cast(value: Unnamed(argument: only), target: backendRecord, location: call.Location);
        }

        bool routesToUserCreate = routine is { IsSynthesized: false, IsCreator: true } &&
                                  constructed is EntityTypeSymbol;
        return constructed switch
        {
            CrashableTypeSymbol crashable when ArgumentsNameFields(arguments: call.Arguments,
                fields: crashable.MemberVariables) => Memberwise(call: call, type: crashable),
            EntityTypeSymbol { MemberVariables.Count: > 0 } entity when !routesToUserCreate &&
                ArgumentsNameFields(arguments: call.Arguments, fields: entity.MemberVariables) =>
                Memberwise(call: call, type: entity),
            RecordTypeSymbol { MemberVariables.Count: > 0 } record and not VariantTypeSymbol when
                ArgumentsNameFields(arguments: call.Arguments, fields: record.MemberVariables) =>
                Memberwise(call: call, type: record),
            _ => null
        };
    }

    /// <summary>
    /// Whether building <paramref name="record"/> from <paramref name="value"/> only changes the value's
    /// representation. A bound creator that takes the value's own type (its other parameters defaulted) is the
    /// conversion and is called. Otherwise the value converts when no creator was bound, when it is itself
    /// backend-represented, or when a pointer becomes a pointer record.
    /// </summary>
    private static bool IsRepresentationChange(RecordTypeSymbol record, Expression value, RoutineInfo? routine)
    {
        if (routine == null)
        {
            return true;
        }

        if (value.ResolvedType is not { } valueType)
        {
            return false;
        }

        if (routine is { IsSynthesized: false, IsCreator: true, Parameters.Count: >= 1 } &&
            routine.Parameters.Skip(count: 1).All(predicate: p => p.HasDefaultValue) &&
            routine.Parameters[index: 0].Type is { } parameterType &&
            (parameterType.FullName == valueType.FullName ||
             parameterType.TypeArguments is [var inner] && inner.FullName == valueType.FullName))
        {
            return false;
        }

        return valueType is RecordTypeSymbol { BackendType: not null } ||
               valueType is EntityTypeSymbol && record.BackendType == "ptr";
    }

    /// <summary>True when <paramref name="creator"/> is the synthesized creator taking exactly the member
    /// variables of <paramref name="owner"/> (by name). A synthesized creator with a body (a conversion, a
    /// variant arm extractor) takes other parameters.</summary>
    private static bool MemberwiseCreatorMatchesFields(RoutineInfo creator, TypeSymbol owner)
    {
        List<MemberVariableInfo>? fields = MemberVariablesOf(type: owner);
        if (fields == null || creator.Parameters.Count != fields.Count)
        {
            return false;
        }

        var fieldNames = new HashSet<string>(collection: fields.Select(selector: f => f.Name));
        return creator.Parameters.All(predicate: p => fieldNames.Contains(item: p.Name));
    }

    /// <summary>True when every argument is named and the names are exactly the member variables.</summary>
    private static bool ArgumentsNameFields(List<Expression> arguments, List<MemberVariableInfo> fields)
    {
        return arguments.Count == fields.Count && arguments.All(predicate: argument =>
            argument is NamedArgumentExpression named &&
            fields.Any(predicate: field => field.Name == named.Name));
    }

    /// <summary>The field-by-field construction of <paramref name="type"/> from the call's arguments: one value
    /// per member variable in declaration order, bound by name, else by position.</summary>
    private static CreatorExpression Memberwise(CallExpression call, TypeSymbol type)
    {
        List<MemberVariableInfo> fields = MemberVariablesOf(type: type) ??
                                          throw new InvalidOperationException(
                                              message: $"'{type.Name}' has no member variables to construct.");
        var values = new List<(string Name, Expression Value)>(capacity: fields.Count);
        for (int i = 0; i < fields.Count; i++)
        {
            string name = fields[index: i].Name;
            Expression? argument =
                call.Arguments.FirstOrDefault(predicate: a => a is NamedArgumentExpression named && named.Name == name) ??
                (i < call.Arguments.Count && call.Arguments[index: i] is not NamedArgumentExpression
                    ? call.Arguments[index: i]
                    : null);
            if (argument == null)
            {
                throw new InvalidOperationException(
                    message: $"The construction of '{type.Name}' at {call.Location} gives no value for '{name}'.");
            }

            values.Add(item: (name, Unnamed(argument: argument)));
        }

        return new CreatorExpression(TypeName: type.Name, TypeArguments: null, MemberVariables: values,
            Location: call.Location)
        {
            ResolvedType = call.ResolvedType ?? type,
            ConstructedType = type,
            LoweringKind = CallLoweringKind.TypeConstructor
        };
    }

    private static BackendCastExpression Cast(Expression value, RecordTypeSymbol target, SourceLocation location)
    {
        return new BackendCastExpression(Value: value, Location: location) { ResolvedType = target };
    }

    private static Expression Unnamed(Expression argument)
    {
        return argument is NamedArgumentExpression named
            ? named.Value
            : argument;
    }

    private static List<MemberVariableInfo>? MemberVariablesOf(TypeSymbol type)
    {
        return type switch
        {
            EntityTypeSymbol e => e.MemberVariables,
            RecordTypeSymbol r => r.MemberVariables,
            _ => null
        };
    }
}
