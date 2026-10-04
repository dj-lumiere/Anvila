using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
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
/// <para>A creator expression that names a <c>create</c> overload becomes the call of that overload, a
/// creator expression that gives a backend-represented record its one value becomes a
/// <see cref="BackendCastExpression"/>, and a creator of a variant (one arm and its value) or of a
/// <c>Check</c>/<c>Lookup</c> carrier (its <c>type_id</c> and payload) becomes a
/// <see cref="TaggedCreatorExpression"/>.</para>
///
/// <para>Runs at Phase 9 over each body that reaches the emitter, after monomorphization, so every
/// constructed type is concrete. It runs first there, so the use-after-steal marks see the final
/// argument positions. The rewrite is idempotent.</para>
/// </summary>
internal sealed class ConstructionLoweringPass(TypeSymbol u64) : AstRewriter
{
    /// <summary>Lowers the constructions of one routine body in place.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new ConstructionLoweringPass(u64: registry.LookupType(name: "U64") ??
                                                     throw new InvalidOperationException(
                                                         message: "The U64 type is not registered."));
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
    private Expression LowerCreator(CreatorExpression creator)
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

        // The backend builds the type the creator is stamped with: the one analysis constructed, else the
        // creator's own type.
        creator.ConstructedType = creator.ConstructedType is null or ErrorTypeSymbol
            ? creator.ResolvedType is null or ErrorTypeSymbol
                ? throw new InvalidOperationException(
                    message: $"The creator of '{creator.TypeName}' at {creator.Location} has no type.")
                : creator.ResolvedType
            : creator.ConstructedType;

        return creator.ConstructedType switch
        {
            VariantTypeSymbol variant => VariantCreator(creator: creator, variant: variant),
            RecordTypeSymbol { BackendType: not null } target when creator is { MemberVariables: [(_, var value)] } =>
                Cast(value: value, target: target, location: creator.Location),
            RecordTypeSymbol { BackendType: null } or EntityTypeSymbol => WithEveryField(creator: creator),
            _ => creator
        };
    }

    /// <summary>The creator with a value for every field: a field it leaves out (the trailing fields, as the
    /// values come in field order) gets the zero value of its type.</summary>
    internal static CreatorExpression WithEveryField(CreatorExpression creator)
    {
        List<MemberVariableInfo>? fields = MemberVariablesOf(type: creator.ConstructedType!);
        if (fields is null || creator.MemberVariables.Count >= fields.Count ||
            creator.MemberVariables.Count == 0 && creator.ConstructedType is EntityTypeSymbol)
        {
            return creator;
        }

        var values = new List<(string Name, Expression Value)>(collection: creator.MemberVariables);
        for (int i = values.Count; i < fields.Count; i++)
        {
            values.Add(item: (fields[index: i].Name, new ZeroValueExpression(Location: creator.Location)
            {
                ResolvedType = fields[index: i].Type
            }));
        }

        return creator with { MemberVariables = values };
    }

    /// <summary>A variant built from one arm and its value: the arm type's <c>type_id</c> and the value. An
    /// arm without a value (<c>None</c>) has tag 0 and no payload.</summary>
    private TaggedCreatorExpression VariantCreator(CreatorExpression creator, VariantTypeSymbol variant)
    {
        if (creator.MemberVariables is not [(string armName, var value)])
        {
            throw new InvalidOperationException(
                message: $"A creator of variant '{variant.Name}' at {creator.Location} gives " +
                         $"{creator.MemberVariables.Count} values, not one arm.");
        }

        VariantMemberInfo arm = (variant.CarrierKind is CarrierKind.Result or CarrierKind.Lookup
                                    ? CarrierArm(carrier: variant, armName: armName)
                                    : variant.Members.FirstOrDefault(predicate: m => m.Name == armName)) ??
                                throw new InvalidOperationException(
                                    message: $"Variant '{variant.Name}' has no arm '{armName}'.");
        bool empty = arm.Type is null or { IsNone: true };
        return new TaggedCreatorExpression(
            Tag: Tag(value: empty
                    ? 0UL
                    : TypeIdHelper.ComputeTypeId(fullName: arm.Type!.FullName),
                location: creator.Location),
            Payload: empty
                ? null
                : value,
            Location: creator.Location) { ResolvedType = variant };
    }

    /// <summary>
    /// The arm of a <c>Check</c>/<c>Lookup</c> a carrier return names (<c>VariantReturnLoweringPass</c>), told apart by
    /// type: <c>Crashables</c> is the error arm (a thrown crashable goes in it boxed, see the backends), a
    /// <c>Lookup</c>'s <c>None</c> its absent arm, and the remaining one the success arm, whatever the carrier's
    /// <c>T</c> is (a <c>Check[None]</c>'s is a None, which is also what its <c>return none</c> names).
    /// </summary>
    private static VariantMemberInfo? CarrierArm(VariantTypeSymbol carrier, string armName)
    {
        VariantMemberInfo? error = carrier.Members.FirstOrDefault(predicate: m =>
            m.Type?.Name == RuntimeContract.Crashables);
        VariantMemberInfo? absent = carrier.CarrierKind == CarrierKind.Lookup
            ? carrier.Members.FirstOrDefault(predicate: m => m.IsNone)
            : null;
        VariantMemberInfo? success = carrier.Members.FirstOrDefault(predicate: m => m != error && m != absent);
        return armName switch
        {
            "None" => absent ?? success,
            RuntimeContract.Crashables => error,
            VariantReturnLoweringPass.SuccessArm => success,
            _ => carrier.Members.FirstOrDefault(predicate: m => m.Name == armName)
        };
    }

    private LiteralExpression Tag(ulong value, SourceLocation location)
    {
        return new LiteralExpression(Value: value, LiteralType: TokenType.U64Literal, Location: location)
        {
            ResolvedType = u64
        };
    }

    /// <summary>The field-by-field construction or the cast a construction call stands for, or null when it
    /// stays a call.</summary>
    private static Expression? LowerConstructionCall(CallExpression call)
    {
        RoutineInfo? routine = call.ResolvedRoutine;
        TypeSymbol? constructed = call.ConstructedType;

        // A recovered construction (`try U64(x)`) calls the creator's recovery variant, which returns the
        // carrier: it stays a call.
        if (routine is { IsRecoveryVariant: true })
        {
            return null;
        }

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
            CrashableTypeSymbol crashable when ArgumentsGiveFields(call: call, fields: crashable.MemberVariables) =>
                Memberwise(call: call, type: crashable),
            EntityTypeSymbol { MemberVariables.Count: > 0 } entity when !routesToUserCreate &&
                ArgumentsGiveFields(call: call, fields: entity.MemberVariables) =>
                Memberwise(call: call, type: entity),
            RecordTypeSymbol { MemberVariables.Count: > 0 } record and not VariantTypeSymbol when
                ArgumentsGiveFields(call: call, fields: record.MemberVariables) =>
                Memberwise(call: call, type: record),
            _ => null
        };
    }

    /// <summary>
    /// Whether the call's arguments are the type's member variables: each one named, or, when analysis bound no
    /// creator, one positional value per member variable in declaration order (<c>Point(3, 4)</c>, which analysis
    /// allows for a type of up to two member variables).
    /// </summary>
    private static bool ArgumentsGiveFields(CallExpression call, List<MemberVariableInfo> fields)
    {
        return ArgumentsNameFields(arguments: call.Arguments, fields: fields) ||
               call.ResolvedRoutine == null && call.Arguments.Count == fields.Count &&
               call.Arguments.All(predicate: argument => argument is not NamedArgumentExpression);
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
