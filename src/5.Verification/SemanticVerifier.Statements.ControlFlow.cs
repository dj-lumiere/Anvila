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
    /// A loop whose condition is a literal builds as written, with a notice. <c>while true</c> / <c>until false</c>
    /// ends only by <c>break</c> or <c>return</c>, which <c>loop</c> says. <c>while false</c> / <c>until true</c> never
    /// runs its body, which an editor shows faded. A named Bool, even a preset, is a condition like any other.
    /// </summary>
    internal void NoteLiteralLoopCondition(WhileStatement whileStmt)
    {
        Expression written = whileStmt.IsUntil && whileStmt.Condition is UnaryExpression
        {
            Operator: UnaryOperator.Not
        } negated
            ? negated.Operand
            : whileStmt.Condition;
        if (written is not LiteralExpression { Value: bool literal })
        {
            return;
        }

        string header = $"{(whileStmt.IsUntil ? "until" : "while")} {(literal ? "true" : "false")}";
        if (literal != whileStmt.IsUntil)
        {
            // `loop` takes no carried values: a loop with `with` keeps its header.
            SourceLocation end = written.Location;
            SuggestedFix? fix = !whileStmt.HadWith && end.Line == whileStmt.Location.Line
                ? new SuggestedFix(Title: "Write 'loop'",
                    Edits:
                    [
                        new TextEdit(StartLine: whileStmt.Location.Line, StartColumn: whileStmt.Location.Column,
                            EndLine: end.Line, EndColumn: end.Column + (literal ? 4 : 5), NewText: "loop")
                    ])
                : null;
            ReportNotice(code: SemanticWarningCode.InfiniteLoopAsWhile,
                message: $"`{header}` runs until a `break` or `return`: write `loop` for a loop like that.",
                location: whileStmt.Location,
                fix: fix);
            return;
        }

        Statement body = whileStmt.Body is BlockStatement { Statements.Count: > 0 } block
            ? block.Statements[index: 0]
            : whileStmt.Body;
        ReportNotice(code: SemanticWarningCode.UnreachableLoopBody,
            message: $"This code never runs: `{header}` doesn't enter the loop.",
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
                message: $"While condition must be boolean, got '{conditionType.Name}'.",
                location: whileStmt.Condition.Location);
        }

        // Analyze loop body. The loop ends at a `break`, and also when the condition is false, maybe before the first
        // turn: then what was unassigned before it still is. `while true` ends only at a `break`.
        bool endsOnlyAtBreak = whileStmt.Condition is LiteralExpression { LiteralType: Builder.Tokenizer.TokenType.True };
        var unassignedBefore = endsOnlyAtBreak
            ? []
            : _deadrefVariables.Where(predicate: n =>
                n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal)).ToList();
        _unassignedAtBreak.Push(item: []);
        _registry.EnterScope(kind: ScopeKind.Loop, name: "while");
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
                $"'{expandStmt.SourceName}' is not a valid expand source — use 'allmemvarof', 'openmemvarof', 'caseof', or 'branchof'.",
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

        _registry.EnterScope(kind: ScopeKind.Loop, name: "expand");

        // Resolve the source type for early error surfacing (a bare generic param resolves fine).
        // The result is intentionally unused — no member walk happens before monomorphization.
        ResolveType(typeExpr: expandStmt.SourceType);

        // Register the per-part handle so `m`, `m.name`, `m.id` resolve leniently in the body.
        _registry.DeclareVariable(name: expandStmt.HandleName,
            type: BuildtimeHandleTypeSymbol.Instance);

        bool prevInExpand = _inExpandBody;
        _inExpandBody = true;
        AnalyzeStatement(statement: expandStmt.Body);
        _inExpandBody = prevInExpand;

        _registry.ExitScope();
    }

    private void AnalyzeEachStatement(EachStatement eachStmt)
    {
        _registry.EnterScope(kind: ScopeKind.Loop, name: "each");

        // Analyze iterable expression
        TypeSymbol iterableType = AnalyzeExpression(expression: eachStmt.Iterable);

        // Get element type from iterable
        TypeSymbol elementType =
            GetIterableElementType(iterableType: iterableType, location: eachStmt.Location);

        // Handle either simple variable or destructuring pattern
        if (eachStmt.Variable != null)
        {
            // Simple variable binding: for item in items
            _registry.DeclareVariable(name: eachStmt.Variable,
                type: elementType,
                location: eachStmt.Location);
            _deadrefVariables.Remove(item: eachStmt.Variable);
        }
        else if (eachStmt.VariablePattern != null)
        {
            // Destructuring pattern: for (index, item) in items.enumerate()
            DeclareEachDestructuringBindings(eachStmt: eachStmt, elementType: elementType);
        }

        // #22: Track active iteration source for reshaping-during-iteration check
        string? iterationSourceName = eachStmt.Iterable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        if (iterationSourceName != null)
        {
            _activeIterationSources.Add(item: iterationSourceName);
        }

        // Analyze loop body
        AnalyzeStatement(statement: eachStmt.Body);

        if (iterationSourceName != null)
        {
            _activeIterationSources.Remove(item: iterationSourceName);
        }

        _registry.ExitScope();
    }

    /// <summary>
    /// Declares the bindings of an each-loop destructuring pattern: matched positionally against a
    /// tuple element type, or with error types (plus a diagnostic) for a non-tuple element type.
    /// </summary>
    private void DeclareEachDestructuringBindings(EachStatement eachStmt, TypeSymbol elementType)
    {
        if (elementType is TupleTypeSymbol tupleType)
        {
            DeclareTupleDestructuringBindings(eachStmt: eachStmt, tupleType: tupleType);
        }
        else
        {
            DeclareErrorDestructuringBindings(eachStmt: eachStmt, elementType: elementType);
        }
    }

    /// <summary>
    /// Declares each-loop bindings for a tuple element type, matching positionally and reporting an
    /// arity mismatch when the binding count differs from the tuple's element count.
    /// </summary>
    private void DeclareTupleDestructuringBindings(EachStatement eachStmt, TupleTypeSymbol tupleType)
    {
        int bindingCount = eachStmt.VariablePattern!.Bindings.Count;
        if (bindingCount != tupleType.Arity)
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Destructuring pattern has {bindingCount} bindings but tuple has {tupleType.Arity} elements.",
                location: eachStmt.VariablePattern.Location);
        }

        for (int i = 0; i < eachStmt.VariablePattern.Bindings.Count; i++)
        {
            DestructuringBinding binding = eachStmt.VariablePattern.Bindings[index: i];
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
    private void DeclareErrorDestructuringBindings(EachStatement eachStmt, TypeSymbol elementType)
    {
        ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
            message: $"Cannot destructure non-tuple type '{elementType.Name}' in for loop.",
            location: eachStmt.VariablePattern!.Location);
        foreach (string? name in eachStmt.VariablePattern
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
            // `Retained`, `Hijacked` or `Maybe`) pins the variable.
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
        // A token (`each m in marks` binds one) is matched by the value it reaches.
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
        // The step of an `each` loop whose walk starts read-only hands its element out as `Viewing`.
        bool viewingElement = whenStmt.IsEachStep &&
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
                    ViewingElement: viewingElement),
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
        _deadrefVariables.Clear();
        _deadrefVariables.UnionWith(other: deadrefBefore);
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
    /// arm <c>is Crashables e => throw e</c> (propagating, in a failable routine) or <c>is Crashables e => pierce e</c>
    /// (crashing loudly, anywhere else). An arm for one crashable (<c>is NopeError e</c>) handles only that one.
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
        bool ViewingElement = false);

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
                location: elsePat.Location);
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
        bool carrierUsesNoneForAbsent = carrierBase is "Maybe";
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
            string? patternKey = GetPatternKey(pattern: pattern);
            if (patternKey != null && !seenPatterns.Add(item: patternKey))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicatePattern,
                    message: $"Duplicate pattern: {patternKey}.",
                    location: pattern.Location);
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

        if (ret.Value != null)
        {
            // Bare `return` is normalized by NoneReturnNormalizationPass to `return None`
            // with Location == ret.Location. Treat these as unreachable-path terminators:
            // skip the type check so exhaustive when-else blocks can end with bare `return`.
            bool isNormalizedBareReturn =
                ret.Value is IdentifierExpression { Name: "None" } blankId &&
                blankId.Location == ret.Location;

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

            if (!isNormalizedBareReturn && _currentRoutine.ReturnType != null &&
                !IsAssignableTo(source: returnType, target: _currentRoutine.ReturnType))
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
                        : $"Cannot return value of type '{returnType.Name}' from function expecting '{_currentRoutine.ReturnType.Name}'.",
                    location: ret.Location);
            }
        }
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

        // A recoverable `throw` requires the routine to be declared `!`; the check runs once the body is
        // analyzed (ValidateRoutineBodyPostAnalysis, RF-S750). `pierce` (IsFatal) stays a fatal
        // uncatchable crash that never marks the routine failable.
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
        // generation). A `pierce` never marks failable — it is a crash, not a recoverable failure.
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
            or RecoveryExpression or AgentExpression))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDropTarget,
                message: "'drop' can only be used with routine calls. " +
                         "Use 'drop some_routine()' to drop a result on purpose.",
                location: dropped.Location);
        }

        // Analyze the expression - this validates the expression and checks for errors
        // The result is intentionally discarded
        TypeSymbol discardedType = AnalyzeExpression(expression: dropped.Expression);

        // `drop agent f()` throws the Agent away before anything starts it: its work never runs. Warn even though
        // the value was dropped on purpose.
        if (IsAgentType(type: discardedType))
        {
            ReportWarning(code: SemanticWarningCode.AsyncAgentNeverLaunched,
                message: dropped.Expression is AgentExpression
                    ? AgentNeverStartedMessage()
                    : "`drop` throws this Agent away before anything starts it, so its work never runs. Call " +
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
        // Mark the resource node so what `view()` / `modify()` on a locking Guarded return is accepted
        // directly here; the same call anywhere else is rejected (RF-S629), keeping its lock strictly
        // `using`-scoped. Save/restore to support nested `using`.
        ISyntaxTreeNode? previousUsingResource = _usingResourceNode;
        _usingResourceNode = usingStmt.Resource;
        // Analyze the resource expression to get its type
        TypeSymbol resourceType = AnalyzeExpression(expression: usingStmt.Resource);
        _usingResourceNode = previousUsingResource;

        // Readers-XOR-writer (RF-S630): if this `using` takes a Guarded's lock (`view()` / `modify()` on a
        // named handle), check it against the holds already live in the enclosing `using` scopes on the SAME
        // handle. A writer (`modify`) conflicts with any other hold; readers (`view`) coexist.
        // The hold is pushed for the duration of the body and popped on exit, so only OVERLAPPING
        // scopes conflict (sequential `using`s on the same handle are fine).
        string accessBase = resourceType.BareName;
        bool opensLock = accessBase is Declaration.RuntimeContract.ViewingGuarded
            or Declaration.RuntimeContract.ModifyingGuarded;
        bool isWriter = accessBase is Declaration.RuntimeContract.ModifyingGuarded
            or Declaration.RuntimeContract.MultiModifyingGuarded;
        string? accessHandle = opensLock
            ? ExtractAccessReceiverName(resource: usingStmt.Resource)
            : null;
        if (accessHandle != null)
        {
            CheckReadersXorWriter(usingStmt: usingStmt,
                isWriter: isWriter,
                accessHandle: accessHandle);
        }

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

        // The tokens a Guarded opener's `enter` binds are backed by its lock for this block: recorded by where they came
        // from, since their type is a plain Viewing / Modifying.
        List<VariableInfo> lockBacked = Declaration.RuntimeContract.GuardOpenerTypes.Contains(item: accessBase)
            ? (usingStmt.Parts ?? [usingStmt.Name])
             .Select(selector: name => _registry.LookupVariable(name: name))
             .OfType<VariableInfo>()
             .ToList()
            : [];
        foreach (VariableInfo token in lockBacked)
        {
            _lockBackedTokens[key: token] = isWriter;
        }

        // Analyze the body. (A token's use after its source changed is RazorForge's TokenLifetimeChecker's, RF-S643.)
        AnalyzeStatement(statement: usingStmt.Body);

        // #171/#172: Token/resource scope escape — validate that the using-bound variable
        // is not returned or stored in outer scope (handled by ValidateNotTokenReturnType
        // for tokens, and conceptually enforced by scope exit for resources)

        foreach (VariableInfo token in lockBacked)
        {
            _lockBackedTokens.Remove(key: token);
        }

        _registry.ExitScope();

        // Pop the MT access hold now that the scope has closed (readers-XOR-writer, RF-S630).
        if (accessHandle != null)
        {
            _activeAccessHolds.RemoveAt(index: _activeAccessHolds.Count - 1);
        }

        // The `fallback` branch runs when acquisition fails: no hold is taken and the bound
        // name is NOT in scope. Analyze it in its own fresh scope, outside the access hold.
        if (usingStmt.FallbackBody != null)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "using-fallback");
            AnalyzeStatement(statement: usingStmt.FallbackBody);
            _registry.ExitScope();
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

        if (usingStmt.FallbackBody != null)
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message: "A 'using' with a 'fallback' binds the resource itself, so it takes one name after 'as', " +
                         $"not '{usingStmt.BoundText}'.",
                location: usingStmt.Location);
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

    /// <summary>
    /// RF-S630 readers-XOR-writer: reports a conflict when the access token this `using` opens
    /// overlaps an active hold in an enclosing scope (a writer excludes all; readers coexist), then
    /// pushes this hold onto the active-holds stack for the duration of the body.
    /// </summary>
    private void CheckReadersXorWriter(UsingStatement usingStmt, bool isWriter, string accessHandle)
    {
        int accessIdentity = GetOrAssignHandleIdentity(path: accessHandle);
        foreach ((string Handle, int Identity, bool IsWriter, SourceLocation Location) hold in
                 _activeAccessHolds)
        {
            // Two holds touch the same memory when they resolve to the same controller identity
            // (aliased handles — `s` and `s2 = s.share()`) OR when their syntactic paths overlap
            // on a field boundary (a parent handle and one of its sub-handles).
            bool sameMemory = hold.Identity == accessIdentity ||
                              PathsOverlap(a: hold.Handle, b: accessHandle);
            if (!sameMemory || !isWriter && !hold.IsWriter)
            {
                continue;
            }

            string newKind = isWriter
                ? "modify()"
                : "view()";
            string heldKind = hold.IsWriter
                ? "modify()"
                : "view()";
            string overlapNote;
            if (hold.Handle == accessHandle)
            {
                overlapNote = "the same shared handle";
            }
            else if (hold.Identity == accessIdentity)
            {
                overlapNote = $"the aliased handle '{hold.Handle}' (same shared data)";
            }
            else
            {
                overlapNote = $"the overlapping handle '{hold.Handle}'";
            }

            ReportError(code: SemanticDiagnosticCode.ReadersXorWriter,
                message:
                $"'using {accessHandle}.{newKind}' waits for the lock that the enclosing 'using ... {heldKind}' on " +
                $"{overlapNote} holds until its block ends, so this block would never start. A writer ('modify') " +
                "excludes every other hold, and readers ('view') share the lock only with other readers. Close the " +
                "outer block first, or use the token it already bound.",
                location: usingStmt.Location);
            break;
        }

        _activeAccessHolds.Add(item: (accessHandle, accessIdentity, isWriter, usingStmt.Location));
    }

    /// <summary>
    /// Validates a RazorForge `using` resource obeys `Enterable`; when it does, overrides the bound
    /// variable type with `enter`'s non-void return and requires `try_enter` for a `fallback` branch.
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
            ReportError(code: SemanticDiagnosticCode.UsingTargetMissingEnterExit,
                message:
                $"Using target of type '{resourceType.Name}' must obey 'Enterable' (which provides " +
                "'enter'/'exit') for scope-managed resource access.",
                location: usingStmt.Location);
            return;
        }

        // The bound variable's type is `enter`'s return type when non-void (pass-through).
        // LookupMemberRoutine handles generic fallback (Viewing[Point].enter -> Viewing.enter).
        RoutineInfo? enterMemberRoutine =
            _registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "enter");
        if (enterMemberRoutine?.ReturnType is { IsNone: false } enterReturn)
        {
            boundType = enterReturn;
        }

        // With a `fallback`, `try_enter` takes the hold instead of `enter`, so the scope binds what `entered` gives
        // (a Guarded opener's token), or the resource itself when the type has no `entered`.
        if (usingStmt.FallbackBody != null)
        {
            boundType = _registry.LookupMemberRoutine(type: resourceType,
                    memberRoutineName: Declaration.RuntimeContract.Entered)?.ReturnType is { IsNone: false } entered
                ? entered
                : resourceType;
        }

        // A `fallback` branch drives a non-blocking acquisition — the resource must
        // provide `try_enter` (returns Bool: did the hold succeed?). Types whose entry
        // can only block (no `try_enter`) cannot take a `fallback`.
        if (usingStmt.FallbackBody != null &&
            _registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "try_enter") ==
            null)
        {
            ReportError(code: SemanticDiagnosticCode.UsingFallbackRequiresTryEnter,
                message:
                $"'using ... fallback' requires the resource type '{resourceType.Name}' to " +
                "provide 'try_enter' (a non-blocking acquisition). This type only supports " +
                "blocking entry — drop the 'fallback' branch.",
                location: usingStmt.Location);
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
