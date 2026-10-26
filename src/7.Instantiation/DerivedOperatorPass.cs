using Builder.Tokenizer;
using Builder.Declaration;
using Builder.Verification.Enums;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Phase 2.6: Generates derived comparison operators from eq and compare routines,
/// and synthesizes crash_title() bodies for all crashable types.
/// </summary>
internal sealed class DerivedOperatorPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;

    /// <summary>Synthetic source location used for builder-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "",
        Line: 0,
        Column: 0,
        Position: 0);

    public DerivedOperatorPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        List<SemanticError> errors)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
    }

    /// <summary>
    /// Generates derived comparison operators from eq and compare routines.
    /// </summary>
    public void Run()
    {
        foreach (TypeSymbol type in _registry.GetTypesWithMemberRoutines())
        {
            GenerateDerivedOperatorsForType(type: type);
        }

        // Synthesize crash_title() bodies for all crashable types
        foreach (TypeSymbol type in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            RoutineInfo? titleMemberRoutine = _registry.GetMemberRoutinesForType(type: type)
                                                       .FirstOrDefault(predicate: m =>
                                                            m.Name == "crash_title");
            if (titleMemberRoutine == null || !titleMemberRoutine.IsSynthesized)
            {
                continue;
            }

            string title = CrashableTypeSymbol.SynthesizeCrashTitle(typeName: type.Name);
            var titleBody = new BlockStatement(
                Statements:
                [
                    new ReturnStatement(Value: new LiteralExpression(Value: title,
                            LiteralType: TokenType.TextLiteral,
                            Location: _synthLoc),
                        Location: _synthLoc)
                ],
                Location: _synthLoc);

            _synthesizedBodies[key: titleMemberRoutine.RegistryKey] =
                (titleMemberRoutine, titleBody);
        }
    }

    /// <summary>
    /// Generates derived operators for a specific type.
    /// </summary>
    private void GenerateDerivedOperatorsForType(TypeSymbol type)
    {
        IEnumerable<RoutineInfo> memberRoutines = _registry.GetMemberRoutinesForType(type: type);
        var memberRoutineList = memberRoutines.ToList();

        // Every eq overload gets its own ne: `Int.eq(you: Int)` and `Int.eq(you: Decimal)` are two
        // routines, and a ne made from only one of them would take the other's argument type.
        foreach (RoutineInfo eqMemberRoutine in memberRoutineList.Where(predicate: m => m.Name == "eq").ToList())
        {
            GenerateNeFromEq(type: type,
                eqMemberRoutine: eqMemberRoutine,
                existingMemberRoutines: memberRoutineList);
        }

        // lt/le/gt/ge are NOT generated here: they are registered by the everywhere-derive pass and their
        // bodies materialized by the collector from the DeriveText.rf `T.lt/le/gt/ge` universal templates
        // (`me.compare(you) == ME_SMALL` etc.). Routing them through the template mechanism — the same path as
        // compare/represent — is what makes them survive the collector/warm codegen path (the C#-synthesized
        // bodies here did not), so `a < b` resolves uniformly for Char/numerics/records.

        foreach (RoutineInfo containsMemberRoutine in memberRoutineList.Where(predicate: m => m.Name == "has")
                    .ToList())
        {
            GenerateNotContainsFromContains(type: type,
                containsMemberRoutine: containsMemberRoutine,
                existingMemberRoutines: memberRoutineList);
        }

    }

    /// <summary>
    /// Generates ne from eq.
    /// ne(you) = not me.eq(you: you)
    /// </summary>
    private void GenerateNeFromEq(TypeSymbol type, RoutineInfo eqMemberRoutine,
        List<RoutineInfo> existingMemberRoutines)
    {
        RoutineInfo? existingNe = existingMemberRoutines.FirstOrDefault(predicate: m =>
            m.Name == "ne" && SameParameterTypes(a: m, b: eqMemberRoutine));

        if (existingNe != null)
        {
            // User provided their own implementation — it takes priority over generated.
            // This is expected behavior for @generated protocol routines (#179).
            return;
        }

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var neMemberRoutine = new RoutineInfo(name: "ne")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = eqMemberRoutine.Parameters,
            ReturnType = boolType,
            IsFailable = eqMemberRoutine.IsFailable,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            // Inherit `eq`'s generic-parameter constraints so `ne` is only available
            // for the same instantiations as `eq`. Without this, Array[T,N].ne is
            // unconditionally derivable even when eq requires `T obeys Equatable`,
            // and the synthesized `ne` body references a non-existent `eq` at link
            // time for instantiations that fail the constraint (e.g. Array[X,N]).
            GenericParameters = eqMemberRoutine.GenericParameters,
            GenericConstraints = eqMemberRoutine.GenericConstraints,
            Visibility = eqMemberRoutine.Visibility,
            Location = eqMemberRoutine.Location,
            Module = eqMemberRoutine.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: neMemberRoutine);

        // Build AST body: return not me.eq(you: you)
        string paramName = eqMemberRoutine.Parameters.Count > 0
            ? eqMemberRoutine.Parameters[index: 0].Name
            : "you";
        BlockStatement neBody = BuildNegatedDelegateBody(ownerType: type,
            delegateMemberRoutine: eqMemberRoutine,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: neMemberRoutine.RegistryKey] = (neMemberRoutine, neBody);
    }

    /// <summary>
    /// Generates notcontains from contains.
    /// notcontains(item) = not me.contains(item: item)
    /// </summary>
    private void GenerateNotContainsFromContains(TypeSymbol type,
        RoutineInfo containsMemberRoutine, List<RoutineInfo> existingMemberRoutines)
    {
        RoutineInfo? existingNotContains = existingMemberRoutines.FirstOrDefault(predicate: m =>
            m.Name == "lacks" && SameParameterTypes(a: m, b: containsMemberRoutine));

        if (existingNotContains != null)
        {
            return;
        }

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var notContainsMemberRoutine = new RoutineInfo(name: "lacks")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = containsMemberRoutine.Parameters,
            ReturnType = boolType,
            IsFailable = containsMemberRoutine.IsFailable,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            // Inherit `contains`'s constraints so `notcontains` is only available for
            // the same instantiations.
            GenericParameters = containsMemberRoutine.GenericParameters,
            GenericConstraints = containsMemberRoutine.GenericConstraints,
            Visibility = containsMemberRoutine.Visibility,
            Location = containsMemberRoutine.Location,
            Module = containsMemberRoutine.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: notContainsMemberRoutine);

        // Build AST body: return not me.contains(item: item)
        string paramName = containsMemberRoutine.Parameters.Count > 0
            ? containsMemberRoutine.Parameters[index: 0].Name
            : "item";
        BlockStatement notContainsBody = BuildNegatedDelegateBody(ownerType: type,
            delegateMemberRoutine: containsMemberRoutine,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: notContainsMemberRoutine.RegistryKey] =
            (notContainsMemberRoutine, notContainsBody);
    }

    /// <summary>
    /// Whether two routines take the same parameter types in the same order.
    /// </summary>
    private static bool SameParameterTypes(RoutineInfo a, RoutineInfo b)
    {
        return a.Parameters.Count == b.Parameters.Count && a.Parameters
                                                            .Zip(second: b.Parameters)
                                                            .All(predicate: pair =>
                                                                 pair.First.Type.Name == pair.Second.Type.Name);
    }

    /// <summary>
    /// Builds: return not me.{memberRoutineName}({paramName}: {paramName})
    /// </summary>
    private static BlockStatement BuildNegatedDelegateBody(TypeSymbol ownerType,
        RoutineInfo delegateMemberRoutine, TypeSymbol boolType, string paramName)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
        {
            ResolvedType = ownerType
        };
        var call = new CallExpression(
            Callee: new MemberExpression(Object: meRef,
                MemberName: delegateMemberRoutine.Name,
                Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(Name: paramName,
                    Value: new IdentifierExpression(Name: paramName, Location: _synthLoc),
                    Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            // The builder's own spelling of `!=` (or `lack`): it may call `eq` on a choice or flags value, which
            // written source may not.
            ResolvedRoutine = delegateMemberRoutine, ResolvedType = boolType, IsSynthesizedLowering = true
        };

        var falseVal =
            new LiteralExpression(Value: false, LiteralType: TokenType.False, Location: _synthLoc)
            {
                ResolvedType = boolType
            };
        var trueVal =
            new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: _synthLoc)
            {
                ResolvedType = boolType
            };
        // Codegen can't handle ConditionalExpression or UnaryNot on synthesized bodies
        // (ExpressionLoweringPass only runs on source AST). Use if-return instead.
        return new BlockStatement(Statements:
            [
                new IfStatement(Condition: call,
                    ThenStatement: new ReturnStatement(Value: falseVal, Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc),
                new ReturnStatement(Value: trueVal, Location: _synthLoc)
            ],
            Location: _synthLoc);
    }
}
