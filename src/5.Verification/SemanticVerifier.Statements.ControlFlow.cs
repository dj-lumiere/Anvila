using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification.Enums;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private void AnalyzeIfStatement(IfStatement ifStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: ifStmt.Condition);

        // Condition must be boolean. An ErrorTypeSymbol condition is either a sub-expression that already
        // reported its own error (piling "must be boolean" on top is pure cascade noise) OR a buildtime
        // splice deferred to monomorphization — e.g. `if me.${m.name}.is_none()` inside an `expand`, where
        // the splice-member is ErrorType pre-monomorph and the real Bool only exists per concrete field.
        // Either way, suppress the boolean check for ErrorType and let the deferred/errored path settle.
        if (!IsBoolType(type: conditionType) && conditionType is not ErrorTypeSymbol)
        {
            ReportError(code: SemanticDiagnosticCode.IfConditionNotBool,
                message: $"If condition must be boolean, got '{conditionType.Name}'.",
                location: ifStmt.Condition.Location);
        }

        // Extract narrowing info from condition: carrier/None (NarrowingInfo) + general user variant
        // (VariantIsNarrowing, which accumulates arm exclusions down an if/elseif chain).
        NarrowingInfo? narrowing = TryExtractNarrowingFromCondition(condition: ifStmt.Condition);
        VariantIsNarrowing? variantNarrowing =
            TryGetVariantIsNarrowing(condition: ifStmt.Condition);

        // ── Deadref (steal) flow across the branches ──────────────────────────────
        // `steal x` marks x dead for the REST of the current linear scope (_deadrefVariables
        // is flow-insensitive). But a steal in a branch that definitely EXITS
        // (return/throw/break/continue) must NOT leak onto the path taken when that branch did
        // not run — e.g. `var v = ...; if c: return steal v; use(v)` keeps `v` alive on the
        // fall-through. Snapshot the dead set, analyze each branch from the same pre-if state,
        // then keep only the deadrefs reaching past the `if` (from branches that fall through).
        var deadrefBefore = new HashSet<string>(collection: _deadrefVariables);

        // Analyze then branch (with narrowing if applicable)
        AnalyzeIfThenBranch(ifStmt: ifStmt,
            narrowing: narrowing,
            variantNarrowing: variantNarrowing);

        bool thenExits = ControlFlowExits.HasDefiniteExit(statement: ifStmt.ThenStatement);
        var afterThen = new HashSet<string>(collection: _deadrefVariables);
        // Re-analyze the else branch from the pre-if dead set — the branches are mutually
        // exclusive, so the else must not see the then branch's steals.
        _deadrefVariables.Clear();
        _deadrefVariables.UnionWith(other: deadrefBefore);

        // Analyze else branch if present (with inverse narrowing if applicable)
        if (ifStmt.ElseStatement != null)
        {
            AnalyzeIfElseBranch(ifStmt: ifStmt,
                narrowing: narrowing,
                variantNarrowing: variantNarrowing);
        }

        bool elseExits = ifStmt.ElseStatement != null &&
                         ControlFlowExits.HasDefiniteExit(statement: ifStmt.ElseStatement);
        var afterElse = new HashSet<string>(collection: _deadrefVariables);
        CheckTakenOnEveryPath(paths: [(afterThen, thenExits), (afterElse, elseExits)], before: deadrefBefore,
            construct: "if", at: ifStmt.Location);

        // Merge: a variable is dead after the `if` iff it is dead on some path that FALLS
        // THROUGH. Steals confined to an exiting branch are dropped. (With no else branch,
        // `afterElse` is the pre-if state, i.e. the condition-false fall-through.)
        _deadrefVariables.Clear();
        if (!thenExits)
        {
            _deadrefVariables.UnionWith(other: afterThen);
        }

        if (!elseExits)
        {
            _deadrefVariables.UnionWith(other: afterElse);
        }

        // Guard clause narrowing: if the then branch definitely exits,
        // apply else narrowing to the remainder of the current scope
        if (ifStmt.ElseStatement == null && ControlFlowExits.HasDefiniteExit(statement: ifStmt.ThenStatement))
        {
            ApplyGuardClauseNarrowing(narrowing: narrowing, variantNarrowing: variantNarrowing);
        }
    }

    /// <summary>
    /// Reports a binding from outside a branching statement that <c>steal</c> takes on some of the paths going on past
    /// it and not on others (RF-S616). Only the bindings still in scope after it count: one declared inside a branch
    /// ends with that branch anyway.
    /// </summary>
    private void CheckTakenOnEveryPath(List<(HashSet<string> Dead, bool Exits)> paths, HashSet<string> before,
        string construct, SourceLocation at)
    {
        List<HashSet<string>> onward = paths.Where(predicate: p => !p.Exits)
                                            .Select(selector: p => p.Dead)
                                            .ToList();
        if (onward.Count < 2)
        {
            return;
        }

        foreach (string name in onward.SelectMany(selector: d => d)
                                      .Where(predicate: n => !before.Contains(item: n))
                                      .Distinct()
                                      .Order(comparer: StringComparer.Ordinal))
        {
            if (_registry.LookupVariable(name: name) is null || onward.All(predicate: d => d.Contains(item: name)))
            {
                continue;
            }

            ReportError(code: SemanticDiagnosticCode.StealOnSomePaths,
                message: $"'{name}' is {EndedHow(name: name)} on some paths through this '{construct}' and kept on " +
                         $"others. What a scope holds is torn down at its end by one rule, so end '{name}' on every " +
                         $"path that goes on past the '{construct}', or on none.",
                location: at);
        }
    }

    /// <summary>
    /// Analyzes the then-branch of an if statement, opening a narrowing scope when the condition
    /// narrows a variable's type / non-null state / variant arm.
    /// </summary>
    private void AnalyzeIfThenBranch(IfStatement ifStmt, NarrowingInfo? narrowing,
        VariantIsNarrowing? variantNarrowing)
    {
        if (narrowing?.ThenBranchType != null || narrowing is { ThenNonNull: true } ||
            variantNarrowing != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "if_then");
            if (narrowing?.ThenBranchType != null)
            {
                _registry.NarrowVariable(name: narrowing.VariableName,
                    narrowedType: narrowing.ThenBranchType);
            }

            if (narrowing is { ThenNonNull: true })
            {
                _registry.MarkVariableNonNull(name: narrowing.VariableName);
            }

            if (variantNarrowing != null)
            {
                ApplyVariantNarrowing(vn: variantNarrowing, conditionTrue: true);
            }

            AnalyzeStatement(statement: ifStmt.ThenStatement);
            _registry.ExitScope();
        }
        else
        {
            AnalyzeStatement(statement: ifStmt.ThenStatement);
        }
    }

    /// <summary>
    /// Analyzes the else-branch of an if statement, opening an inverse-narrowing scope when the
    /// condition narrows a variable's type / non-null state / variant arm.
    /// </summary>
    private void AnalyzeIfElseBranch(IfStatement ifStmt, NarrowingInfo? narrowing,
        VariantIsNarrowing? variantNarrowing)
    {
        if (narrowing?.ElseBranchType != null || narrowing is { ElseNonNull: true } ||
            variantNarrowing != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "if_else");
            if (narrowing?.ElseBranchType != null)
            {
                _registry.NarrowVariable(name: narrowing.VariableName,
                    narrowedType: narrowing.ElseBranchType);
            }

            if (narrowing is { ElseNonNull: true })
            {
                _registry.MarkVariableNonNull(name: narrowing.VariableName);
            }

            if (variantNarrowing != null)
            {
                ApplyVariantNarrowing(vn: variantNarrowing, conditionTrue: false);
            }

            AnalyzeStatement(statement: ifStmt.ElseStatement!);
            _registry.ExitScope();
        }
        else
        {
            AnalyzeStatement(statement: ifStmt.ElseStatement!);
        }
    }

    /// <summary>
    /// Applies inverse (else) narrowing to the remainder of the current scope for a guard-clause
    /// if (no else branch, then-branch definitely exits).
    /// </summary>
    private void ApplyGuardClauseNarrowing(NarrowingInfo? narrowing,
        VariantIsNarrowing? variantNarrowing)
    {
        if (narrowing?.ElseBranchType != null)
        {
            _registry.NarrowVariable(name: narrowing.VariableName,
                narrowedType: narrowing.ElseBranchType);
        }

        if (narrowing is { ElseNonNull: true })
        {
            _registry.MarkVariableNonNull(name: narrowing.VariableName);
        }

        if (variantNarrowing != null)
        {
            ApplyVariantNarrowing(vn: variantNarrowing, conditionTrue: false);
        }
    }

    /// <summary>
    /// An <c>until</c> loop whose condition is a literal builds as written, with a notice. <c>until false</c> ends only
    /// at a <c>break</c>, <c>return</c> or <c>throw</c>, which <c>loop</c> says. <c>until true</c> never runs its body,
    /// which an editor shows faded. A named Bool, even a preset, is a condition like any other.
    /// </summary>
    internal void NoteLiteralLoopCondition(WhileStatement whileStmt)
    {
        if (whileStmt is not { IsUntil: true, Condition: UnaryExpression { Operator: UnaryOperator.Not } negated } ||
            negated.Operand is not LiteralExpression { Value: bool literal } written)
        {
            return;
        }

        if (!literal)
        {
            // The fix writes `loop` over `until false`, which keeps its carried values and `then` as they are.
            SourceLocation end = written.Location;
            SuggestedFix? fix = end.Line == whileStmt.Location.Line
                ? new SuggestedFix(Title: "Write 'loop'",
                    Edits:
                    [
                        new TextEdit(StartLine: whileStmt.Location.Line, StartColumn: whileStmt.Location.Column,
                            EndLine: end.Line, EndColumn: end.Column + 5, NewText: "loop")
                    ])
                : null;
            ReportNotice(code: SemanticWarningCode.EndlessUntil,
                message: "`until false` never ends by itself: it runs until a `break`, `return` or `throw`. Write `loop` " +
                         "for a loop like that.",
                location: whileStmt.Location,
                fix: fix);
            return;
        }

        Statement body = whileStmt.Body is BlockStatement { Statements.Count: > 0 } block
            ? block.Statements[index: 0]
            : whileStmt.Body;
        ReportNotice(code: SemanticWarningCode.UnreachableLoopBody,
            message: "This code never runs: `until true` stops before the first round.",
            location: body.Location,
            endLine: LastLine(statement: whileStmt.Body),
            neverRuns: true);
    }

    private void AnalyzeWhileStatement(WhileStatement whileStmt)
    {
        TypeSymbol conditionType = AnalyzeExpression(expression: whileStmt.Condition);

        // Condition must be boolean (ErrorType suppressed as cascade / buildtime-splice deferral — see
        // AnalyzeIfStatement for the rationale).
        if (!IsBoolType(type: conditionType) && conditionType is not ErrorTypeSymbol)
        {
            ReportError(code: SemanticDiagnosticCode.WhileConditionNotBool,
                message: $"A loop's condition must be a Bool, and this one is '{conditionType.Name}'.",
                location: whileStmt.Condition.Location);
        }

        // Analyze loop body. The loop ends at a `break`, and also when the condition is false, maybe before the first
        // turn: then what was unassigned before it still is. `until false` ends only at a `break`.
        bool endsOnlyAtBreak = Builder.Desugaring.Passes.ControlFlowLoweringPass.IsEndless(condition: whileStmt.Condition);
        var unassignedBefore = endsOnlyAtBreak
            ? []
            : _deadrefVariables.Where(predicate: n =>
                n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal)).ToList();
        _unassignedAtBreak.Push(item: []);
        _registry.EnterScope(kind: ScopeKind.Loop, name: "until");
        AnalyzeStatement(statement: whileStmt.Body);
        _registry.ExitScope();
        HashSet<string> atBreak = _unassignedAtBreak.Pop();
        if (endsOnlyAtBreak)
        {
            _deadrefVariables.RemoveWhere(match: n =>
                n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal));
        }

        _deadrefVariables.UnionWith(other: unassignedBefore);
        _deadrefVariables.UnionWith(other: atBreak);
    }

    /// <summary>
    /// Analyzes a buildtime <c>expand m in allmemvarof(T)</c> loop. Because the concrete members of
    /// <c>T</c> are unknown until monomorphization, this does NOT unroll or fully type the body —
    /// it only validates the template shape: the source type resolves, the handle <c>m</c> is
    /// registered as a buildtime-handle sentinel (so <c>m.name</c>/<c>m.id</c> and the splices type
    /// leniently), and the body is analyzed once. The real per-member expansion and typecheck
    /// happen in the generic AST rewriter at instantiation.
    /// </summary>
    private void AnalyzeExpandStatement(ExpandStatement expandStmt)
    {
        // BuilderExpansion gate: `expand` and its sources (allmemvarof/openmemvarof/caseof/branchof) are
        // buildtime intrinsics housed in the BuilderExpansion module — no longer keywords, siblings of
        // nameof/typeof. Using them requires the opt-in import (mirrors `import BuilderQuery`).
        if (!_importedModules.Contains(item: "BuilderExpansion"))
        {
            ReportError(code: SemanticDiagnosticCode.BuilderExpansionImportRequired,
                message: "'expand' requires 'import BuilderExpansion'.",
                location: expandStmt.Location);
        }

        // The parser is name-agnostic (it stored whatever identifier followed `in`); validate here that it
        // is a real reflection source.
        if (!ExpandSources.IsSource(name: expandStmt.SourceName))
        {
            ReportError(code: SemanticDiagnosticCode.UnknownExpandSource,
                message:
                $"'{expandStmt.SourceName}' is not a valid expand source — use 'allmemvarof', 'openmemvarof', 'caseof', 'branchof', or 'protocolof'.",
                location: expandStmt.Location);
        }

        // Expansion is single-level: a member walk cannot itself contain a member walk.
        if (_inExpandBody)
        {
            ReportError(code: SemanticDiagnosticCode.NestedExpandNotAllowed,
                message:
                "An 'expand' cannot be nested inside another 'expand' — buildtime member expansion is single-level.",
                location: expandStmt.Location);
        }

        // Resolve the source type for early error surfacing (a bare generic param resolves fine).
        TypeSymbol source = ResolveType(typeExpr: expandStmt.SourceType);

        // A concrete source (`allmemvarof(Pair)` in a routine that is not generic) has its parts now, so the body is
        // unrolled here and each copy is checked with its member's real type: `before.$nameof(m) != after.$nameof(m)`
        // is a `!=` on that member's type, with no `needs Equatable everywhere` to promise it. The enclosing block puts
        // the copies in place of the statement.
        if (IsConcreteExpandSource(source: source) && !_inExpandBody &&
            _importedModules.Contains(item: "BuilderExpansion") && ExpandSources.IsSource(name: expandStmt.SourceName))
        {
            // The source is handed to the unroller under a key no written name can be, so the substitution binds
            // the source alone and leaves every type the body names as written.
            const string sourceKey = "expand source";
            ExpandStatement keyed = expandStmt with
            {
                SourceType = expandStmt.SourceType with { Name = sourceKey, GenericArguments = null }
            };
            var unrolled = (BlockStatement)Instantiation.GenericAstRewriter.RewriteStatement(stmt: keyed,
                subs: new Dictionary<string, string>(comparer: StringComparer.Ordinal),
                typeSubs: new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal)
                {
                    [key: sourceKey] = source
                },
                registry: _registry,
                enclosingRoutine: _currentRoutine);
            expandStmt.Unrolled = unrolled;
            if (unrolled.Statements.Count > 0)
            {
                AnalyzeBlockStatement(block: unrolled);
            }

            return;
        }

        _registry.EnterScope(kind: ScopeKind.Loop, name: "expand");

        // Register the per-part handle so `m`, `m.name`, `m.id` resolve leniently in the body.
        _registry.DeclareVariable(name: expandStmt.HandleName,
            type: BuildtimeHandleTypeSymbol.Instance);

        bool prevInExpand = _inExpandBody;
        _inExpandBody = true;
        AnalyzeStatement(statement: expandStmt.Body);
        _inExpandBody = prevInExpand;

        _registry.ExitScope();
    }

    /// <summary>Whether an <c>expand</c> source is a type whose parts are known before monomorphization: no type
    /// parameter in it and not a generic definition.</summary>
    private static bool IsConcreteExpandSource(TypeSymbol source)
    {
        return source is not (ErrorTypeSymbol or GenericParameterTypeSymbol) && !source.IsGenericDefinition &&
               (source.TypeArguments ?? []).All(predicate: IsConcreteExpandSource);
    }

    private void AnalyzeForStatement(ForStatement forStmt)
    {
        _registry.EnterScope(kind: ScopeKind.Loop, name: "for");

        // Analyze iterable expression
        TypeSymbol iterableType = AnalyzeExpression(expression: forStmt.Iterable);

        // Get element type from iterable
        TypeSymbol elementType =
            GetIterableElementType(iterableType: iterableType, location: forStmt.Location);

        // Handle either simple variable or destructuring pattern
        if (forStmt.Variable != null)
        {
            // Simple variable binding: for item in items
            _registry.DeclareVariable(name: forStmt.Variable,
                type: elementType,
                location: forStmt.Location);
            _deadrefVariables.Remove(item: forStmt.Variable);
        }
        else if (forStmt.VariablePattern != null)
        {
            // Destructuring pattern: for (index, item) in items.enumerate()
            DeclareForDestructuringBindings(forStmt: forStmt, elementType: elementType);
        }

        // #22: Track active iteration source for reshaping-during-iteration check
        string? iterationSourceName = forStmt.Iterable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        if (iterationSourceName != null)
        {
            _activeIterationSources.Add(item: iterationSourceName);
        }

        // Analyze loop body
        AnalyzeStatement(statement: forStmt.Body);

        if (iterationSourceName != null)
        {
            _activeIterationSources.Remove(item: iterationSourceName);
        }

        _registry.ExitScope();
    }

    /// <summary>
    /// Declares the bindings of a for-loop destructuring pattern: matched positionally against a
    /// tuple element type, or with error types (plus a diagnostic) for a non-tuple element type.
    /// </summary>
    private void DeclareForDestructuringBindings(ForStatement forStmt, TypeSymbol elementType)
    {
        if (elementType is TupleTypeSymbol tupleType)
        {
            DeclareTupleDestructuringBindings(forStmt: forStmt, tupleType: tupleType);
        }
        else
        {
            DeclareErrorDestructuringBindings(forStmt: forStmt, elementType: elementType);
        }
    }

    /// <summary>
    /// Declares for-loop bindings for a tuple element type, matching positionally and reporting an
    /// arity mismatch when the binding count differs from the tuple's element count.
    /// </summary>
    private void DeclareTupleDestructuringBindings(ForStatement forStmt, TupleTypeSymbol tupleType)
    {
        int bindingCount = forStmt.VariablePattern!.Bindings.Count;
        if (bindingCount != tupleType.Arity)
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Destructuring pattern has {bindingCount} bindings but tuple has {tupleType.Arity} elements.",
                location: forStmt.VariablePattern.Location);
        }

        for (int i = 0; i < forStmt.VariablePattern.Bindings.Count; i++)
        {
            DestructuringBinding binding = forStmt.VariablePattern.Bindings[index: i];
            if (binding.BindingName == null)
            {
                continue;
            }

            TypeSymbol bindingType = i < tupleType.Arity
                ? tupleType.ElementTypes[index: i]
                : ErrorTypeSymbol.Instance;
            _registry.DeclareVariable(name: binding.BindingName, type: bindingType);
            _deadrefVariables.Remove(item: binding.BindingName);
        }
    }

    /// <summary>
    /// Reports a non-tuple destructuring mismatch and declares all bindings as the error type so
    /// downstream analysis can continue.
    /// </summary>
    private void DeclareErrorDestructuringBindings(ForStatement forStmt, TypeSymbol elementType)
    {
        ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
            message: $"Cannot destructure non-tuple type '{elementType.Name}' in for loop.",
            location: forStmt.VariablePattern!.Location);
        foreach (string? name in forStmt.VariablePattern
                                         .Bindings
                                         .Select(selector: b => b.BindingName)
                                         .Where(predicate: n => n != null))
        {
            _registry.DeclareVariable(name: name!, type: ErrorTypeSymbol.Instance);
        }
    }

    /// <summary>The variables matched by the <c>when</c> arms being analyzed whose arm named what they hold, with that
    /// name, innermost last.</summary>
    private readonly Stack<(string Subject, string Binding)> _matchedSubjects = new();

    /// <summary>RF-S264 for giving <paramref name="target"/> a new value inside an arm that named what it holds.</summary>
    private void ReportAssignWhileMatched(Expression target)
    {
        if (target is not IdentifierExpression id)
        {
            return;
        }

        foreach ((string subject, string binding) in _matchedSubjects)
        {
            // A value branch's name is a copy, so only a name that reaches the variant's own value (an entity, a
            // `Retained`, `Hijacked` or `Optional`) pins the variable.
            if (subject == id.Name && _registry.LookupVariable(name: binding)?.Type is { } held &&
                (held is EntityTypeSymbol || IsMaybeType(type: held) ||
                 held.BareName is Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Hijacked))
            {
                ReportError(code: SemanticDiagnosticCode.AssignWhileMatched,
                    message: $"You give '{subject}' a new value inside the arm where '{binding}' reads what '{subject}' " +
                             $"holds, so that value would end while '{binding}' still reads it. Give '{subject}' its new " +
                             "value after the 'when'.",
                    location: target.Location);
                return;
            }
        }
    }

    private void AnalyzeWhenStatement(WhenStatement whenStmt)
    {
        TypeSymbol matchedType = AnalyzeExpression(expression: whenStmt.Expression);
        // A token (`for m in marks` binds one) is matched by the value it reaches.
        if (Declaration.RuntimeContract.AccessTokenTypes.Contains(item: matchedType.BareName) &&
            Wrappers.GetWrapperInnerType(wrapperType: matchedType) is { } reached)
        {
            matchedType = reached;
        }

        // Buildtime arm-expansion: concrete arms are unknown until monomorphization.
        // Validate leniently and skip exhaustiveness/order checks.
        if (whenStmt.ArmExpansion is { } armExp)
        {
            AnalyzeWhenArmExpansion(whenStmt: whenStmt, armExp: armExp);
            return;
        }

        AddImplicitErrorArm(whenStmt: whenStmt, matchedType: matchedType);


        CheckWhenPatternOrder(whenStmt: whenStmt);
        CheckWhenDuplicatePatterns(whenStmt: whenStmt);
        CheckWhenSharedEntityRef(whenStmt: whenStmt, matchedType: matchedType);

        string? whenVarName = (whenStmt.Expression as IdentifierExpression)?.Name;
        VariantTypeSymbol? whenVariant =
            whenVarName != null && matchedType is VariantTypeSymbol wv &&
            !IsCarrierType(type: matchedType)
                ? wv
                : null;
        var handledArms = new List<string>();
        // The step of a `for` loop whose walk starts read-only hands its element out as `Viewing`.
        bool viewingElement = whenStmt.IsForStep &&
                              whenStmt.Expression is RecoveryExpression
                              {
                                  Inner: CallExpression { Callee: MemberExpression { Object: IdentifierExpression iterator } }
                              } &&
                              _readOnlyIterators.Contains(item: iterator.Name);
        bool handledNone = false;
        bool handledNoneValue = false;
        bool handledCrashable = false;
        bool handledValue = false;

        // Each arm runs from the state before the `when`: one arm's `steal` is not seen by the next.
        var deadrefBefore = new HashSet<string>(collection: _deadrefVariables);
        var armEnds = new List<(HashSet<string> Dead, bool Exits)>();

        foreach (WhenClause clause in whenStmt.Clauses)
        {
            if (armEnds.Count > 0 || clause != whenStmt.Clauses[index: 0])
            {
                _deadrefVariables.Clear();
                _deadrefVariables.UnionWith(other: deadrefBefore);
            }

            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");
            // In RazorForge the arm's name (`is Player as p`) reads the value inside the matched variable, so that
            // variable can't be given a new value in the arm.
            string? armBinding = clause.Pattern switch
            {
                TypePattern { VariableName: { } typed } => typed,
                ElsePattern { VariableName: { } rest } => rest,
                _ => null
            };
            bool pins = whenVarName != null && armBinding != null && !_registry.EntitiesAreShared &&
                        (whenVariant != null || IsCarrierType(type: matchedType));
            if (pins)
            {
                _matchedSubjects.Push(item: (whenVarName!, armBinding!));
            }

            bool handled;
            try
            {
            handled = AnalyzeWhenClause(clause: clause,
                matchedType: matchedType,
                priorClauses: whenStmt.Clauses.TakeWhile(predicate: c => !ReferenceEquals(objA: c, objB: clause)),
                ctx: new WhenClauseContext(WhenVarName: whenVarName,
                    WhenVariant: whenVariant,
                    HandledArms: handledArms,
                    ViewingElement: viewingElement,
                    ForStep: whenStmt.IsForStep),
                handledNone: ref handledNone,
                handledNoneValue: ref handledNoneValue,
                handledCrashable: ref handledCrashable,
                handledValue: ref handledValue);
            }
            finally
            {
                if (pins)
                {
                    _matchedSubjects.Pop();
                }
            }

            armEnds.Add(item: (new HashSet<string>(collection: _deadrefVariables),
                ControlFlowExits.HasDefiniteExit(statement: clause.Body)));
            if (handled)
            {
                continue;
            }

            _registry.ExitScope();
        }

        CheckTakenOnEveryPath(paths: armEnds, before: deadrefBefore, construct: "when", at: whenStmt.Location);
        // What holds after the `when` is what holds at the end of an arm that goes on past it. Without an `else` arm
        // the `when` may also match nothing, which goes on from the state before it. With one, a name every arm that
        // goes on gives a value to has one after the `when`, even when an arm that throws gives it none.
        bool coversAll = whenStmt.Clauses.Any(predicate: c => c.Pattern is ElsePattern);
        _deadrefVariables.Clear();
        if (!coversAll)
        {
            _deadrefVariables.UnionWith(other: deadrefBefore);
        }

        foreach ((HashSet<string> dead, bool exits) in armEnds.Where(predicate: a => !a.Exits))
        {
            _deadrefVariables.UnionWith(other: dead);
        }

        if (matchedType is ChoiceTypeSymbol or VariantTypeSymbol || IsCarrierType(type: matchedType) ||
            IsBoolType(type: matchedType))
        {
            CheckWhenExhaustiveness(whenStmt: whenStmt, matchedType: matchedType);
        }
    }

    /// <summary>
    /// A <c>when</c> on a <c>Check</c>/<c>Lookup</c> that does not handle the caught error passes it on: it gets the
    /// arm <c>is Crashables e => throw e</c> (propagating, in a failable routine) or <c>is Crashables e => throw e</c>
    /// that crashes where it stands (anywhere else). An arm for one crashable (<c>is NopeError e</c>) handles only that one.
    /// </summary>
    private void AddImplicitErrorArm(WhenStatement whenStmt, TypeSymbol matchedType)
    {
        if (!IsCarrierType(type: matchedType) || IsMaybeType(type: matchedType) ||
            whenStmt.Clauses.Any(predicate: c => c.Pattern is CrashablePattern or WildcardPattern or ElsePattern
                or IdentifierPattern or TypePattern { Type.Name: Builder.Declaration.RuntimeContract.Crashables }))
        {
            return;
        }

        const string errorName = "__rf_unhandled_error";
        SourceLocation loc = whenStmt.Location;
        whenStmt.Clauses.Add(item: new WhenClause(
            Pattern: new TypePattern(
                Type: new TypeExpression(Name: Builder.Declaration.RuntimeContract.Crashables,
                    GenericArguments: null,
                    Location: loc),
                VariableName: errorName,
                Bindings: null,
                Location: loc),
            Body: new ThrowStatement(Error: new IdentifierExpression(Name: errorName, Location: loc),
                Location: loc,
                IsFatal: _currentRoutine is not { IsFailable: true }),
            Location: loc));
    }

    /// <summary>
    /// In Suflae, rejects a <c>when</c> whose subject is an entity reference — entity refs have
    /// only two flow states (none / present) and should use <c>if x is None</c> instead.
    /// </summary>
    private void CheckWhenSharedEntityRef(WhenStatement whenStmt, TypeSymbol matchedType)
    {
        if (_registry.EntitiesAreShared && SharedEntities.IsEntityRef(type: matchedType))
        {
            ReportError(code: SemanticDiagnosticCode.NullableEntityDeref,
                message:
                "Cannot use 'when' on an entity reference. Check its none-state with " +
                "'if x is None' / 'if x isnot None' instead.",
                location: whenStmt.Expression.Location);
        }
    }

    /// <summary>
    /// Analyzes a single <c>when</c> clause: applies variant narrowing for else clauses, tracks
    /// handled carrier patterns, dispatches carrier else-binding, then analyzes the pattern and
    /// body. Returns <c>true</c> when the clause already called <c>ExitScope</c> (caller must
    /// <c>continue</c>), or <c>false</c> when the caller should call <c>ExitScope</c>.
    /// </summary>
    private readonly record struct WhenClauseContext(
        string? WhenVarName,
        VariantTypeSymbol? WhenVariant,
        List<string> HandledArms,
        bool ViewingElement = false,
        bool ForStep = false);

    private bool AnalyzeWhenClause(WhenClause clause, TypeSymbol matchedType,
        IEnumerable<WhenClause> priorClauses, WhenClauseContext ctx, ref bool handledNone, ref bool handledNoneValue,
        ref bool handledCrashable, ref bool handledValue)
    {
        ApplyElseVariantNarrowing(clause: clause,
            whenVarName: ctx.WhenVarName,
            whenVariant: ctx.WhenVariant,
            handledArms: ctx.HandledArms);
        UpdateCarrierHandledFlags(clause: clause,
            matchedType: matchedType,
            handledNone: ref handledNone,
            handledNoneValue: ref handledNoneValue,
            handledCrashable: ref handledCrashable);

        if (clause.Pattern is ElsePattern { VariableName: { } elseName } elsePat)
        {
            // The name has the one type left after the earlier arms. On a `Check`, when that is the caught error,
            // the arm is `is Crashables e`.
            TypeSymbol? rest = ResolveElseRemainder(elsePat: elsePat,
                matchedType: matchedType,
                priorClauses: priorClauses);
            elsePat.RemainingType = rest;
            elsePat.BindsCarrierError = rest is not null && GetCarrierBaseName(type: matchedType) == "Check" &&
                                        rest.BareName == Builder.Declaration.RuntimeContract.Crashables;
            DeclarePatternVariable(name: elseName,
                type: rest == null
                    ? ErrorTypeSymbol.Instance
                    : ctx.ViewingElement && IsCarrierType(type: matchedType)
                        ? ReadOnlyElement(element: rest)
                        : rest,
                location: elsePat.Location,
                isLoopVariable: ctx.ForStep);
            AnalyzeStatement(statement: clause.Body);
            _registry.ExitScope();
            return true;
        }

        AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);
        if (IsCarrierValueArm(pattern: clause.Pattern, carrierType: matchedType))
        {
            handledValue = true;
        }

        ApplyVariantArmNarrowing(clause: clause,
            whenVarName: ctx.WhenVarName,
            whenVariant: ctx.WhenVariant,
            handledArms: ctx.HandledArms);
        AnalyzeStatement(statement: clause.Body);
        return false;
    }

    /// <summary>
    /// For a variant subject's else clause, excludes all previously matched arms from the registry
    /// and narrows to the sole remaining arm when exactly one is left.
    /// </summary>
    private void ApplyElseVariantNarrowing(WhenClause clause, string? whenVarName,
        VariantTypeSymbol? whenVariant, List<string> handledArms)
    {
        if (whenVariant == null || whenVarName == null || clause.Pattern is not ElsePattern)
        {
            return;
        }

        foreach (string armName in handledArms)
        {
            _registry.ExcludeVariantArm(name: whenVarName, armFullName: armName);
        }

        IReadOnlyCollection<string> excluded = _registry.GetExcludedVariantArms(name: whenVarName);
        if (whenVariant.Members
                       .Where(predicate: m => !excluded.Contains(value: m.Name))
                       .ToList() is [{ Type: not null } sole])
        {
            _registry.NarrowVariable(name: whenVarName, narrowedType: sole.Type);
        }
    }

    /// <summary>
    /// Updates the <c>handledNone</c>, <c>handledNoneValue</c>, and <c>handledCrashable</c> flags
    /// based on the current clause's pattern and the carrier base name of the matched type.
    /// </summary>
    private static void UpdateCarrierHandledFlags(WhenClause clause, TypeSymbol matchedType,
        ref bool handledNone, ref bool handledNoneValue, ref bool handledCrashable)
    {
        string? carrierBase = GetCarrierBaseName(type: matchedType);
        bool carrierUsesNoneForAbsent = carrierBase is "Optional";
        if (carrierUsesNoneForAbsent && IsNonePattern(pattern: clause.Pattern))
        {
            handledNone = true;
        }
        else if (carrierBase == "Check" && IsNoneTypePattern(pattern: clause.Pattern))
        {
            handledNoneValue = true;
        }
        else if (IsCrashablePattern(pattern: clause.Pattern))
        {
            handledCrashable = true;
        }
    }

    /// <summary>
    /// True when <paramref name="pattern"/> is an unguarded <c>is T</c> / <c>is T v</c> arm that fully
    /// covers a carrier's value case (<c>T</c> is the carrier's value type).
    /// </summary>
    private bool IsCarrierValueArm(Pattern pattern, TypeSymbol carrierType)
    {
        if (GetCarrierBaseName(type: carrierType) is not "Check" ||
            carrierType.TypeArguments is not { Count: > 0 } ||
            pattern is not TypePattern tp ||
            (tp.Type.ResolvedType ?? _registry.LookupType(name: tp.Type.Name)) is not { } armType)
        {
            return false;
        }

        TypeSymbol valueType = carrierType.TypeArguments[index: 0];
        return ReferenceEquals(objA: armType, objB: valueType) || armType.Name == valueType.Name;
    }

    /// <summary>
    /// Records which variant arm the clause matched (for the trailing else exclusion) and narrows
    /// the subject variable to the matched arm's type inside this arm's body.
    /// </summary>
    private void ApplyVariantArmNarrowing(WhenClause clause, string? whenVarName,
        VariantTypeSymbol? whenVariant, List<string> handledArms)
    {
        if (whenVariant == null || whenVarName == null)
        {
            return;
        }

        if (ResolveVariantArm(pattern: clause.Pattern, variant: whenVariant) is { } matchedArm)
        {
            handledArms.Add(item: matchedArm.Name);
        }

        // The arm's body sees the subject as the arm's type even when the arm tests more (a guard, a member
        // variable's value): the body only runs once those passed.
        Pattern armPattern = clause.Pattern switch
        {
            GuardPattern gp => gp.InnerPattern,
            TypePattern { Bindings: not null } tp => tp with { Bindings = null },
            _ => clause.Pattern
        };
        if (ResolveVariantArm(pattern: armPattern, variant: whenVariant) is
            { Type: not null } bodyArm)
        {
            _registry.NarrowVariable(name: whenVarName, narrowedType: bodyArm.Type);
        }
    }

    /// <summary>
    /// Analyzes a buildtime arm-expansion `when` (`when me` / `is ${m.type} x => …`): its explicit
    /// clauses and the expansion template body are validated leniently (bindings deferred to
    /// monomorphization) with the exhaustiveness/order checks skipped.
    /// </summary>
    private void AnalyzeWhenArmExpansion(WhenStatement whenStmt, WhenArmExpansion armExp)
    {
        // The subject type is the generic param (arms unknown pre-monomorph), so the explicit
        // clauses' patterns (e.g. `is None`) can't be validated here — defer them. Declare any
        // simple type-pattern binding leniently so its body still type-checks.
        foreach (WhenClause clause in whenStmt.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when-clause");
            if (clause.Pattern is TypePattern { VariableName: { } explicitBind })
            {
                _registry.DeclareVariable(name: explicitBind, type: ErrorTypeSymbol.Instance);
            }

            AnalyzeStatement(statement: clause.Body);
            _registry.ExitScope();
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: "expand-arm");
        _registry.DeclareVariable(name: armExp.HandleName, type: BuildtimeHandleTypeSymbol.Instance);
        if (armExp.Template.Pattern is SpliceTypePattern { VariableName: { } bindName })
        {
            _registry.DeclareVariable(name: bindName, type: ErrorTypeSymbol.Instance);
        }

        AnalyzeStatement(statement: armExp.Template.Body);
        _registry.ExitScope();
    }

    /// <summary>
    /// #88: Enforces that an else/wildcard clause is last — any clause after one is unreachable.
    /// </summary>
    private void CheckWhenPatternOrder(WhenStatement whenStmt)
    {
        bool seenElse = false;
        foreach (Pattern pattern in whenStmt.Clauses.Select(selector: c => c.Pattern))
        {
            if (seenElse)
            {
                ReportError(code: SemanticDiagnosticCode.PatternOrderViolation,
                    message: "Unreachable pattern after 'else' or wildcard.",
                    location: pattern.Location);
            }

            if (pattern is ElsePattern or WildcardPattern)
            {
                seenElse = true;
            }
        }
    }

    /// <summary>
    /// #130/#148: Reports clauses whose pattern key repeats an earlier clause's.
    /// </summary>
    private void CheckWhenDuplicatePatterns(WhenStatement whenStmt)
    {
        var seenPatterns = new HashSet<string>();
        foreach (Pattern pattern in whenStmt.Clauses.Select(selector: c => c.Pattern))
        {
            foreach (string patternKey in GetPatternKeys(pattern: pattern))
            {
                if (!seenPatterns.Add(item: patternKey))
                {
                    ReportError(code: SemanticDiagnosticCode.DuplicatePattern,
                        message: $"Duplicate pattern: {patternKey}.",
                        location: pattern.Location);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Checks exhaustiveness of a `when` over an enumerable type: a catch-all clause is always
    /// exhaustive, otherwise a missing 'Crashable' arm on Result/Lookup is a hard error and any
    /// other gap is a non-exhaustive warning.
    /// </summary>
    private void CheckWhenExhaustiveness(WhenStatement whenStmt, TypeSymbol matchedType)
    {
        bool hasCatchAll = whenStmt.Clauses.Any(predicate: c =>
            c.Pattern is WildcardPattern or ElsePattern or IdentifierPattern);

        if (hasCatchAll)
        {
            _exhaustiveWhens.Add(item: whenStmt);
            return;
        }

        ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
            clauses: whenStmt.Clauses,
            matchedType: matchedType);

        if (exhaustiveness.IsExhaustive)
        {
            _exhaustiveWhens.Add(item: whenStmt);
            return;
        }

        string missing = exhaustiveness.MissingCases.Count > 0
            ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
            : "";

        // A `when` on a Check/Lookup must take the success value (the error arm is added when it is left out).
        if (IsCarrierType(type: matchedType) && !IsMaybeType(type: matchedType) &&
            exhaustiveness.MissingCases.Any(predicate: m => m != "None"))
        {
            string valueName = matchedType.TypeArguments?.FirstOrDefault()?.Name ?? "T";
            bool someValueTaken = whenStmt.Clauses.Any(predicate: c => IsHeldTypeArm(pattern: c.Pattern,
                carrierType: matchedType));
            ReportError(code: SemanticDiagnosticCode.NonExhaustiveMatch,
                message: someValueTaken
                    ? $"This `when` on '{matchedType.Name}' takes some kinds of '{valueName}' but not all of them. " +
                      $"Add an arm for each one left, or one for the whole value (`is {valueName} as v`).{missing}"
                    : $"This `when` on '{matchedType.Name}' never takes the success value. Add an arm for it " +
                      $"(`is {valueName} as v`).{missing}",
                location: whenStmt.Location);
        }
        else
        {
            ReportWarning(code: SemanticWarningCode.NonExhaustiveWhen,
                message:
                $"When statement may not cover all cases of '{matchedType.Name}'.{missing}",
                location: whenStmt.Location);
        }
    }

    private void AnalyzeReturnStatement(ReturnStatement ret)
    {
        if (_currentRoutine == null)
        {
            ReportError(code: SemanticDiagnosticCode.ReturnOutsideFunction,
                message: "Return statement outside of function.",
                location: ret.Location);
            return;
        }

        // A routine declared without `->` has no result type until its body is analyzed, and gives nothing back.
        TypeSymbol? declaredResult = _currentRoutine.ReturnType;
        bool givesNothing = declaredResult is null or { IsNone: true };

        // Bare `return` is normalized by NoneReturnNormalizationPass to `return None` with Location == ret.Location.
        bool isBareReturn = ret.Value is null ||
                            ret.Value is IdentifierExpression { Name: "None" } blankId &&
                            blankId.Location == ret.Location;

        // RF-S301: a bare `return` in a routine that gives back a value leaves the caller without one.
        if (isBareReturn && !givesNothing)
        {
            string written = WrittenTypeName(type: declaredResult!);
            ReportError(code: SemanticDiagnosticCode.ReturnTypeMismatch,
                message: $"Routine '{_currentRoutine.Name}' gives back {Article.For(name: written)} '{written}', and this " +
                         $"'return' hands back nothing. Return {Article.For(name: written)} '{written}' here, " +
                         $"or take '-> {written}' off the routine if it gives nothing back.",
                location: ret.Location);
        }

        if (ret.Value != null)
        {
            // A local handed over inside the returned value ends at this return: it moves without `steal`.
            HandOverReturnedLocals(ret: ret);

            // Pass expected return type for contextual literal inference
            TypeSymbol returnType = AnalyzeExpression(expression: ret.Value,
                expectedType: _currentRoutine.ReturnType);

            // A token may be returned only when it is taken from `me` (RazorForge only)
            ValidateNotTokenReturnType(type: returnType, location: ret.Location, returned: ret.Value);

            // RF-S413: returning a field or a container element hands the caller an entity its owner
            // still keeps. A returned local is a move, so a bare variable is not flagged here.
            if (ChecksHolderOf(type: returnType) &&
                ReadsKeptEntity(value: ret.Value, includeVariables: false) &&
                MayHoldEntity(type: returnType))
            {
                ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                    message: KeptEntityMessage(action: "You are returning", value: ret.Value,
                        type: returnType),
                    location: ret.Value.Location);
            }

            // RF-S301: a value returned from a routine that gives nothing back would be dropped without a word.
            // The declaration states the result type, so the value never turns the routine into one that has one.
            if (!isBareReturn && givesNothing && !returnType.IsNone && returnType is not ErrorTypeSymbol)
            {
                string written = WrittenTypeName(type: returnType);
                ReportError(code: SemanticDiagnosticCode.ReturnTypeMismatch,
                    message: (declaredResult == null
                                 ? $"Routine '{_currentRoutine.Name}' gives nothing back (it has no '-> T'), "
                                 : $"Routine '{_currentRoutine.Name}' gives nothing back (it is declared '-> None'), ") +
                             $"and this 'return' hands back {Article.For(name: written)} '{written}'. " +
                             $"Write '-> {written}' on the routine, or return without a value.",
                    location: ret.Location);
            }
            else if (!isBareReturn && !givesNothing &&
                !IsAssignableTo(source: returnType, target: _currentRoutine.ReturnType!))
            {
                // A token where its entity is expected (`var b = boxes[0]` then `return b`): say what the value
                // is and how to hand back an entity of its own.
                bool tokenForEntity =
                    returnType.BareName is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying &&
                    returnType.TypeArguments is [{ } tokenTarget] &&
                    tokenTarget.FullName == _currentRoutine.ReturnType.FullName;
                ReportError(code: SemanticDiagnosticCode.ReturnTypeMismatch,
                    message: tokenForEntity
                        ? $"You are returning {Article.For(name: returnType.Name)} '{returnType.Name}' token where this routine returns the " +
                          $"'{_currentRoutine.ReturnType.Name}' itself. The token only points at an entity something " +
                          "else owns (an element read into a variable, `var b = boxes[0]`, holds one). Return a copy " +
                          "with '.duplicate()', take the element out of its container (`remove_at`), or return the token " +
                          $"by declaring the routine '-> {returnType.Name}'."
                        : $"Cannot return value of type '{WrittenTypeName(type: returnType)}' from function expecting " +
                          $"'{WrittenTypeName(type: _currentRoutine.ReturnType)}'.",
                    location: ret.Location);
            }
        }
    }

    /// <summary>
    /// A local of the routine handed over inside a <c>return</c> expression (an argument, a tuple or list item:
    /// <c>return GameBoard(rows: rows)</c>) ends at that return, so the return is its last use and handing it over
    /// leaves no second holder. An entity local there moves as if <c>steal</c> were written, and a written
    /// <c>steal</c> of such a local is a warning whose quick fix removes it.
    /// </summary>
    private void HandOverReturnedLocals(ReturnStatement ret)
    {
        if (!ChecksOwnershipHere || _isInCompilerGeneratedBody || ret.Value is null)
        {
            return;
        }

        // `return steal xs`: the returned local moves by itself.
        if (ret.Value is StealExpression { IsImplicitMove: false, Operand: IdentifierExpression returned } direct &&
            IsReturnEndedLocal(name: returned.Name))
        {
            WarnStealInReturn(steal: direct, local: returned);
        }

        HandOverIn(value: ret.Value);
    }

    /// <summary>Walks the items of <paramref name="value"/> that hand a value over: call and construction arguments,
    /// tuple and list items, and those of nested calls.</summary>
    private void HandOverIn(Expression value)
    {
        switch (value)
        {
            case CallExpression call:
                HandOverItems(items: call.Arguments);
                break;
            case GenericMemberRoutineCallExpression generic:
                HandOverItems(items: generic.Arguments);
                break;
            case TupleLiteralExpression tuple:
                HandOverItems(items: tuple.Elements);
                break;
            case ListLiteralExpression list:
                HandOverItems(items: list.Elements);
                break;
            case NamedArgumentExpression named:
                HandOverIn(value: named.Value);
                break;
        }
    }

    private void HandOverItems(List<Expression> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            Expression item = items[index: i];
            Expression inner = item is NamedArgumentExpression named ? named.Value : item;
            Expression? replaced = inner switch
            {
                IdentifierExpression id when IsReturnEndedLocal(name: id.Name) &&
                                             _registry.LookupVariable(name: id.Name) is { } local &&
                                             IsRawEntityType(type: local.Type) =>
                    new StealExpression(Operand: id, Location: id.Location) { IsImplicitMove = true },
                StealExpression { IsImplicitMove: false, Operand: IdentifierExpression stolen } written
                    when IsReturnEndedLocal(name: stolen.Name) => WarnStealInReturn(steal: written, local: stolen),
                _ => null
            };

            if (replaced is null)
            {
                HandOverIn(value: inner);
                continue;
            }

            items[index: i] = item is NamedArgumentExpression namedItem
                ? namedItem with { Value = replaced }
                : replaced;
        }
    }

    /// <summary>Reports RF-W259 on a written <c>steal</c> of a local that ends at the return, with the quick fix that
    /// removes the word, and returns null so the steal stays as written.</summary>
    private Expression? WarnStealInReturn(StealExpression steal, IdentifierExpression local)
    {
        ReportWarning(code: SemanticWarningCode.StealInReturn,
            message: $"'{local.Name}' ends at this return, so it is handed over without 'steal'. Write " +
                     $"'{local.Name}' alone (migrate --step return-steal removes it).",
            location: steal.Location,
            fix: new SuggestedFix(Title: "Remove 'steal'",
                Edits:
                [
                    new TextEdit(StartLine: steal.Location.Line, StartColumn: steal.Location.Column,
                        EndLine: local.Location.Line, EndColumn: local.Location.Column, NewText: "")
                ]));
        return null;
    }

    /// <summary>Whether <paramref name="name"/> is a variable the routine declared itself (not a parameter, a global,
    /// a preset or <c>me</c>), which every <c>return</c> ends.</summary>
    private bool IsReturnEndedLocal(string name)
    {
        return name != "me" && _registry.LookupVariable(name: name) is
        {
            IsParameter: false, IsGlobal: false, IsPreset: false
        };
    }

    private void AnalyzeThrowStatement(ThrowStatement throwStmt)
    {
        if (_currentRoutine == null)
        {
            ReportWarning(code: SemanticWarningCode.ThrowAbsentInNonFailable,
                message: "Throw statement outside any routine.",
                location: throwStmt.Location);
            return;
        }

        // A `throw` in a failable routine is its failure. In a routine not declared failable the parser made it
        // fatal (IsFatal): it crashes where it stands and never marks the routine failable.
        TypeSymbol errorType = AnalyzeExpression(expression: throwStmt.Error);

        // Only `crashable`-kind types are throwable errors. The `crashable` keyword implicitly
        // confers the Crashable contract; no other type kind may obey it (enforced at the
        // declaration site — see ValidateTypeProtocolImplementation), so there is no longer an
        // explicit-`obeys Crashable` path for records/entities. `Error`/ErrorTypeSymbol are the
        // catch-all error references used by generic error handling.
        bool isCrashable = errorType.Category == TypeCategory.Crashable ||
                           errorType.Name == Builder.Declaration.RuntimeContract.Crashables ||
                           errorType is ErrorTypeSymbol || errorType.Name == "Error";
        if (!isCrashable)
        {
            ReportError(code: SemanticDiagnosticCode.ThrowNotCrashable,
                message:
                $"Only `crashable`-kind types can be thrown, got '{errorType.Name}' ({errorType.Category}). " +
                "Declare the error type with the `crashable` keyword.",
                location: throwStmt.Error.Location);
        }

        // One warning per routine with a `todo`, at its first one, so a routine written in full has none left.
        if (throwStmt.IsTodo && _routinesWarnedForTodo.Add(item: _currentRoutine))
        {
            ReportWarning(code: SemanticWarningCode.TodoLeft,
                message: $"'{_currentRoutine.Name}' has a 'todo': it crashes with NotImplementedError when it runs. " +
                         "Write the code in its place.",
                location: throwStmt.Location);
        }

        // Mark routine as having throw statements (drives the declaration check and variant
        // generation). A fatal throw never marks failable: it is a crash, not a failure the caller receives.
        if (!throwStmt.IsFatal)
        {
            _currentRoutine.HasThrow = true;
        }
    }

    private void AnalyzeBreakStatement(BreakStatement breakStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(code: SemanticDiagnosticCode.BreakOutsideLoop,
                message: "Break statement is only allowed inside a loop.",
                location: breakStmt.Location);
        }
    }

    private void AnalyzeContinueStatement(ContinueStatement continueStmt)
    {
        if (!_registry.CurrentScope.IsInLoop)
        {
            ReportError(code: SemanticDiagnosticCode.ContinueOutsideLoop,
                message: "Continue statement is only allowed inside a loop.",
                location: continueStmt.Location);
        }
    }

    private void AnalyzeDestructuringStatement(DestructuringStatement destruct)
    {
        TypeSymbol? declared = destruct.DeclaredType is { } written
            ? ResolveType(typeExpr: written)
            : null;
        TypeSymbol initType = AnalyzeExpression(expression: destruct.Initializer, expectedType: declared);
        if (declared != null && initType is not ErrorTypeSymbol && !IsAssignableTo(source: initType, target: declared))
        {
            ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                message: $"The value is {Article.For(name: initType.Name)} '{initType.Name}', but the names are declared as '{declared.Name}'.",
                location: destruct.Initializer.Location);
        }

        // Analyze the destructuring pattern and bind variables
        AnalyzeDestructuringPattern(pattern: destruct.Pattern, sourceType: initType);
    }

    /// <summary>
    /// Analyzes a drop statement (explicitly ignores a return value).
    /// Used to explicitly indicate that a routine's return value is intentionally ignored.
    /// </summary>
    private void AnalyzeDropStatement(DropStatement dropped)
    {
        // `drop x` on a binding ends it here.
        if (dropped.Expression is IdentifierExpression ended)
        {
            AnalyzeDropBinding(dropped: dropped, ended: ended);
            return;
        }

        // drop must target a routine call, not an arbitrary expression like a literal or variable.
        // Explicit-generic calls (`f[T](...)`) parse to GenericMemberRoutineCallExpression — also a call.
        // A `try`/`check` recovery (`drop check feeder.send(...)`) is likewise a call under the
        // hood — it lowers to the failable call's recovery variant — so discarding its carrier is valid.
        if (dropped.Expression is not (CallExpression or GenericMemberRoutineCallExpression
            or RecoveryExpression or WorkExpression))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDropTarget,
                message: "'drop' can only be used with routine calls. " +
                         "Use 'drop some_routine()' to drop a result on purpose.",
                location: dropped.Location);
        }

        // Analyze the expression - this validates the expression and checks for errors
        // The result is intentionally discarded
        TypeSymbol discardedType = AnalyzeExpression(expression: dropped.Expression);

        // `drop job f()` throws the Work away before anything starts it: its work never runs. Warn even though
        // the value was dropped on purpose.
        if (IsWorkType(type: discardedType))
        {
            ReportWarning(code: SemanticWarningCode.AsyncWorkNeverLaunched,
                message: dropped.Expression is WorkExpression
                    ? WorkNeverStartedMessage()
                    : "`drop` throws this Work away before anything starts it, so its work never runs. Call " +
                      "`.start_now()` on it to start it, and `.retrieve()` later to wait for its value.",
                location: dropped.Location);
        }
    }

    /// <summary>
    /// <c>drop x</c>: tears a local binding the routine owns down here and ends the name, as <c>steal x</c> does
    /// (a later use is RF-S615, and the scope's end does not tear it down again). A global, <c>me</c>
    /// and an access token (<c>Viewing</c> / <c>Modifying</c>, which someone else owns) cannot be dropped.
    /// </summary>
    private void AnalyzeDropBinding(DropStatement dropped, IdentifierExpression ended)
    {
        TypeSymbol type = AnalyzeExpression(expression: ended);
        if (type is ErrorTypeSymbol)
        {
            return;
        }

        VariableInfo? variable = _registry.LookupVariable(name: ended.Name);
        string? refusal = variable == null
            ? $"'{ended.Name}' is not a variable."
            : ended.Name == "me"
                ? "'me' belongs to whoever called this routine, so it cannot be dropped here."
                : variable.IsGlobal
                    ? $"'{ended.Name}' is a global, which lives as long as the program. `drop` ends a local binding."
                    : ImplicitToken.IsTokenParameter(parameter: type, tokenKind: out string kind, target: out _)
                        ? $"'{ended.Name}' is a {kind} token, access to a value someone else owns: dropping it would " +
                          "end that value. The access ends with its scope."
                        : null;
        if (refusal != null)
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDropTarget,
                message: refusal,
                location: dropped.Location);
            return;
        }

        dropped.EndsBinding = true;
        _deadrefVariables.Add(item: ended.Name);
        _everStolenVariables.Add(item: ended.Name);
        _droppedVariables.Add(item: ended.Name);
    }

    private void AnalyzeDangerStatement(DangerStatement danger)
    {
        if (UnsafeSurfaceIsBarred() && !danger.IsBuilderWritten)
        {
            ReportError(code: SemanticDiagnosticCode.FeatureNotInSuflae,
                message:
                $"A {_registry.Rules.Name} program has no `danger` block: memory is never handled directly here. " +
                "Code that must handle memory or call C goes in a `.rf` module this program imports.",
                location: danger.Location);
            return;
        }

        // Danger blocks cannot be nested
        if (InDangerBlock)
        {
            ReportError(code: SemanticDiagnosticCode.NestedDangerBlock,
                message: "Danger blocks cannot be nested.",
                location: danger.Location);
            return;
        }

        // Enter danger scope
        _registry.EnterScope(kind: ScopeKind.Block, name: "danger");
        _dangerBlockDepth = 1;

        try
        {
            AnalyzeBlockStatement(block: danger.Body);
        }
        finally
        {
            _dangerBlockDepth = 0;
            _registry.ExitScope();
        }
    }

    private void AnalyzeUsingStatement(UsingStatement usingStmt)
    {
        // Analyze the resource expression to get its type
        TypeSymbol resourceType = AnalyzeExpression(expression: usingStmt.Resource);

        // The bound variable type defaults to the resource type, but may be overridden
        // by enter's return type when it returns non-void.
        TypeSymbol boundType = resourceType;

        // A `using` target must obey `Enterable` — the protocol that declares the `enter`/`exit`
        // scope-management contract. Conformance (not just the presence of `enter`/`exit` by name)
        // is the gate, so being `using`-able is an explicit, checked capability.
        if (_registry.Rules.RequiresEnterableForUsing)
        {
            ValidateEnterableResource(usingStmt: usingStmt,
                resourceType: resourceType,
                boundType: ref boundType);
        }

        // Create a new scope for the using block
        _registry.EnterScope(kind: ScopeKind.Block, name: "using");

        // Declare the binding variable in the using scope
        _registry.DeclareVariable(name: usingStmt.Name,
            type: boundType,
            location: usingStmt.Location);
        if (usingStmt.Parts != null)
        {
            DeclareUsingParts(usingStmt: usingStmt, boundType: boundType);
        }

        // Analyze the body. (A token's use after its source changed is RazorForge's TokenLifetimeChecker's, RF-S643.)
        AnalyzeStatement(statement: usingStmt.Body);
        _registry.ExitScope();
    }

    /// <summary>
    /// Validates a RazorForge `using` resource obeys `Enterable`; when it does, overrides the bound
    /// variable type with `enter`'s non-void return.
    /// </summary>
    private void ValidateEnterableResource(UsingStatement usingStmt, TypeSymbol resourceType,
        ref TypeSymbol boundType)
    {
        // A resource whose own analysis failed has been reported already: no second error about its type.
        if (resourceType.Category == TypeCategory.Error)
        {
            return;
        }

        if (!ImplementsProtocol(type: resourceType, protocolName: "Enterable"))
        {
            string access = resourceType.BareName is Declaration.RuntimeContract.Viewing
                or Declaration.RuntimeContract.Modifying or Declaration.RuntimeContract.Guarded
                ? " Access to an entity is not a resource: open it with 'viewing x as v' or 'modifying x as m', " +
                  "which 'migrate --step access' writes for 'using x.view() as v'."
                : "";
            ReportError(code: SemanticDiagnosticCode.UsingTargetMissingEnterExit,
                message:
                $"You give 'using' a {resourceType.Name}, which can't be opened and closed: a 'using' resource obeys " +
                $"'Enterable', which gives it 'enter' and 'exit'.{access}",
                location: usingStmt.Location);
            return;
        }

        // The bound variable's type is `enter`'s return type when non-void (pass-through).
        RoutineInfo? enterMemberRoutine =
            _registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "enter");
        if (enterMemberRoutine?.ReturnType is { IsNone: false } enterReturn)
        {
            boundType = enterReturn;
        }
    }

    /// <summary>Declares the names of <c>using r as (a, b)</c>, one per item of the tuple the scope binds.</summary>
    private void DeclareUsingParts(UsingStatement usingStmt, TypeSymbol boundType)
    {
        List<string> parts = usingStmt.Parts!;
        if (boundType is ErrorTypeSymbol)
        {
            return;
        }

        if (boundType is not TupleTypeSymbol tuple || tuple.ElementTypes.Count != parts.Count)
        {
            string gives = boundType is TupleTypeSymbol other
                ? $"a tuple of {other.ElementTypes.Count}"
                : $"one '{boundType.Name}'";
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message: $"'as {usingStmt.BoundText}' names {parts.Count} items, but this 'using' gives {gives}. " +
                         "Name each item of the tuple it gives, or bind it with one name.",
                location: usingStmt.Location);
            return;
        }

        for (int i = 0; i < parts.Count; i++)
        {
            _registry.DeclareVariable(name: parts[index: i],
                type: tuple.ElementTypes[index: i],
                location: usingStmt.Location);
        }
    }

    /// <summary>The most targets one <c>viewing</c> / <c>modifying</c> block opens: the AccessManager keeps a block's
    /// targets in a list of this many until it opens them.</summary>
    internal const int MaxAccessTargets = 16;

    /// <summary>
    /// Analyzes a <c>viewing</c> / <c>modifying</c> block. Each target is a type the block enters (a <c>Guarded</c>, a
    /// <c>FileStorage</c>, any type with <c>view_enter</c> / <c>modify_enter</c> and the matching exit), an access token
    /// (only checked to differ from the others), or an entity (whose token the block gives), and its name inside the
    /// block is a <c>Viewing</c> or <c>Modifying</c> token, a <c>Optional</c> of it in a <c>try</c> block. A target named
    /// twice is RF-S650, one that can't be opened with its verb RF-S651, an enter that gives no token RF-S654, a
    /// <c>try</c> on one the block doesn't enter RF-S611, and a <c>Guarded</c> an enclosing block of the routine
    /// already opened RF-S630.
    /// </summary>
    private void AnalyzeAccessStatement(AccessStatement access)
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            ReportError(code: SemanticDiagnosticCode.FeatureNotInSuflae,
                message: $"A {_registry.Rules.Name} program has no 'viewing' or 'modifying' block: an entity it shares is " +
                         "used directly.",
                location: access.Location);
            return;
        }

        if (access.Targets.Count > MaxAccessTargets)
        {
            ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                message: $"This block opens {access.Targets.Count} targets, and one block opens at most " +
                         $"{MaxAccessTargets}. Split it into blocks that each open what belongs together.",
                location: access.Location);
        }

        var opened = new List<(AccessTarget Target, TypeSymbol Token, bool LockBacked, string? Path)>();
        var seenPaths = new Dictionary<string, AccessTarget>(comparer: StringComparer.Ordinal);
        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        // The names of targets the block can't open, reported already: each is still declared inside the block (of the
        // error type), so a use of it there is not a second error about an unknown name.
        var unopened = new List<AccessTarget>();
        foreach (AccessTarget target in access.Targets)
        {
            TypeSymbol type = AnalyzeExpression(expression: target.Target);
            string? path = BuildAccessPath(expr: target.Target);
            string shown = path ?? "this target";
            if (path != null && !seenPaths.TryAdd(key: path, value: target))
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNamedTwice,
                    message: $"You name '{path}' twice in this block. A block opens each target once: name it once, " +
                             "with the verb it needs ('modifying' also reads).",
                    location: target.Location);
                if (target.Name.Length > 0 && seenNames.Add(item: target.Name))
                {
                    unopened.Add(item: target);
                }

                continue;
            }

            if (target.Name.Length == 0)
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                    message: "This target has no name of its own for its token inside the block. Give it one with " +
                             "'as': 'modifying accounts[0] as first'.",
                    location: target.Location);
                continue;
            }

            if (!seenNames.Add(item: target.Name))
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNamedTwice,
                    message: $"Two targets of this block go by '{target.Name}' inside it. Give each its own name " +
                             "with 'as'.",
                    location: target.Location);
                continue;
            }

            if (type is ErrorTypeSymbol ||
                AccessTargetToken(target: target, type: type, isTry: access.IsTry, shown: shown,
                    lockBacked: out bool lockBacked) is not { } token)
            {
                unopened.Add(item: target);
                continue;
            }

            if (lockBacked && path != null)
            {
                CheckGuardedOpenedAgain(path: path, isWrite: target.IsWrite, location: target.Location);
            }

            opened.Add(item: (target, token, lockBacked, path));
        }

        int holdsBefore = _activeAccessHolds.Count;
        foreach ((AccessTarget target, TypeSymbol _, bool lockBacked, string? path) in opened)
        {
            if (lockBacked && path != null)
            {
                _activeAccessHolds.Add(item: (path, GetOrAssignHandleIdentity(path: path), target.IsWrite,
                    target.Location));
            }
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: "access");
        foreach (AccessTarget target in unopened)
        {
            _registry.DeclareVariable(name: target.Name, type: ErrorTypeSymbol.Instance, location: target.Location);
        }

        var lockBackedTokens = new List<VariableInfo>();
        foreach ((AccessTarget target, TypeSymbol token, bool lockBacked, string? _) in opened)
        {
            _registry.DeclareVariable(name: target.Name, type: token, location: target.Location);
            if (lockBacked && !access.IsTry && _registry.LookupVariable(name: target.Name) is { } variable)
            {
                _lockBackedTokens[key: variable] = target.IsWrite;
                lockBackedTokens.Add(item: variable);
            }
        }

        AnalyzeStatement(statement: access.Body);
        foreach (VariableInfo token in lockBackedTokens)
        {
            _lockBackedTokens.Remove(key: token);
        }

        _registry.ExitScope();
        _activeAccessHolds.RemoveRange(index: holdsBefore, count: _activeAccessHolds.Count - holdsBefore);
    }

    /// <summary>
    /// The type a <c>viewing</c> / <c>modifying</c> target's name has inside its block (its token, a <c>Optional</c> of it
    /// in a <c>try</c> block), or null after reporting why the target can't be opened that way.
    /// <paramref name="lockBacked"/> says whether the block takes a <c>Guarded</c>'s access (an <c>Exclusive</c> or
    /// <c>MultiRead</c> policy). A target the block enters gets its analyzed enter and exit calls on
    /// <see cref="AccessTarget.Enter"/> / <see cref="AccessTarget.Exit"/>.
    /// </summary>
    private TypeSymbol? AccessTargetToken(AccessTarget target, TypeSymbol type, bool isTry, string shown,
        out bool lockBacked)
    {
        lockBacked = false;
        string verb = target.IsWrite ? "modifying" : "viewing";
        TypeSymbol? inner;
        string enterName = target.IsWrite
            ? Declaration.RuntimeContract.AccessBlock.ModifyEnter
            : Declaration.RuntimeContract.AccessBlock.ViewEnter;
        if (type.BareName == Declaration.RuntimeContract.Guarded)
        {
            string? policy = Wrappers.GuardedPolicy(type: type);
            if (policy == "ReadOnly" && target.IsWrite)
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                    message: $"You open '{shown}' with 'modifying', but it is a Guarded with the ReadOnly policy, which " +
                             "nothing may change. Read it directly, or keep it under the Exclusive or MultiRead policy.",
                    location: target.Location);
                return null;
            }

            if (policy == "Exclusive" && !target.IsWrite)
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                    message: $"You open '{shown}' with 'viewing', but it is a Guarded with the Exclusive policy, which " +
                             "lets in one user at a time and has only 'modifying'. Open it with 'modifying', which " +
                             "also reads.",
                    location: target.Location);
                return null;
            }

            lockBacked = Declaration.GuardAccess.TakesAccess(type: type);
            return EnteredTargetToken(target: target, type: type, isTry: isTry, shown: shown, enterName: enterName);
        }

        if (type.BareName is Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Roamed)
        {
            ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                message: $"You open '{shown}' with '{verb}', but it is {Article.For(name: type.BareName)} " +
                         $"{type.BareName}, which has nothing to open: use it directly. Hand it where a Viewing or a " +
                         "Modifying token goes, and read and write through it.",
                location: target.Location);
            return null;
        }

        if (type.BareName is Declaration.RuntimeContract.Tracked or Declaration.RuntimeContract.Witnessed or "Watched")
        {
            string strong = type.BareName switch
            {
                Declaration.RuntimeContract.Tracked => "a Retained, which you then use directly",
                Declaration.RuntimeContract.Witnessed => "a Guarded, which you then open with this block",
                _ => "a Roamed, which you then use directly"
            };
            ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                message: $"You open '{shown}' with '{verb}', but it is a {type.BareName}, a weak handle that doesn't keep " +
                         $"its entity. Take a strong handle with '{shown}.hold()' first ({strong}). 'hold()' fails when " +
                         "the entity is gone: 'try' gives none then.",
                location: target.Location);
            return null;
        }

        if (_registry.LookupMemberRoutine(type: type, memberRoutineName: enterName) != null ||
            _registry.LookupMemberRoutine(type: type, memberRoutineName: enterName, isFailable: true) != null)
        {
            return EnteredTargetToken(target: target, type: type, isTry: isTry, shown: shown, enterName: enterName);
        }

        if (type.BareName is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying)
        {
            inner = Wrappers.GetWrapperInnerType(wrapperType: type);
            if (target.IsWrite && type.BareName == Declaration.RuntimeContract.Viewing)
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                    message: $"You open '{shown}' with 'modifying', but it is a Viewing token, permission to read only. " +
                             "Open it with 'viewing', or take a Modifying token where it comes from.",
                    location: target.Location);
                return null;
            }

            if (isTry)
            {
                ReportTryOnUntryable(target: target, shown: shown,
                    why: "it is an access token, which the block only names");
                return null;
            }
        }
        else if (type is EntityTypeSymbol)
        {
            inner = type;
            // An entity of the routine's own is a variable. One inside another (a member variable, an element) is
            // reached through the token of what holds it: opening it apart would hand out a second token on part of
            // what that token already reaches.
            if (target.Target is not IdentifierExpression)
            {
                ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                    message: $"You open '{shown}' with '{verb}', but it is an entity inside another one. Reach it " +
                             "through the token of the entity that holds it, and open that one if you have no token " +
                             "for it yet.",
                    location: target.Location);
                return null;
            }

            // A global is only read (every job reaches it), and a block on an entity takes no access of its own, so
            // `modifying` it would change it with no lock. A Guarded global has the pair and takes its access above.
            if (target.IsWrite && target.Target is IdentifierExpression { Name: var globalName } &&
                _registry.LookupVariable(name: globalName) is { IsGlobal: true, IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.ModifyingCallOnImmutable,
                    message: $"You open the global '{shown}' with 'modifying', but a global is only read: every job " +
                             $"reaches it, and a block on a plain entity takes no access that would keep the others " +
                             $"out. Open it with 'viewing' to read it, or declare it as a Guarded " +
                             $"(`global {shown}: Guarded[{type.ShortTypeName}, Exclusive]`) and change it inside " +
                             "'modifying'.",
                    location: target.Location);
                return null;
            }

            if (isTry)
            {
                ReportTryOnUntryable(target: target, shown: shown,
                    why: "it is an entity of this routine's own, which the block only names");
                return null;
            }
        }
        else
        {
            string what = BuildAccessPath(expr: target.Target) is { } named
                ? $"You open '{named}' with '{verb}', but it is {Article.For(name: type.Name)} {type.Name}."
                : $"You open {Article.For(name: type.Name)} {type.Name} with '{verb}'.";
            ReportError(code: SemanticDiagnosticCode.AccessTargetNotOpenable,
                message: $"{what} A block opens a type with '{enterName}()' (a Guarded, a FileStorage), an access " +
                         "token (Viewing, Modifying) or an entity.",
                location: target.Location);
            return null;
        }

        if (inner == null ||
            _registry.LookupType(name: target.IsWrite
                ? Declaration.RuntimeContract.Modifying
                : Declaration.RuntimeContract.Viewing) is not { } tokenDef)
        {
            return null;
        }

        return _registry.GetOrCreateResolution(genericDef: tokenDef, typeArguments: [inner]);
    }

    /// <summary>
    /// The name's type for a target the block enters: analyzes <c>t.view_enter()</c> (<c>try t.view_enter()</c> in a
    /// <c>try</c> block) and <c>t.view_exit()</c> (the <c>modify_</c> pair for <c>modifying</c>) onto the target, on
    /// the target itself when it is a name or a member path, else on a variable the block holds it in. Reports RF-S654
    /// when the enter gives no token of the block's kind or the exit is missing.
    /// </summary>
    private TypeSymbol? EnteredTargetToken(AccessTarget target, TypeSymbol type, bool isTry, string shown,
        string enterName)
    {
        string exitName = target.IsWrite
            ? Declaration.RuntimeContract.AccessBlock.ModifyExit
            : Declaration.RuntimeContract.AccessBlock.ViewExit;
        string tokenName = target.IsWrite
            ? Declaration.RuntimeContract.Modifying
            : Declaration.RuntimeContract.Viewing;
        if (_registry.LookupMemberRoutine(type: type, memberRoutineName: exitName) == null)
        {
            ReportError(code: SemanticDiagnosticCode.AccessEnterWithoutToken,
                message: $"You open '{shown}', and '{type.Name}' has '{enterName}()' but no '{exitName}()'. A type a " +
                         $"block enters declares both: '{exitName}()' gives up what '{enterName}()' took, on every way " +
                         "out of the block.",
                location: target.Location);
            return null;
        }

        Expression receiver = target.Target;
        if (!IsAccessPath(expr: target.Target))
        {
            target.Hidden = $"__access_held{_accessHeldCount++}";
            _registry.DeclareVariable(name: target.Hidden, type: type, location: target.Location);
            receiver = new IdentifierExpression(Name: target.Hidden, Location: target.Location);
        }

        var enter = new CallExpression(
            Callee: new MemberExpression(Object: receiver, MemberName: enterName, Location: target.Location),
            Arguments: [],
            Location: target.Location);
        Expression entered = isTry
            ? new RecoveryExpression(Kind: RecoveryKind.Try, Inner: enter, Location: target.Location)
            {
                IsSynthesized = true
            }
            : enter;
        TypeSymbol enteredType = AnalyzeExpression(expression: entered);
        if (enter.ResolvedType is not { } token || token is ErrorTypeSymbol || enteredType is ErrorTypeSymbol)
        {
            return null;
        }

        if (token.BareName != tokenName)
        {
            ReportError(code: SemanticDiagnosticCode.AccessEnterWithoutToken,
                message: $"You open '{shown}' with '{(target.IsWrite ? "modifying" : "viewing")}', and " +
                         $"'{type.Name}.{enterName}()' gives {Article.For(name: token.Name)} '{token.Name}'. The name a " +
                         $"block hands out is its token: '{enterName}()' returns {Article.For(name: tokenName)} " +
                         $"'{tokenName}[...]'.",
                location: target.Location);
            return null;
        }

        var exit = new CallExpression(
            Callee: new MemberExpression(Object: receiver, MemberName: exitName, Location: target.Location),
            Arguments: [],
            Location: target.Location);
        AnalyzeExpression(expression: exit);
        target.Enter = entered;
        target.Exit = exit;
        return enteredType;
    }

    /// <summary>The number of variables blocks hold a made target in, for their names.</summary>
    private int _accessHeldCount;

    /// <summary>Whether <paramref name="expr"/> is a name or a member path (<c>g</c>, <c>me.table</c>), which a block
    /// may read again for its exit: anything else (a call) is made once, into a variable.</summary>
    private static bool IsAccessPath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression => true,
            MemberExpression member => IsAccessPath(expr: member.Object),
            _ => false
        };
    }

    /// <summary>RF-S611: <c>try viewing</c> / <c>try modifying</c> on a target the block doesn't enter, which can't
    /// fail to open.</summary>
    private void ReportTryOnUntryable(AccessTarget target, string shown, string why)
    {
        ReportError(code: SemanticDiagnosticCode.TryAccessOnUntryable,
            message: $"You open '{shown}' with 'try', but {why}: opening it never fails. Remove 'try', and use the " +
                     "name as the token it is.",
            location: target.Location);
    }

    /// <summary>
    /// RF-S630: reports opening the Guarded at <paramref name="path"/> while an enclosing block of the same routine has
    /// it (or an alias of it) open, for reading or for writing.
    /// </summary>
    private void CheckGuardedOpenedAgain(string path, bool isWrite, SourceLocation location)
    {
        int identity = GetOrAssignHandleIdentity(path: path);
        foreach ((string Handle, int Identity, bool IsWriter, SourceLocation Location) hold in _activeAccessHolds)
        {
            bool sameMemory = hold.Identity == identity || PathsOverlap(a: hold.Handle, b: path);
            if (!sameMemory)
            {
                continue;
            }

            string held = hold.Handle == path
                ? $"'{path}'"
                : $"'{hold.Handle}', which is '{path}' under another name,";
            string verb = isWrite ? "modifying" : "viewing";
            string heldVerb = hold.IsWriter ? "modifying" : "viewing";
            ReportError(code: SemanticDiagnosticCode.GuardedOpenedAgain,
                message: $"You open '{path}' with '{verb}' inside a '{heldVerb}' block (line {hold.Location.Line}) that " +
                         $"already has {held} open. Waiting here would never end, since the outer block can't end " +
                         "while it waits, and going in would see that block's work half done. Use the token the outer " +
                         "block gives, and pass it to the routines you call.",
                location: location);
            return;
        }
    }

    /// <summary>
    /// Extracts a path key for the Guarded handle of a `view`/`modify` access expression — the
    /// receiver of `s.view()` / `s.modify()`, as a dotted path so distinct fields are distinct
    /// handles: `s` → "s", `s.a` → "s.a". This makes `s.a.modify()` and `s.b.modify()` independent
    /// (both open in one scope) while `s.a` opened for writing twice still conflicts. Returns null for
    /// receivers that aren't a pure identifier/field path (indexing, call results), which the
    /// readers-XOR-writer check conservatively skips (no false positives).
    /// </summary>
    private static string? ExtractAccessReceiverName(Expression resource)
    {
        return resource is CallExpression { Callee: MemberExpression { Object: var receiver } }
            ? BuildAccessPath(expr: receiver)
            : null;
    }

    /// <summary>Whether two access paths overlap — equal, or one a prefix of the other on a field
    /// boundary (`s.a` overlaps `s.a.x` and `s`, but not `s.b` or `s.ab`). Overlapping paths name
    /// memory where one contains the other, so concurrent access to them conflicts.</summary>
    private static bool PathsOverlap(string a, string b)
    {
        return a == b || a.StartsWith(value: b + ".") || b.StartsWith(value: a + ".");
    }

    /// <summary>Builds a dotted path for an identifier/field-access chain (`s.a.b` → "s.a.b"), or
    /// null if the chain bottoms out on anything else (indexing, a call, a literal).</summary>
    private static string? BuildAccessPath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var prop } =>
                BuildAccessPath(expr: inner) is { } prefix
                    ? $"{prefix}.{prop}"
                    : null,
            _ => null
        };
    }

    /// <summary>Returns the controller identity for a Guarded/Witnessed handle path. A path bound to a
    /// tracked handle (recorded at its <c>var</c> declaration, see
    /// <see cref="RecordSharedHandleIdentity"/>) reuses its identity; an untracked path is assigned a
    /// fresh unique identity on first use and remembered, so repeated uses of the same path match
    /// (the readers-XOR-writer check then degrades to per-path keying — the pre-aliasing behaviour —
    /// for handles whose origin it can't see).</summary>
    private int GetOrAssignHandleIdentity(string path)
    {
        if (_sharedHandleIdentity.TryGetValue(key: path, value: out int id))
        {
            return id;
        }

        id = _nextSharedHandleIdentity++;
        _sharedHandleIdentity[key: path] = id;
        return id;
    }

    /// <summary>Records the controller identity of a freshly declared Guarded/Witnessed handle from its
    /// initializer, so later aliases and access-token receivers resolve to the same controller:
    /// <list type="bullet">
    /// <item>a clone (<c>s.share()</c> RC copy / <c>s.observe()</c> strong→weak) or a plain copy
    /// (<c>var s2 = s</c>) INHERITS the source handle's identity;</item>
    /// <item>anything else — including a fresh Arc construction <c>Guarded[T, P](from: n)</c> — gets a
    /// fresh identity (conservative — a missed alias only weakens the check, never a false positive).</item>
    /// </list></summary>
    private void RecordSharedHandleIdentity(string name, Expression? initializer)
    {
        int identity = initializer switch
        {
            // Clone: `s.share()` (RC copy verb — mint a co-owner) / `s.observe()` (strong→weak conversion)
            // — inherit the receiver handle's identity (both alias the same controller).
            CallExpression
            {
                Callee: MemberExpression
                {
                    Object: var receiver,
                    MemberName: Declaration.RuntimeContract.RefCount.Share or "observe"
                }
            } when BuildAccessPath(expr: receiver) is { } recvPath => GetOrAssignHandleIdentity(
                path: recvPath),
            // Plain copy: `var s2 = s` — inherit (usually blocked by the copy-verb rule, handled
            // here for completeness).
            IdentifierExpression copySource => GetOrAssignHandleIdentity(path: copySource.Name),
            _ => _nextSharedHandleIdentity++
        };
        _sharedHandleIdentity[key: name] = identity;
    }
}
