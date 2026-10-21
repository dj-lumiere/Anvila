using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private TypeSymbol AnalyzeGenericMemberRoutineCallExpression(
        GenericMemberRoutineCallExpression generic)
    {
        TypeSymbol result = AnalyzeGenericMemberRoutineCallExpressionCore(generic: generic);
        // A generic call (`make_channel[S64](capacity: 4)`, `obj.method[T](..)`) resolves through a path distinct from
        // the plain-call analyzer, so the Suflae unsafe-call gate is applied here too on its resolved routine.
        EnforceUnsafeCallAllowed(resolved: generic.ResolvedRoutine, location: generic.Location);
        ReportNotRebuildableFromSerial(generic: generic, target: result);
        return result;
    }

    /// <summary>
    /// <c>sv.to[T]()</c> rebuilds <c>T</c> through its <c>from_serial</c>: a type's own, or the one the builder writes
    /// for a type whose member variables can all be read. A <c>T</c> with neither, or holding a type with neither,
    /// would build a value with nothing in it, so it is a build error.
    /// </summary>
    private void ReportNotRebuildableFromSerial(GenericMemberRoutineCallExpression generic, TypeSymbol target)
    {
        if (generic is not { MemberRoutineName: "to", Object.ResolvedType.Name: "SerialValue" } ||
            target is ErrorTypeSymbol || !IsProgramFile(filePath: generic.Location.FileName) ||
            NotRebuildable(type: target, seen: []) is not { } missing)
        {
            return;
        }

        string inside = missing.FullName == target.FullName ? "" : $" ('{target.Name}' holds it)";
        ReportError(code: SemanticDiagnosticCode.NotRebuildableFromSerial,
            message: $"'{missing.Name}'{inside} cannot be read back from a serialized value: it keeps member " +
                     "variables that 'serialize' does not write. Read back a type whose member variables are all " +
                     "readable, or keep the serialized value.",
            location: generic.Location);
    }

    /// <summary>The first type in <paramref name="type"/>, itself or one it holds, that has no way back from its
    /// serialized form, or null when every one has.</summary>
    private TypeSymbol? NotRebuildable(TypeSymbol type, HashSet<string> seen)
    {
        type = Unroamed(type: type);
        if (type is GenericParameterTypeSymbol or ErrorTypeSymbol || !seen.Add(item: type.FullName))
        {
            return null;
        }

        RoutineInfo? own = _registry.LookupMemberRoutine(type: type,
            memberRoutineName: Instantiation.AutoWiredRegistrationPass.FromSerialRoutineName);
        IEnumerable<TypeSymbol> parts;
        if (own is { OwnerType: not GenericParameterTypeSymbol, IsSynthesized: false })
        {
            // A library leaf or collection: its elements are rebuilt with theirs.
            parts = type.TypeArguments ?? [];
        }
        else if (Instantiation.AutoWiredRegistrationPass.IsFromSerialCandidate(type: type))
        {
            parts = type switch
            {
                VariantTypeSymbol variant => variant.Members.Select(selector: m => m.Type)
                                                    .OfType<TypeSymbol>(),
                RecordTypeSymbol record => record.MemberVariables.Select(selector: mv => mv.Type),
                EntityTypeSymbol entity => entity.MemberVariables.Select(selector: mv => mv.Type),
                _ => []
            };
        }
        else
        {
            return type;
        }

        foreach (TypeSymbol part in parts)
        {
            if (NotRebuildable(type: part, seen: seen) is { } missing)
            {
                return missing;
            }
        }

        return null;
    }

    /// <summary>
    /// A bracket naming a generic type without its arguments (`set.to[List]()`) takes them from the one member of
    /// the family that goes to that type (`Set[S64].to[List[S64]]`): the receiver decides them. The call's own
    /// bracket is rewritten to the full type, so the rest of the build sees `to[List[S64]]`. Left as written when no
    /// member, or more than one, goes to that type.
    /// </summary>
    private static void FillBracketFromFamily(GenericMemberRoutineCallExpression generic, List<TypeSymbol> typeArgs,
        List<RoutineInfo> family)
    {
        for (int i = 0; i < typeArgs.Count; i++)
        {
            if (Unroamed(type: typeArgs[index: i]) is not { IsGenericDefinition: true } open)
            {
                continue;
            }

            int slot = i;
            List<TypeSymbol> targets = family.Where(predicate: r => r.TypeArguments?.Count == typeArgs.Count)
                                             .Select(selector: r => FamilyTarget(routine: r, index: slot))
                                             .Where(predicate: t => t.BareName == open.BareName &&
                                                                    !t.IsGenericDefinition &&
                                                                    DefinitionOf(type: t).Realm == open.Realm)
                                             .DistinctBy(keySelector: t => t.FullName)
                                             .ToList();
            if (targets is not [{ } full])
            {
                continue;
            }

            typeArgs[index: i] = full;
            generic.TypeArguments[index: i] =
                Builder.Instantiation.GenericAstRewriter.TypeInfoToTypeExpr(type: full,
                    location: generic.TypeArguments[index: i].Location) with
                {
                    KnownType = full,
                    // The completed argument names the same language's type as the written one (`RF::List`).
                    Realm = generic.TypeArguments[index: i].Realm
                };
        }
    }

    /// <summary>The specialization family <paramref name="name"/> a call on <paramref name="receiver"/> picks from:
    /// the receiver's own (a handle's is its entity's), or for a type parameter the families of the protocols its
    /// <c>obeys</c> constraints name.</summary>
    private List<RoutineInfo> FamilyOfReceiver(TypeSymbol receiver, string name)
    {
        if (receiver is not GenericParameterTypeSymbol parameter)
        {
            return _registry.LookupSpecializedMemberRoutines(owner: Unroamed(type: receiver), name: name);
        }

        var found = new List<RoutineInfo>();
        IEnumerable<GenericConstraintDeclaration> constraints =
            (_currentRoutine?.GenericConstraints ?? []).Concat(second: _currentRoutine?.OwnerType?.GenericConstraints ?? []);
        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (constraint.ParameterName != parameter.Name ||
                constraint is not { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: { } protocols })
            {
                continue;
            }

            foreach (TypeExpression protocolExpr in protocols)
            {
                if (ResolveType(typeExpr: protocolExpr) is ProtocolTypeSymbol protocol)
                {
                    found.AddRange(collection: _registry.LookupSpecializedMemberRoutines(owner: protocol, name: name)
                                                        .Where(predicate: r => found.All(predicate: f =>
                                                             f.RegistryKey != r.RegistryKey)));
                }
            }
        }

        return found;
    }

    /// <summary>The type a member of a family goes to at <paramref name="index"/>: what it gives back when its bracket
    /// names that (the bracket of a routine a library writes can name a type of the same name in another language's
    /// library), else the bracket entry.</summary>
    internal static TypeSymbol FamilyTarget(RoutineInfo routine, int index)
    {
        TypeSymbol written = routine.TypeArguments![index: index];
        return routine.ReturnType is { } returned && Unroamed(type: returned) is { } target &&
               target.FullName == written.FullName
            ? target
            : written;
    }

    /// <summary>The entity a shared-entity handle (<c>Roamed[E]</c>) stands for, else the type itself: a Suflae bracket
    /// that names an entity (<c>to[Set]</c>) means the entity.</summary>
    internal static TypeSymbol Unroamed(TypeSymbol type)
    {
        return type is RecordTypeSymbol
        {
            GenericDefinition.Name: Builder.Declaration.RuntimeContract.Roamed, TypeArguments: [{ } inner]
        }
            ? inner
            : type;
    }

    private TypeSymbol AnalyzeGenericMemberRoutineCallExpressionCore(
        GenericMemberRoutineCallExpression generic)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: generic.Object);

        // Resolve type arguments
        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            typeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // Check if this is a generic type constructor call (e.g., Hijacked[U8](addr))
        // The parser creates GenericMemberRoutineCallExpression for both Type[Args](args) and obj.MemberCallable[Args](args).
        // A FAILABLE construction `Type![Args](args)` parses with a BARE memberRoutineName equal to the
        // type name plus the structured `IsMemoryOperation` failable flag — recognize it here and
        // route to the type's failable `create` overload (e.g. the auto-generated variant arm
        // extractor `Dict[Text, SerialValue].create!(from: sv)`).
        if (generic.IsFree && generic.Object is IdentifierExpression typeId && objectType is TypeSymbol
            {
                IsGenericDefinition: true
            } typeInfo)
        {
            return AnalyzeGenericTypeConstructorCall(generic: generic,
                typeId: typeId,
                typeSymbol: typeInfo,
                typeArgs: typeArgs);
        }

        // A family of specializations (`routine S64.to[U8]()`, ...) is picked by the written type arguments.
        // A Suflae entity is held through its handle (`Roamed[SkipIterable[...]]`): the family is the entity's. A
        // type parameter's family is what the protocols it obeys write (`source.to[List]()` with
        // `TSource obeys Iterable[T]`): the member is bound to the protocol for now and picked again on the concrete
        // receiver when the body is made concrete.
        // A value's token (an element `each` hands out, `Modifying[Text]`) converts the value it is on: the family is
        // the value's, and the lowering reads the value through the token (TokenReadLoweringPass).
        if (typeArgs.Count > 0 && ValueTokenTarget(type: objectType) is { } held &&
            FamilyOfReceiver(receiver: held, name: generic.MemberRoutineName) is { Count: > 0 } heldFamily &&
            heldFamily.Any(predicate: r => r.TypeArguments is { } args && args.Count == typeArgs.Count &&
                                           typeArgs.Select(selector: (t, i) =>
                                                        SameTypeInRealm(a: FamilyTarget(routine: r, index: i),
                                                            b: Unroamed(type: t)))
                                                    .All(predicate: same => same)))
        {
            objectType = held;
        }

        if (typeArgs.Count > 0 && objectType is not ErrorTypeSymbol &&
            FamilyOfReceiver(receiver: objectType, name: generic.MemberRoutineName) is { Count: > 0 } family)
        {
            FillBracketFromFamily(generic: generic, typeArgs: typeArgs, family: family);

            RoutineInfo? chosen = family.FirstOrDefault(predicate: r =>
                r.TypeArguments is { } args && args.Count == typeArgs.Count &&
                typeArgs.Select(selector: (t, i) => SameTypeInRealm(a: FamilyTarget(routine: r, index: i),
                             b: Unroamed(type: t)))
                        .All(predicate: same => same));

            // The library converts a value to its own type where generic code needs one (a `Text` item of a tuple
            // read from text), a program never does: it uses the value as it is.
            if (chosen != null && IsOwnTypeConversion(name: generic.MemberRoutineName, owner: objectType,
                    typeArguments: typeArgs, location: generic.Location))
            {
                chosen = null;
            }

            // A variant's arm extraction (`sv.to[S64]()`) has a body the builder writes when it is first reached.
            if (chosen != null && Instantiation.AutoWiredRegistrationPass.IsVariantArmExtractor(routine: chosen))
            {
                EnsureVariantArmExtractorBody(extractor: chosen);
            }

            // A member a protocol writes for every obeying type (`Iterable[T].to[List[T]]`) runs on the receiver
            // itself: homed on the receiver's type, its `me` is that type (in Suflae the entity's handle).
            if (chosen is { OwnerType: ProtocolTypeSymbol } && Unroamed(type: objectType) is var home &&
                home is RecordTypeSymbol or EntityTypeSymbol && !home.IsGenericDefinition &&
                _registry.SubstituteMemberRoutineForOwner(memberRoutine: chosen, resolvedOwner: home) is { } homed)
            {
                chosen = homed;
            }

            // In a generic body (`line.to[T]()`), which member of the family it is depends on `T`: the call is bound to
            // one of them for now (they share their name, parameters and failability), and monomorphization picks
            // the one for the concrete type arguments. Its value has the type the written arguments say.
            if (chosen == null && typeArgs.Any(predicate: t => t is GenericParameterTypeSymbol) &&
                family.FirstOrDefault(predicate: r => r.TypeArguments?.Count == typeArgs.Count) is { } stand)
            {
                _ = AnalyzeGenericReceiverMemberRoutineCall(generic: generic,
                    objectType: objectType,
                    memberRoutine: stand,
                    typeArgs: typeArgs);
                int slot = stand.TypeArguments!.FindIndex(match: t => t.FullName == stand.ReturnType?.FullName);
                TypeSymbol value = slot >= 0
                    ? typeArgs[index: slot]
                    : stand.ReturnType ?? ErrorTypeSymbol.Instance;
                generic.ResolvedType = value;
                return value;
            }

            // No routine for exactly these arguments: one written for their kind (`needs TupleType T`) takes them.
            if (chosen == null && _registry.LookupKindGatedMemberRoutine(owner: objectType,
                    name: generic.MemberRoutineName, typeArguments: typeArgs) is { } forKind)
            {
                return AnalyzeGenericReceiverMemberRoutineCall(generic: generic,
                    objectType: objectType,
                    memberRoutine: forKind,
                    typeArgs: typeArgs);
            }

            if (chosen == null)
            {
                ReportSpecializationNotFound(name: generic.MemberRoutineName, owner: objectType,
                    typeArguments: typeArgs, location: generic.Location);
                foreach (Expression argument in generic.Arguments)
                {
                    AnalyzeExpression(expression: argument);
                }

                return ErrorTypeSymbol.Instance;
            }

            return AnalyzeGenericReceiverMemberRoutineCall(generic: generic,
                objectType: objectType,
                memberRoutine: chosen,
                typeArgs: typeArgs);
        }

        // Look up the memberRoutine on receiver type — LookupMemberRoutine handles generic resolutions. A name the
        // type declares more than once (`FileManager.storage[T](at:)` and `storage[T](at:, start:, count:)`) is
        // picked by the names of the arguments, the parameters a call names being the routine's.
        RoutineInfo? memberRoutine = _registry.LookupMemberRoutine(type: objectType,
            memberRoutineName: generic.MemberRoutineName);
        if (memberRoutine == null && objectType is not ErrorTypeSymbol &&
            _registry.HasSeveralOwnMemberRoutines(type: objectType, memberRoutineName: generic.MemberRoutineName,
                isFailable: null))
        {
            var candidates = new List<RoutineInfo>();
            _registry.CollectMemberRoutineCandidates(type: objectType, memberRoutineName: generic.MemberRoutineName,
                candidates: candidates);
            List<string?> named = generic.Arguments
                .Select(selector: a => a is NamedArgumentExpression n ? n.Name : null)
                .ToList();
            List<RoutineInfo> fitting = candidates
                .Where(predicate: r => r.Parameters.Count == named.Count &&
                                       named.Select(selector: (n, i) => n == null || r.Parameters[index: i].Name == n)
                                            .All(predicate: same => same))
                .ToList();
            if (fitting.Count == 1)
            {
                memberRoutine = fitting[index: 0];
            }
            else
            {
                ReportError(code: SemanticDiagnosticCode.MemberRoutineNotFound,
                    message: $"'{objectType.Name}' has {candidates.Count} routines '{generic.MemberRoutineName}', and " +
                             $"{(fitting.Count == 0 ? "none takes" : "more than one takes")} these arguments. Name the " +
                             "arguments the routine you mean takes.",
                    location: generic.Location);
                return ErrorTypeSymbol.Instance;
            }
        }
        if (memberRoutine is { IsSpecialization: true } && objectType is GenericParameterTypeSymbol)
        {
            ReportSpecializationNotFound(name: generic.MemberRoutineName, owner: objectType,
                typeArguments: typeArgs, location: generic.Location);
            return ErrorTypeSymbol.Instance;
        }

        if (memberRoutine != null)
        {
            return AnalyzeGenericReceiverMemberRoutineCall(generic: generic,
                objectType: objectType,
                memberRoutine: memberRoutine,
                typeArgs: typeArgs);
        }

        // Standalone generic function call (e.g., ptrtoint[Point, U64](p), hijacked_from[T](addr))
        // The object is an identifier that resolves to a routine, not a type or variable
        if (generic.Object is IdentifierExpression funcId)
        {
            // A realm-qualified foreign generic call (`LLVM::atan2[B32](...)` / `C::name[...]`) must
            // resolve to the FOREIGN routine, not the bare-name slot — which an ambient same-named free
            // routine may now own (the free `atan2(y, x)` vs the `LLVM::atan2` intrinsic). The realm-
            // qualified index and the generic-overload index both reach the intrinsic; the ambient
            // `LookupRoutine(bareName)` would wrongly bind the ambient routine and then trip RF-S460.
            // LookupGenericOverload covers generic free routines, which register only in the
            // generic-overload index — without it an explicit `gen_id[T](...)` call to a generic
            // routine in another module fails to resolve (concrete free routines resolve fine).
            RoutineInfo? routine = funcId.Realm is "LLVM" or "C"
                ? _registry.LookupRoutine(fullName: $"{funcId.Realm}::{funcId.Name}") ??
                  _registry.LookupGenericOverload(name: funcId.Name,
                      preferredArity: generic.Arguments.Count, includeForeign: true)
                : LookupRoutineWithModulePrefix(name: funcId.Name, preferredArity: generic.Arguments.Count);
            if (routine != null)
            {
                return AnalyzeStandaloneGenericRoutineCall(generic: generic,
                    funcId: funcId,
                    routine: routine,
                    typeArgs: typeArgs);
            }
        }

        // Analyze arguments
        foreach (Expression arg in generic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Analyzes a generic MEMBER-routine call on a receiver (<c>obj.select_many[U](...)</c>): aligns
    /// owner-level and member-level type arguments, monomorphizes <paramref name="memberRoutine"/>,
    /// analyzes the arguments, and returns the return type with <c>Me</c> / member-generic substitution
    /// applied.
    /// </summary>
    private TypeSymbol AnalyzeGenericReceiverMemberRoutineCall(
        GenericMemberRoutineCallExpression generic, TypeSymbol objectType,
        RoutineInfo memberRoutine, List<TypeSymbol> typeArgs)
    {
        generic.LoweringKind = ClassifyMemberRoutineCall(memberRoutine: memberRoutine);

        // Resolve the memberRoutine's generics from the explicit type arguments BEFORE analyzing the
        // arguments, so a lambda argument whose parameter binds a memberRoutine-level generic receives a
        // concrete expected type (e.g. `select_many[S64](transform: x => …)` types `x` as S64
        // instead of the unbound generic, which would collapse to <error> and cascade S503/S450/S505).
        if (memberRoutine.IsGenericDefinition)
        {
            // Owner-level generic params (e.g. T from Hijacked[T]) are bound by the receiver.
            // Compare typeArgs only against memberRoutine-level params (e.g. U from recast_as[U]).
            RoutineInfo? mono = MonomorphizeMemberRoutineWithTypeArgs(generic: generic,
                objectType: objectType,
                memberRoutine: memberRoutine,
                typeArgs: typeArgs);
            if (mono == null)
            {
                return ErrorTypeSymbol.Instance;
            }


            memberRoutine = mono;
        }

        // Variadic member routine (e.g. `List[T].create(elements...: T)` called as
        // `List[S32].create(1, 2, 3)`): pack the K trailing args into an Array[T, K] literal first.
        PackVariadicCallArgs(arguments: generic.Arguments,
            routine: memberRoutine,
            location: generic.Location);

        // Analyze arguments against the resolved memberRoutine (param types now concrete), so lambda
        // parameters bound to memberRoutine generics are typed correctly. AnalyzeCallArguments also binds
        // named/positional args and applies any remaining owner-generic substitution.
        AnalyzeCallArguments(routine: memberRoutine,
            arguments: generic.Arguments,
            location: generic.Location,
            callObjectType: objectType);

        ValidateExclusiveTokenUniqueness(arguments: generic.Arguments, location: generic.Location);

        generic.ResolvedRoutine = memberRoutine;
        generic.LoweringKind = ClassifyMemberRoutineCall(memberRoutine: memberRoutine);
        generic.IsInFlight = memberRoutine.IsInFlightReturn;
        TrackFailableMemberRoutineCall(memberRoutine: memberRoutine, location: generic.Location);

        if (memberRoutine.ReturnType == null)
        {
            return _registry.LookupType(name: "None") ?? ErrorTypeSymbol.Instance;
        }

        TypeSymbol returnType = memberRoutine.ReturnType;

        // Bind ProtocolSelf (`Me`) in the return type to the receiver. A protocol-extension memberRoutine
        // like `select_many[U] -> ?SelectManyIterator[T, U, Me]` leaves `Me` in its return; left
        // unbound it leaks into the per-implementer collector symbol (SelectManyIterator[.., Me].List)
        // and never resolves. Rebuild the resolution explicitly — a ProtocolSelf argument suppresses
        // the IsGenericResolution flag, so the generic substitution helpers below skip it.
        if (returnType.TypeArguments is { Count: > 0 } retArgs &&
            retArgs.Any(predicate: a => a is ProtocolSelfTypeSymbol || a.Name == "Me") &&
            GetGenericDefinition(resolution: returnType) is { } retDef)
        {
            var boundArgs = retArgs.Select(selector: a =>
                                        a is ProtocolSelfTypeSymbol || a.Name == "Me"
                                            ? objectType
                                            : a)
                                   .ToList();
            returnType =
                _registry.GetOrCreateResolution(genericDef: retDef, typeArguments: boundArgs);
        }

        // Substitute memberRoutine's own generic params (U from obtain_as[U]).
        return SubstituteGenericParamsInReturnType(returnType: returnType,
            memberRoutine: memberRoutine,
            typeArgs: typeArgs);
    }

    /// <summary>
    /// Monomorphizes a standalone generic free routine against the caller's explicit type arguments.
    /// Validates arity, builds the substitution map in <paramref name="typeSubs"/>, replaces
    /// <paramref name="routine"/> with the concrete resolution, and returns true on success.
    /// Reports a <c>WrongTypeArgumentCount</c> error and returns false when the arity does not match.
    /// </summary>
    private bool TryMonomorphizeFreeRoutine(GenericMemberRoutineCallExpression generic,
        ref RoutineInfo routine, List<TypeSymbol> typeArgs,
        out Dictionary<string, TypeSymbol>? typeSubs)
    {
        if (routine.GenericParameters == null || routine.GenericParameters.Count != typeArgs.Count)
        {
            ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"Routine '{routine.Name}' expects {routine.GenericParameters?.Count ?? 0} type arguments, got {typeArgs.Count}.",
                location: generic.Location);
            typeSubs = null;
            return false;
        }

        typeSubs = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < routine.GenericParameters.Count; i++)
        {
            if (typeArgs[index: i] is TypeSymbol concreteArg)
            {
                typeSubs[key: routine.GenericParameters[index: i]] = concreteArg;
            }
        }

        routine = _registry.GetOrCreateRoutineResolution(genericDef: routine,
            typeArguments: typeArgs.ToList());
        return true;
    }

    /// <summary>
    /// Monomorphizes a generic member routine against the caller's explicit type arguments, aligning
    /// owner-level and member-level parameters. Returns the concrete routine resolution, or null and
    /// reports a <c>WrongTypeArgumentCount</c> error when the arity does not match.
    /// </summary>
    private RoutineInfo? MonomorphizeMemberRoutineWithTypeArgs(
        GenericMemberRoutineCallExpression generic, TypeSymbol objectType,
        RoutineInfo memberRoutine, List<TypeSymbol> typeArgs)
    {
        // A routine of any type (`routine N.to[T]()`) owns all of its parameters: the receiver's own parameters
        // (`T` of `List[T]`) are not among them, whatever their names.
        HashSet<string> ownerGenericParamNames =
            (memberRoutine.GenericDefinition ?? memberRoutine).OwnerType is GenericParameterTypeSymbol
                ? []
                : GetOwnerGenericParameterNames(ownerType: objectType);
        List<string> memberRoutineOnlyParams = memberRoutine.GenericParameters
                                                           ?.Where(predicate: gp =>
                                                                 !ownerGenericParamNames.Contains(
                                                                     item: gp))
                                                            .ToList() ?? new List<string>();

        if (memberRoutineOnlyParams.Count != typeArgs.Count)
        {
            ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"member routine '{memberRoutine.Name}' expects {memberRoutineOnlyParams.Count} type arguments, got {typeArgs.Count}.",
                location: generic.Location);
            return null;
        }

        List<TypeSymbol> fullTypeArgs;
        if (memberRoutine.GenericParameters != null &&
            memberRoutine.GenericParameters.Count != typeArgs.Count)
        {
            fullTypeArgs = new List<TypeSymbol>(capacity: memberRoutine.GenericParameters.Count);
            int memberRoutineArgIdx = 0;
            Dictionary<string, TypeSymbol> ownerBindings =
                BuildOwnerBindingMap(ownerType: objectType);
            foreach (string paramName in memberRoutine.GenericParameters)
            {
                if (ownerGenericParamNames.Contains(item: paramName) &&
                    ownerBindings.TryGetValue(key: paramName, value: out TypeSymbol? ownerArg))
                {
                    fullTypeArgs.Add(item: ownerArg);
                }
                else if (memberRoutineArgIdx < typeArgs.Count)
                {
                    fullTypeArgs.Add(item: typeArgs[index: memberRoutineArgIdx++]);
                }
            }
        }
        else
        {
            fullTypeArgs = typeArgs.ToList();
        }

        return _registry.GetOrCreateRoutineResolution(genericDef: memberRoutine,
            typeArguments: fullTypeArgs);
    }

    /// <summary>
    /// Substitutes member-routine-level generic parameters in <paramref name="returnType"/> using
    /// the caller's <paramref name="typeArgs"/>. Handles both a bare generic parameter return and a
    /// generic resolution (e.g. <c>Hijacked[U]</c>) whose arguments contain parameters to substitute.
    /// Returns the substituted type, or <paramref name="returnType"/> unchanged when nothing matches.
    /// </summary>
    private TypeSymbol SubstituteGenericParamsInReturnType(TypeSymbol returnType,
        RoutineInfo memberRoutine, List<TypeSymbol> typeArgs)
    {
        if (memberRoutine.GenericParameters == null)
        {
            return returnType;
        }

        if (returnType is GenericParameterTypeSymbol)
        {
            int paramIndex = memberRoutine.GenericParameters
                                          .ToList()
                                          .IndexOf(item: returnType.Name);
            if (paramIndex >= 0 && paramIndex < typeArgs.Count &&
                typeArgs[index: paramIndex] is TypeSymbol resolved)
            {
                return resolved;
            }
        }

        if (returnType is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol? substituted = SubstituteGenericResolutionArgs(returnType: returnType,
                genericParams: memberRoutine.GenericParameters,
                typeArgs: typeArgs);
            if (substituted != null)
            {
                return substituted;
            }
        }

        return returnType;
    }

    /// <summary>
    /// Substitutes the type arguments of a generic resolution type (e.g. <c>Hijacked[U]</c>) using
    /// the member-routine generic parameter map, and returns a new resolution when any argument
    /// changed. Returns null when no substitution occurred.
    /// </summary>
    private TypeSymbol? SubstituteGenericResolutionArgs(TypeSymbol returnType,
        List<string> genericParams, List<TypeSymbol> typeArgs)
    {
        var substitutedArgs = new List<TypeSymbol>();
        bool anySubstituted = false;
        foreach (TypeSymbol typeArg in returnType.TypeArguments!)
        {
            int idx = genericParams.ToList()
                                   .IndexOf(item: typeArg.Name);
            if (idx >= 0 && idx < typeArgs.Count && typeArgs[index: idx] is TypeSymbol sub)
            {
                substitutedArgs.Add(item: sub);
                anySubstituted = true;
            }
            else
            {
                substitutedArgs.Add(item: typeArg);
            }
        }

        if (anySubstituted)
        {
            TypeSymbol? genericDef = GetGenericDefinition(resolution: returnType);
            if (genericDef != null)
            {
                return _registry.GetOrCreateResolution(genericDef: genericDef,
                    typeArguments: substitutedArgs);
            }
        }

        return null;
    }

    /// <summary>
    /// Analyzes a standalone generic FREE-routine call (<c>ptrtoint[Point, U64](p)</c>,
    /// <c>hijacked_from[T](addr)</c>): monomorphizes <paramref name="routine"/> against the explicit type
    /// args, types the arguments through the resulting substitution, and returns the (substituted)
    /// return type.
    /// </summary>
    private TypeSymbol AnalyzeStandaloneGenericRoutineCall(
        GenericMemberRoutineCallExpression generic, IdentifierExpression funcId,
        RoutineInfo routine, List<TypeSymbol> typeArgs)
    {
        // Realm gate for a GENERIC foreign call `LLVM::add[U128](...)` — `funcId` (== generic.Object)
        // carries the `::` qualifier the parser preserved, so enforce it here just like the
        // non-generic free-call path.

        CheckCallRealm(callee: funcId, routine: routine, location: generic.Location);

        // Capture the generic-def shape BEFORE swapping to the resolution: explicit
        // type arguments give a complete substitution map, which (a) types bare-literal
        // arguments via expectedType (`sub[S8](a: 0, ...)` makes 0 an S8, not an
        // UndecidedInteger) and (b) produces a CONCRETE annotated return type. Without
        // this, the call used to annotate ResolvedType=T — harmless for direct emission
        // (intrinsics emit from the explicit TypeArguments) but fatal for any consumer
        // that needs the value's type, e.g. the Maybe-carrier construction inside
        // generated try/check variant bodies.
        Dictionary<string, TypeSymbol>? typeSubs = null;
        List<ParamInfo> declParams = routine.Parameters;
        // Tracks whether the routine is fully monomorphized below. When true, its ReturnType is
        // already the substituted form, so the typeSubs re-substitution further down must be
        // skipped — re-applying the map to an already-substituted return double-wraps nested
        // type args (RF-S301).
        bool routineMonomorphized = false;
        if (routine.IsGenericDefinition)
        {
            if (!TryMonomorphizeFreeRoutine(generic: generic,
                    routine: ref routine,
                    typeArgs: typeArgs,
                    typeSubs: out typeSubs))
            {
                return ErrorTypeSymbol.Instance;
            }

            declParams = routine.Parameters;
            routineMonomorphized = true;
        }

        generic.ResolvedRoutine = routine;
        generic.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);
        generic.IsInFlight = routine.IsInFlightReturn;

        // A failable routine called with its type arguments written out (`serial_decode[T](value: v)`) is a
        // failable call like any other: the enclosing routine can fail through it, and its recovery variant
        // calls the callee's.
        if (routine.IsFailable && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(item: routine);
            if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
                !_currentRoutine.IsSynthesized)
            {
                ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                    message: $"Failable routine '{routine.Name}!' called without error handling. " + UseWhenHint,
                    location: generic.Location);
            }
        }

        if (routine.LlvmIrTemplate is { } template && typeSubs != null)
        {
            CheckIntrinsicConversionTypes(template: template,
                typeSubs: typeSubs,
                genericParameters: (routine.GenericDefinition ?? routine).GenericParameters,
                routineName: funcId.Realm != null ? $"{funcId.Realm}::{funcId.Name}" : funcId.Name,
                location: generic.Location);
        }

        // With concrete type arguments the routine is concrete here (monomorphized, or a pre-built
        // resolution), so its arguments get the same binding, count and type checks as any other call. Only
        // typing them against the expected type let `probe[Text](p: 5_s64)` through, and an array passed
        // where `LLVM::load_element_ref` wants a `Hijacked` pointer reached the emitter as invalid IR.
        // Type arguments that are still generic parameters (a call inside a generic definition's body, like
        // `load_element_ref[Array[T, N], T](me.hijack(), i)`) are checked when that body is monomorphized.
        if (typeArgs.Any(predicate: MentionsGenericParameter) ||
            typeSubs == null && !routineMonomorphized && routine.IsGenericDefinition)
        {
            AnalyzeGenericCallArguments(arguments: generic.Arguments,
                declParams: declParams,
                typeSubs: typeSubs);
        }
        else
        {
            AnalyzeCallArguments(routine: routine, arguments: generic.Arguments, location: generic.Location);
        }

        if (routine.ReturnType == null)
        {
            return _registry.LookupType(name: "None") ?? ErrorTypeSymbol.Instance;
        }

        TypeSymbol returnType = routine.ReturnType;

        // Already monomorphized above → ReturnType is final; re-substituting would double-wrap.
        if (routineMonomorphized)
        {
            return returnType;
        }

        // Explicit type arguments: substitute them through the whole return type
        // (bare T, nested Hijacked[T], tuples, …) in one general pass.
        if (typeSubs != null)
        {
            return SubstituteTypeParams(type: returnType, substitution: typeSubs);
        }

        // Legacy fallback for routines looked up as pre-built resolutions: substitute generic
        // parameters through the return type using the same helper as the member-routine path.
        return SubstituteGenericParamsInReturnType(returnType: returnType,
            memberRoutine: routine,
            typeArgs: typeArgs);
    }

    /// <summary>Whether <paramref name="type"/> is, or has among its type arguments, a generic parameter or an
    /// unparameterized generic definition.</summary>
    private static bool MentionsGenericParameter(TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol || type.IsGenericDefinition ||
               type.TypeArguments is { } args && args.Any(predicate: MentionsGenericParameter);
    }

    private void AnalyzeGenericCallArguments(List<Expression> arguments,
        List<ParamInfo> declParams, Dictionary<string, TypeSymbol>? typeSubs)
    {
        for (int argIdx = 0; argIdx < arguments.Count; argIdx++)
        {
            Expression arg = arguments[index: argIdx];
            ParamInfo? param;
            if (arg is NamedArgumentExpression namedArg)
            {
                param = declParams.FirstOrDefault(predicate: p => p.Name == namedArg.Name);
            }
            else
            {
                param = argIdx < declParams.Count
                    ? declParams[index: argIdx]
                    : null;
            }

            TypeSymbol? expected = null;
            if (param?.Type is { } paramType)
            {
                expected = typeSubs != null
                    ? SubstituteTypeParams(type: paramType, substitution: typeSubs)
                    : paramType;
            }

            AnalyzeExpression(expression: arg, expectedType: expected);
        }
    }

    /// <summary>
    /// Analyzes a generic TYPE CONSTRUCTOR call (<c>Hijacked[U8](addr)</c> / failable
    /// <c>Dict[Text, SerialValue]![...]</c>): resolves the generic def with the type args, binds a
    /// matching <c>create</c> overload (or inline field-init construction), and returns the constructed
    /// type. <paramref name="typeSymbol"/> is the generic definition resolved from <paramref name="typeId"/>.
    /// </summary>
    private TypeSymbol AnalyzeGenericTypeConstructorCall(
        GenericMemberRoutineCallExpression generic, IdentifierExpression typeId, TypeSymbol typeSymbol,
        List<TypeSymbol> typeArgs)
    {
        // Honor an explicit `RF::`/`SF::` realm on a generic construction (`RF::System.List[T]()`): the
        // Object was resolved realm-blind (prefers the file's resolution realm), so inside an SF file
        // `RF::System.List[T]()` would resolve to the SF-realm list and the SF wrapper's constructor
        // `return List[T](inner: RF::System.List[T]())` would self-recurse. Swap the generic DEF to the
        // qualified realm before resolving, so the inner construction reaches the RazorForge list.
        if (typeId.Realm is { } genCtorRealm && typeSymbol.Realm != genCtorRealm &&
            _registry.ReResolveInRealm(type: typeSymbol, realm: genCtorRealm) is TypeSymbol
            {
                IsGenericDefinition: true
            } realmCtorDef)
        {
            typeSymbol = realmCtorDef;
        }

        // The type needs one argument per parameter (`Guarded[T, P]` takes both T and the lock policy P).
        if (typeSymbol.GenericParameters is { } expectedParams && expectedParams.Count != typeArgs.Count)
        {
            ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"You are constructing '{typeSymbol.Name}' with {typeArgs.Count} type argument(s), but it takes " +
                $"{expectedParams.Count}: '{typeSymbol.Name}[{string.Join(separator: ", ", values: expectedParams)}]'. " +
                "Give one type for each.",
                location: generic.Location);
            return ErrorTypeSymbol.Instance;
        }

        // The type's own `needs` constraints (`SplitList[T] needs T obeys Splittable`) on a concrete
        // construction. Inside a template (`EnumerateIterable[T, Me]`) the arguments are still open and are
        // checked where the template is used.
        if (typeArgs.All(predicate: Declaration.TypeRegistry.IsFullyConcrete))
        {
            ValidateGenericConstraints(genericDef: typeSymbol,
                typeArgs: typeArgs.ToList(),
                location: generic.Location);
        }

        // Resolve the generic type with the provided type arguments
        TypeSymbol resolvedType = _registry.GetOrCreateResolution(genericDef: typeSymbol,
            typeArguments: typeArgs.ToList());
        generic.ConstructedType = resolvedType;
        generic.LoweringKind = ClassifyConstruction(type: resolvedType);
        ZeroFillCheck.Check(rules: _registry.CompilationRules, constructed: resolvedType, argumentCount: generic.Arguments.Count, location: generic.Location, report: ReportError);

        // Named arguments see the type they will land in as their expected type, so a literal adapts: the
        // parameters of a written creator called by exactly these names (`List[Text](count: 2, fill_value:
        // "ab")` takes an `Integer` count even though the list's `count` field is a `U64`), else the fields
        // of a field-init.
        Dictionary<string, TypeSymbol>? fieldTypeByName =
            BuildCreatorParameterTypeMap(generic: generic, resolvedType: resolvedType) ??
            BuildFieldTypeMap(resolvedType: resolvedType);
        List<TypeSymbol> argTypes =
            AnalyzeGenericConstructorArgs(generic: generic, fieldTypeByName: fieldTypeByName);
        return ResolveGenericConstructorResult(generic: generic,
            resolvedType: resolvedType,
            argTypes: argTypes);
    }

    /// <summary>
    /// The parameter types of the written creators of <paramref name="resolvedType"/> whose parameter names are
    /// exactly the call's argument names, by name. Null when the arguments are not all named, no written creator
    /// takes those names, or the creators that do disagree on a parameter's type.
    /// </summary>
    private Dictionary<string, TypeSymbol>? BuildCreatorParameterTypeMap(
        GenericMemberRoutineCallExpression generic, TypeSymbol resolvedType)
    {
        if (generic.Arguments.Count == 0 ||
            !generic.Arguments.All(predicate: a => a is NamedArgumentExpression))
        {
            return null;
        }

        var names = generic.Arguments.Cast<NamedArgumentExpression>()
                           .Select(selector: a => a.Name)
                           .ToHashSet(comparer: StringComparer.Ordinal);
        var candidates = new List<RoutineInfo>();
        _registry.CollectCreatorCandidates(type: resolvedType, candidates: candidates);
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: resolvedType)
                                                 .Where(predicate: m => m.IsCreator));
        Dictionary<string, TypeSymbol>? map = null;
        foreach (RoutineInfo creator in candidates.Where(predicate: m =>
                     !m.IsSynthesized && m.Parameters.Count == names.Count &&
                     m.Parameters.All(predicate: p => names.Contains(item: p.Name))))
        {
            map ??= new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
            foreach (ParamInfo parameter in creator.Parameters)
            {
                TypeSymbol type = resolvedType is { IsGenericResolution: true, TypeArguments: not null }
                    ? SubstituteTypeParameters(type: parameter.Type, genericType: resolvedType)
                    : parameter.Type;
                if (map.TryGetValue(key: parameter.Name, value: out TypeSymbol? seen) &&
                    seen.FullName != type.FullName)
                {
                    return null;
                }

                map[key: parameter.Name] = type;
            }
        }

        return map;
    }

    /// <summary>
    /// Builds a field-name to field-type map for <paramref name="resolvedType"/> so that named
    /// constructor arguments see the correct contextual expected type. Generic type parameters in
    /// field types are substituted using the type's own type arguments. Returns null when the type
    /// has no member variables (e.g. a wrapper type or scalar).
    /// </summary>
    private Dictionary<string, TypeSymbol>? BuildFieldTypeMap(TypeSymbol resolvedType)
    {
        List<MemberVariableInfo>? memberVars = resolvedType switch
        {
            RecordTypeSymbol r => r.MemberVariables,
            EntityTypeSymbol e => e.MemberVariables,
            _ => null
        };
        if (memberVars == null)
        {
            return null;
        }

        var map = new Dictionary<string, TypeSymbol>();
        foreach (MemberVariableInfo mv in memberVars)
        {
            TypeSymbol ft = resolvedType is { IsGenericResolution: true, TypeArguments: not null }
                ? SubstituteTypeParameters(type: mv.Type, genericType: resolvedType)
                : mv.Type;
            map[key: mv.Name] = ft;
        }

        return map;
    }

    /// <summary>
    /// Analyzes each argument of a generic constructor call, using
    /// <paramref name="fieldTypeByName"/> to supply the expected type for named arguments.
    /// Returns the list of resolved argument types.
    /// </summary>
    private List<TypeSymbol> AnalyzeGenericConstructorArgs(
        GenericMemberRoutineCallExpression generic,
        Dictionary<string, TypeSymbol>? fieldTypeByName)
    {
        var argTypes = new List<TypeSymbol>(capacity: generic.Arguments.Count);
        foreach (Expression arg in generic.Arguments)
        {
            TypeSymbol? expectedArgType = null;
            if (fieldTypeByName != null && arg is NamedArgumentExpression named &&
                fieldTypeByName.TryGetValue(key: named.Name, value: out TypeSymbol? ft))
            {
                expectedArgType = ft;
            }

            argTypes.Add(item: AnalyzeExpression(expression: arg, expectedType: expectedArgType));
        }

        return argTypes;
    }

    /// <summary>
    /// Validates that all constructor arguments are named when the type has 2+ fields,
    /// and returns the constructed type (or the creator's return type when a matching overload exists).
    /// </summary>
    private TypeSymbol ResolveGenericConstructorResult(GenericMemberRoutineCallExpression generic,
        TypeSymbol resolvedType, List<TypeSymbol> argTypes)
    {
        RoutineInfo? creator =
            _registry.LookupCreatorOverload(type: resolvedType, argTypes: argTypes);
        if (creator != null && creator.Parameters.Count == argTypes.Count &&
            !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
        {
            CheckCreatorArgumentNames(arguments: generic.Arguments, creator: creator, type: resolvedType);
            generic.ResolvedRoutine = creator;
            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                location: generic.Location);
            // Prefer the concrete resolvedType over the creator's return type when that type is still
            // generic (contains GenericParameterTypeSymbol placeholders), to avoid unresolved type leaking
            // to downstream callers.
            bool returnTypeIsGenericOrUnresolved =
                creator.ReturnType is null or { IsGenericDefinition: true } ||
                creator.ReturnType.TypeArguments?.Any(
                    predicate: t => t is GenericParameterTypeSymbol) == true;
            return returnTypeIsGenericOrUnresolved
                ? resolvedType
                : creator.ReturnType!;
        }

        // A type the backend holds as one value (`BitArray[8]`) has no member variables to give values to: several
        // positional values are its literal, which goes into a declared variable of the type.
        if (resolvedType is RecordTypeSymbol { BackendType: not null } && generic.Arguments.Count > 1 &&
            generic.Arguments.All(predicate: a => a is not NamedArgumentExpression))
        {
            ReportError(code: SemanticDiagnosticCode.TypeNotCallable,
                message: $"'{resolvedType.Name}' is not built from its values in a call. Write them as a literal into " +
                         $"a variable of that type: `var bits: {resolvedType.Name} = [...]`.",
                location: generic.Location);
            return ErrorTypeSymbol.Instance;
        }

        int memberCount = resolvedType switch
        {
            EntityTypeSymbol e => e.MemberVariables.Count,
            RecordTypeSymbol r => r.MemberVariables.Count,
            _ => 0
        };
        if (memberCount >= 2)
        {
            foreach (Expression arg in generic.Arguments.Where(predicate: a =>
                         a is not NamedArgumentExpression))
            {
                ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                    message:
                    $"Type '{resolvedType.Name}' has {memberCount} fields - all constructor arguments must be named.",
                    location: arg.Location);
            }
        }

        // A field-init construction (every argument named): the same checks as a non-generic one, so a
        // generic body can't put a `Hijacked[U8]` into a `Hijacked[T]` field, name a field the type lacks, or
        // leave a required one out. A positional argument is a wrapper's single field (`Hijacked[T](me)`).
        if (generic.Arguments.Count > 0 && generic.Arguments.All(predicate: a => a is NamedArgumentExpression))
        {
            ValidateCreatorMemberVariables(type: resolvedType,
                memberVariables: generic.Arguments
                                        .Cast<NamedArgumentExpression>()
                                        .Select(selector: named => (named.Name, named.Value))
                                        .ToList(),
                location: generic.Location,
                analyzedTypes: argTypes);
        }

        ValidateExclusiveTokenUniqueness(arguments: generic.Arguments, location: generic.Location);
        return resolvedType;
    }

    private TypeSymbol AnalyzeGenericMemberExpression(GenericMemberExpression genericMember)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        var resolvedTypeArgs = new List<TypeSymbol>(capacity: genericMember.TypeArguments.Count);
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            resolvedTypeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // Typewise receiver: `Ident[T]` parsed as GenericMemberExpression(Ident, Ident.Name, [T])
        // — the parser sets MemberName == Object.Name when the source was `Ident[Args]`, not
        // `obj.field[i]`. When that holds and Object resolves to a generic type, return the
        // monomorphized type so the outer `.MemberRoutine()` call has a proper typewise receiver type.
        if (genericMember.Object is IdentifierExpression idReceiver &&
            idReceiver.Name == genericMember.MemberName && objectType.IsGenericDefinition &&
            resolvedTypeArgs.Count == objectType.GenericParameters?.Count)
        {
            TypeSymbol resolved = _registry.GetOrCreateResolution(genericDef: objectType,
                typeArguments: resolvedTypeArgs);
            genericMember.ResolvedType = resolved;
            return resolved;
        }

        // Look up the member on the object type
        List<MemberVariableInfo>? memberVars = objectType switch
        {
            EntityTypeSymbol e => e.MemberVariables,
            RecordTypeSymbol r => r.MemberVariables,
            _ => null
        };
        MemberVariableInfo? memberVar =
            memberVars?.FirstOrDefault(predicate: mv => mv.Name == genericMember.MemberName);
        if (memberVar != null)
        {
            return AnalyzeGenericMemberIndexing(genericMember: genericMember,
                memberVar: memberVar);
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Handles the case where a <c>GenericMemberExpression</c>'s bracketed args are actually INDEX
    /// values into a member variable's collection value (not type arguments). Analyzes the index
    /// expressions and returns the element type (collection first type-arg, else <c>getitem</c>'s
    /// return type, else the member type itself).
    /// </summary>
    private TypeSymbol AnalyzeGenericMemberIndexing(GenericMemberExpression genericMember,
        MemberVariableInfo memberVar)
    {
        // Member found — the [args] are indexing into the member's value.
        // Analyze the "type arguments" as expressions (they're actually index values).
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            // The type arg's Name is actually a variable name — analyze it as identifier
            if (typeArg.Name != null)
            {
                AnalyzeExpression(expression: new IdentifierExpression(Name: typeArg.Name,
                    Location: typeArg.Location));
            }
        }

        // Determine the element type of the member's collection type
        TypeSymbol? memberType = memberVar.Type;
        if (memberType is { TypeArguments: { Count: > 0 } })
        {
            // e.g., List[SortedDict[K,V]]  element is SortedDict[K,V]
            return memberType.TypeArguments[index: 0];
        }

        // If the member type has a getitem memberRoutine, use its return type
        RoutineInfo? getItem =
            _registry.LookupMemberRoutine(type: memberType, memberRoutineName: "getitem");
        if (getItem?.ReturnType != null)
        {
            return getItem.ReturnType;
        }

        return memberType;
    }

    private TypeSymbol AnalyzeIsPatternExpression(IsPatternExpression isPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: isPat.Expression);

        // `is` matches a variant TYPE only. A choice case is matched with `==`; a flags membership with
        // `have`/`lack`. Steer either off `is`.
        switch (exprType)
        {
            case ChoiceTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                    message:
                    $"'is' cannot match a choice case on '{exprType.Name}'. Use '== {isPat.Pattern switch { TypePattern tp => tp.Type.Name, _ => "CASE" }}' (or '!=') for case matching.",
                    location: isPat.Location);
                return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
            case FlagsTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                    message:
                    $"'is' cannot test flags on '{exprType.Name}'. Use '.has({isPat.Pattern switch { TypePattern tp => tp.Type.Name, _ => "FLAG" }})' / '.lacks(…)' for membership.",
                    location: isPat.Location);
                return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        // Analyze the pattern (may bind variables)
        AnalyzePattern(pattern: isPat.Pattern, matchedType: exprType);

        // 'is' expressions always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
    }


    /// <summary>
    /// Reports RF-S (FlagsMemberNotFound) for each name in <paramref name="flagNames"/> that is not a
    /// member of <paramref name="flagsType"/>.
    /// </summary>
    private void ValidateFlagNamesExist(FlagsTypeSymbol flagsType, IEnumerable<string> flagNames,
        SourceLocation location)
    {
        foreach (string flagName in flagNames.Where(predicate: n =>
                     flagsType.Members.All(predicate: m => m.Name != n)))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                message:
                $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Resolves the generic DEFINITION backing an owner type (its <c>GenericDefinition</c>, or the type
    /// itself when it already IS a generic definition), or null when the owner is not generic. Shared by
    /// the owner-binding-map / owner-parameter-name walkers.
    /// </summary>
    private static TypeSymbol? ResolveGenericOwnerDefinition(TypeSymbol ownerType)
    {
        return ownerType switch
        {
            RecordTypeSymbol r => r.GenericDefinition ?? (r.IsGenericDefinition
                ? r
                : null),
            EntityTypeSymbol e => e.GenericDefinition ?? (e.IsGenericDefinition
                ? e
                : null),
            ProtocolTypeSymbol p => p.GenericDefinition ?? (p.IsGenericDefinition
                ? p
                : null),
            _ => ownerType.IsGenericDefinition
                ? ownerType
                : null
        };
    }

    private static Dictionary<string, TypeSymbol> BuildOwnerBindingMap(TypeSymbol? ownerType)
    {
        var map = new Dictionary<string, TypeSymbol>();
        if (ownerType == null)
        {
            return map;
        }

        TypeSymbol? def = ResolveGenericOwnerDefinition(ownerType: ownerType);

        List<string>? paramNames = def?.GenericParameters ?? ownerType.GenericParameters;
        List<TypeSymbol>? args = ownerType.TypeArguments;

        if (paramNames != null && args != null)
        {
            for (int i = 0; i < paramNames.Count && i < args.Count; i++)
            {
                map[key: paramNames[index: i]] = args[index: i];
            }

            return map;
        }

        // Fallback: receiver is an unsubstituted generic instance like Hijacked[T] inside its own
        // body. Map each param name to a same-named GenericParameterTypeSymbol placeholder.
        if (paramNames != null)
        {
            foreach (string p in paramNames)
            {
                map[key: p] = new GenericParameterTypeSymbol(name: p);
            }
        }

        return map;
    }

    private static HashSet<string> GetOwnerGenericParameterNames(TypeSymbol? ownerType)
    {
        var names = new HashSet<string>();
        if (ownerType == null)
        {
            return names;
        }

        TypeSymbol? def = ResolveGenericOwnerDefinition(ownerType: ownerType);

        if (def?.GenericParameters != null)
        {
            foreach (string p in def.GenericParameters)
            {
                names.Add(item: p);
            }
        }
        else if (ownerType.GenericParameters != null)
        {
            foreach (string p in ownerType.GenericParameters)
            {
                names.Add(item: p);
            }
        }

        return names;
    }

    private ErrorTypeSymbol HandleUnknownExpression(Expression expression)
    {
        ReportWarning(code: SemanticWarningCode.UnknownExpressionType,
            message:
            $"Internal: semantic analyzer has no handler for AST node '{expression.GetType().Name}'. This expression will be skipped; downstream type info may be incomplete. Please report as a builder bug.",
            location: expression.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>The LLVM cast opcodes an <c>@llvm_ir</c> conversion template can carry, keyed by opcode:
    /// the scalar kind each side must be (integer or floating point) and how the target width must
    /// relate to the source width.</summary>
    private static readonly Dictionary<string, (bool FromFloat, bool ToFloat, int WidthOrder)>
        IntrinsicConversionRules = new(comparer: StringComparer.Ordinal)
        {
            ["trunc"] = (false, false, -1),
            ["zext"] = (false, false, 1),
            ["sext"] = (false, false, 1),
            ["fptrunc"] = (true, true, -1),
            ["fpext"] = (true, true, 1),
            ["sitofp"] = (false, true, 2),
            ["uitofp"] = (false, true, 2),
            ["fptosi"] = (true, false, 2),
            ["fptoui"] = (true, false, 2)
        };

    /// <summary>
    /// Rejects an <c>LLVM::</c> conversion whose concrete type arguments its cast cannot take:
    /// <c>int_truncate[U32, U64]</c> would otherwise reach the emitter as <c>trunc i32 ... to i64</c>,
    /// which opt refuses long after the call was written. Only templates of the shape
    /// <c>{result} = OPCODE {source} {value} to {target}</c> are checked, where the source and target are the
    /// routine's first and second type parameters (by position, whatever they are called), and only when both
    /// sides are scalar LLVM types; <c>bitcast</c> needs equal widths.
    /// </summary>
    private void CheckIntrinsicConversionTypes(string template, Dictionary<string, TypeSymbol> typeSubs,
        List<string>? genericParameters, string routineName, SourceLocation location)
    {
        if (genericParameters is not { Count: 2 } || genericParameters[0] is not { } sourceName ||
            genericParameters[1] is not { } targetName)
        {
            return;
        }

        const string prefix = "{result} = ";
        string middle = $" {{{sourceName}}} {{value}} to {{{targetName}}}";
        string body = template.Trim();
        if (!body.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) ||
            !body.EndsWith(value: middle, comparisonType: StringComparison.Ordinal))
        {
            return;
        }

        string opcode = body[prefix.Length..^middle.Length];
        if (!typeSubs.TryGetValue(key: sourceName, value: out TypeSymbol? from) ||
            !typeSubs.TryGetValue(key: targetName, value: out TypeSymbol? to) ||
            from is not RecordTypeSymbol fromRecord || to is not RecordTypeSymbol toRecord ||
            !TryGetScalarLlvmWidth(llvmType: fromRecord.LlvmType, width: out int fromWidth, isFloat: out bool fromFloat) ||
            !TryGetScalarLlvmWidth(llvmType: toRecord.LlvmType, width: out int toWidth, isFloat: out bool toFloat))
        {
            return;
        }

        (string Problem, string Fix)? mismatch;
        if (opcode == "bitcast")
        {
            mismatch = fromWidth == toWidth
                ? null
                : ($"it reinterprets the bits of {Article.For(name: fromWidth.ToString())} {fromWidth}-bit '{from.Name}' as {Article.For(name: toWidth.ToString())} {toWidth}-bit '{to.Name}', and a bit reinterpretation needs both types to be the same size",
                    "Widen or narrow the value first so both sides have the same width");
        }
        else if (IntrinsicConversionRules.TryGetValue(key: opcode, value: out var rule))
        {
            mismatch = DescribeConversionProblem(rule: rule,
                from: from.Name,
                fromWidth: fromWidth,
                fromFloat: fromFloat,
                to: to.Name,
                toWidth: toWidth,
                toFloat: toFloat);
        }
        else
        {
            return;
        }

        if (mismatch is not { } found)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.IntrinsicConversionTypeMismatch,
            message: $"'{routineName}[{from.Name}, {to.Name}]' cannot be built: {found.Problem}. {found.Fix}.",
            location: location);
    }

    private static (string Problem, string Fix)? DescribeConversionProblem((bool FromFloat, bool ToFloat, int WidthOrder) rule,
        string from, int fromWidth, bool fromFloat, string to, int toWidth, bool toFloat)
    {
        const string kindFix = "Use the conversion made for that pair of integer and floating-point types";
        if (rule.FromFloat != fromFloat)
        {
            return ($"it takes {(rule.FromFloat ? "a floating-point" : "an integer")} source, and '{from}' is {(fromFloat ? "floating-point" : "an integer")}",
                kindFix);
        }

        if (rule.ToFloat != toFloat)
        {
            return ($"it produces {(rule.ToFloat ? "a floating-point" : "an integer")} value, and '{to}' is {(toFloat ? "floating-point" : "an integer")}",
                kindFix);
        }

        const string widthFix = "Swap the type arguments if they are reversed, or use the conversion that goes the other way";
        return rule.WidthOrder switch
        {
            -1 when toWidth >= fromWidth =>
                ($"it narrows, and the {toWidth}-bit '{to}' is not narrower than the {fromWidth}-bit '{from}'", widthFix),
            1 when toWidth <= fromWidth =>
                ($"it widens, and the {toWidth}-bit '{to}' is not wider than the {fromWidth}-bit '{from}'", widthFix),
            _ => null
        };
    }

    /// <summary>The bit width of a scalar LLVM type (<c>iN</c>, <c>half</c>, <c>bfloat</c>, <c>float</c>,
    /// <c>double</c>, <c>x86_fp80</c>, <c>fp128</c>, <c>ppc_fp128</c>); false for anything else.</summary>
    private static bool TryGetScalarLlvmWidth(string llvmType, out int width, out bool isFloat)
    {
        isFloat = true;
        width = llvmType switch
        {
            "half" or "bfloat" => 16,
            "float" => 32,
            "double" => 64,
            "x86_fp80" => 80,
            "fp128" or "ppc_fp128" => 128,
            _ => 0
        };
        if (width != 0)
        {
            return true;
        }

        isFloat = false;
        return llvmType.Length > 1 && llvmType[0] == 'i' &&
               int.TryParse(s: llvmType.AsSpan(start: 1), result: out width) && width > 0;
    }
}
