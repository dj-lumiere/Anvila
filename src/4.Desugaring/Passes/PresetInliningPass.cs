using Builder.Declaration;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Desugaring.Passes;

/// <summary>
/// Substitutes <c>IdentifierExpression</c> nodes for preset constants with the
/// preset's literal value expression.
///
/// <para>Must run <em>first</em> in the per-file desugaring pipeline so that all
/// subsequent passes (operator lowering, f-string lowering, etc.) operate on
/// concrete literal values rather than named identifiers. For example,</para>
/// <code>
/// preset RUNS: S64 = 20
/// for _ in 0..RUNS ?? for _ in 0..S64(20)
/// </code>
/// <para>Needs <see cref="VariableInfo.PresetValue"/> to be set on preset entries in
/// the <see cref="TypeRegistry"/>, which happens during
/// <c>CollectPresetDeclaration</c> in Phase 3.</para>
/// </summary>
internal sealed class PresetInliningPass(DesugaringContext ctx) : AstRewriter
{
    // Presets are GLOBAL by default (public constants like `S64_MAX`/`DECIMAL_PI` are part of the System
    // prelude and usable everywhere). A `secret preset` is FILE-PRIVATE: inlinable only inside the file
    // that declares it. `_ownPresets` is the set of preset names declared in the file currently
    // being lowered — a secret preset from another file is NOT in it, so it is left un-inlined and a bare
    // identifier that merely shares a name (e.g. a user `record B` vs a stdlib `secret preset B`) resolves
    // as its own declaration. Null means "no scoping" (builder-synthesized variant bodies).
    //
    // NOTE (module-private secret, WIP): the intended end-state is that `secret` is MODULE-private for
    // ACCESS (un-importable, usable across files of the same module) enforced at the VISIBILITY layer with
    // a diagnostic — NOT by widening inlining to module scope here. A prior attempt to inline within-module
    // secret presets regressed reachability (Dict/Set memberRoutine bodies pruned → 48 "never defined" in the
    // stdlib harness). Keep inlining FILE-scoped; enforce module-private access separately.
    private Dictionary<string, PresetDeclaration>? _ownPresets;

    /// <summary>Presets whose value is being inlined right now, so a preset that reaches itself through
    /// other presets is left as an identifier (reported downstream) instead of recursing forever.</summary>
    private readonly HashSet<string> _inlining = new(comparer: StringComparer.Ordinal);

    /// <summary>The names the routine being lowered binds itself (parameters, variables, loop and pattern names): a
    /// preset of the same name is out of sight there (`each letter in word` next to a preset `letter`).</summary>
    private HashSet<string> _locals = new(comparer: StringComparer.Ordinal);

    /// <summary>The presets of each library module, over all its files, once computed.</summary>
    private Dictionary<string, Dictionary<string, PresetDeclaration>>? _stdlibPresetsByModule;

    /// <summary>Every <c>preset</c> declared in the current file, keyed by name (public or secret).</summary>
    private static Dictionary<string, PresetDeclaration> CollectOwnPresets(Program program)
    {
        var map = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            if (decl is PresetDeclaration preset)
            {
                map[key: preset.Name] = preset;
            }
        }

        return map;
    }

    /// <summary>
    /// Runs this builder phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        _ownPresets = CollectOwnPresets(program: program);
        // A secret preset of a library module is the module's: another file of the module may name it.
        if (ctx.Registry.StdlibPrograms.FirstOrDefault(predicate: p => ReferenceEquals(objA: p.Program, objB: program))
                is { Module: { } module, FilePath: { } path } &&
            (_stdlibPresetsByModule ??= StdlibPresetsByModule()).TryGetValue(key: ModuleKey(module: module, file: path),
                value: out Dictionary<string, PresetDeclaration>? shared))
        {
            foreach ((string name, PresetDeclaration preset) in shared)
            {
                _ownPresets.TryAdd(key: name, value: preset);
            }
        }

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            _locals = new HashSet<string>(comparer: StringComparer.Ordinal);
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                {
                    _locals = AstWalker.BoundNames(node: r);
                    Statement newBody = VisitStatement(stmt: r.Body);
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
    /// Inlines presets in all synthesized bodies in <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated.
    /// </summary>
    public void RunOnVariantBodies()
    {
        // A variant body is built from the body of the routine it recovers, so it may name a file-private
        // secret preset of the file that routine is written in (a library routine recovered for the routine
        // values it reaches keeps its body as written). Public presets inline via the registry.
        var empty = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, PresetDeclaration>>? presetsByFile = null;
        Dictionary<string, Dictionary<string, PresetDeclaration>>? presetsByModule = null;
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            if (ctx.RestoredVariantKeys.Contains(item: key))
            {
                continue; // already inlined at snapshot capture
            }

            RoutineInfo? recovered = ctx.Registry.GetRoutineByExactKey(registryKey: key);
            string? file = recovered?.Location?.FileName;
            presetsByFile ??= StdlibPresetsByFile();
            presetsByModule ??= StdlibPresetsByModule();
            // A secret preset is the module's: the routine's own file, or another file of its module.
            _ownPresets = recovered?.Module is { } module && file != null &&
                          presetsByModule.TryGetValue(key: ModuleKey(module: module, file: file), value: out var mine)
                ? mine
                : file != null && presetsByFile.TryGetValue(key: file, value: out var own)
                    ? own
                    : empty;
            Statement body = ctx.VariantBodies[key: key];
            _locals = AstWalker.BoundNames(node: body);
            if (recovered != null)
            {
                _locals.UnionWith(other: recovered.Parameters.Select(selector: p => p.Name));
            }

            Statement lowered = VisitStatement(stmt: body);
            if (!ReferenceEquals(objA: lowered, objB: body))
            {
                ctx.VariantBodies[key: key] = lowered;
            }
        }
    }

    /// <summary>The presets each library module declares, over all its files, by module. Only a module of several
    /// files whose presets don't share a name across them is listed (else its files' own presets decide).</summary>
    private Dictionary<string, Dictionary<string, PresetDeclaration>> StdlibPresetsByModule()
    {
        var byModule = new Dictionary<string, Dictionary<string, PresetDeclaration>>(comparer: StringComparer.Ordinal);
        var clashing = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program program, string filePath, string module) in ctx.Registry.StdlibPrograms)
        {
            // System's files stay file-scoped: widening its secret presets to the module pruned reachability.
            if (StandardModules.IsSystem(module: module))
            {
                continue;
            }

            foreach (PresetDeclaration preset in program.Declarations.OfType<PresetDeclaration>())
            {
                string key = ModuleKey(module: module, file: filePath);
                if (!byModule.TryGetValue(key: key, value: out Dictionary<string, PresetDeclaration>? presets))
                {
                    presets = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
                    byModule[key: key] = presets;
                }

                if (!presets.TryAdd(key: preset.Name, value: preset))
                {
                    clashing.Add(item: key);
                }
            }
        }

        foreach (string module in clashing)
        {
            byModule.Remove(key: module);
        }

        return byModule;
    }

    /// <summary>The key of a module's presets: the module and the language its file is written in (a Suflae surface
    /// of a module declares presets of the same names as the RazorForge files of that module).</summary>
    private static string ModuleKey(string module, string file)
    {
        return module + "|" + Path.GetExtension(path: file);
    }

    /// <summary>The presets each library file declares, by file.</summary>
    private Dictionary<string, Dictionary<string, PresetDeclaration>> StdlibPresetsByFile()
    {
        var byFile = new Dictionary<string, Dictionary<string, PresetDeclaration>>(comparer: StringComparer.Ordinal);
        foreach ((Program program, _, _) in ctx.Registry.StdlibPrograms)
        {
            foreach (PresetDeclaration preset in program.Declarations.OfType<PresetDeclaration>())
            {
                string file = preset.Location.FileName;
                if (!byFile.TryGetValue(key: file, value: out Dictionary<string, PresetDeclaration>? presets))
                {
                    presets = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
                    byFile[key: file] = presets;
                }

                presets[key: preset.Name] = preset;
            }
        }

        return byFile;
    }

    /// <summary>
    /// Inlines presets in monomorphized instance bodies (pull/(B) demand path). GMP clones from templates
    /// that — under the demand flip — may not have been preset-inlined before monomorphization, so a preset
    /// (e.g. <c>ENTRY_LIVE</c>) survives into the instance and reaches codegen. Mirrors
    /// <see cref="RunOnVariantBodies"/>: an empty own-preset set (instances aren't tied to a source file;
    /// public presets inline via the registry).
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, Instantiation.MonomorphizedBody> bodies)
    {
        _ownPresets = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
        foreach (string key in bodies.Keys.ToList())
        {
            Instantiation.MonomorphizedBody entry = bodies[key: key];
            if (entry.Ast.Body is not { } body)
            {
                continue;
            }

            _locals = AstWalker.BoundNames(node: entry.Ast);
            Statement lowered = VisitStatement(stmt: body);
            if (!ReferenceEquals(objA: lowered, objB: body))
            {
                bodies[key: key] = entry with { Ast = entry.Ast with { Body = lowered } };
            }
        }
    }

    /// <summary>
    /// Lower member list as part of this builder phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }


            _locals = AstWalker.BoundNames(node: m);
            Statement newBody = VisitStatement(stmt: m.Body);
            if (!ReferenceEquals(objA: newBody, objB: m.Body))
            {
                members[index: j] = m with { Body = newBody };
            }
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Inlines a preset whose value is built from other parts (<c>f"name={NAME}"</c>, <c>Byte(0x20u8)</c>).
    /// Each use gets its OWN copy of the value, so analysis and lowering at one site (an f-string lowered
    /// to <c>represent</c> calls, a literal conformed to its context) cannot leak into another, and the
    /// copy is inlined in turn so a preset it names (<c>NAME</c>) is replaced as well.
    /// </summary>
    private Expression InlineComposedPreset(IdentifierExpression id, Expression value,
        TypeSymbol? resolvedType, TypeSymbol? declared)
    {
        // A negated number (`preset LOWEST: B64 = -inf`) keeps the preset's declared type on the number, as a bare
        // number preset does.
        if (value is UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression negated } negation)
        {
            return negation with
            {
                Operand = TypedNumber(literal: negated, declared: declared, resolvedType: null),
                ResolvedType = resolvedType ?? declared
            };
        }

        if (!_inlining.Add(item: id.Name))
        {
            return id;
        }

        try
        {
            Expression inlined = VisitExpression(expr: GenericAstRewriter.DeepCloneExpression(expr: value));
            inlined.ResolvedType ??= resolvedType;
            return inlined;
        }
        finally
        {
            _inlining.Remove(item: id.Name);
        }
    }

    /// <summary>
    /// Inlines an identifier that resolves to a preset constant with its literal value, honoring
    /// secret-preset file scoping and aggregate-preset exclusion. Returns the identifier unchanged
    /// when it is not an inlinable preset.
    /// </summary>
    private Expression LowerIdentifier(Expression expr, IdentifierExpression id)
    {
        if (_locals.Contains(item: id.Name))
        {
            return expr;
        }

        VariableInfo? v = ctx.Registry.LookupVariable(name: id.Name);

        // A `secret preset` is MODULE-private: inline it inside any file of the module that declares
        // it (same granularity as `secret record`/`secret entity`). A reference from ANOTHER module is
        // left un-inlined — since it stays a bare identifier that resolves to a preset, the
        // backend-entry validator flags it (RF-S958), so a secret constant cannot silently be used
        // from another module. Public presets always inline. When there is no current module
        // (`_currentModule == null` for synthesized variant bodies), a secret with a known module is
        // treated as foreign and left un-inlined, preserving the prior variant-body behavior.
        if (v is { IsPreset: true, IsSecret: true } && _ownPresets is not null &&
            !_ownPresets.ContainsKey(key: id.Name))
        {
            return expr;
        }

        // A preset of this file wins over a preset of the same name declared in another module (the registry
        // keeps one of them under the bare name).
        if (v is { IsPreset: true } && _ownPresets is not null &&
            _ownPresets.TryGetValue(key: id.Name, value: out PresetDeclaration? own) &&
            !ReferenceEquals(objA: own.Value, objB: v.PresetValue))
        {
            return own.Value switch
            {
                ListLiteralExpression => expr,
                LiteralExpression ownLiteral => TypedNumber(literal: ownLiteral,
                    declared: ctx.Registry.LookupType(name: own.Type.Name),
                    resolvedType: id.ResolvedType ?? ownLiteral.ResolvedType),
                _ => InlineComposedPreset(id: id, value: own.Value, resolvedType: id.ResolvedType ?? own.Value.ResolvedType,
                    declared: ctx.Registry.LookupType(name: own.Type.Name))
            };
        }

        if (v is { IsPreset: true, PresetValue: not null })
        {
            // Aggregate (Array[T,N]) presets are NOT inlined: substituting the whole list
            // literal at every use site rebuilds the array per reference (codegen lowered it to
            // a heap List rebuilt element-by-element — the fun_bench OOM). Keep the identifier so
            // codegen emits a single `@preset.*` constant global and indexes into it.
            if (v.IsPresettableAggregate)
            {
                return expr;
            }

            // Carry the Phase-4 ResolvedType from the identifier onto the inlined value.
            // This ensures operator-lowering and other subsequent passes see the correct type.
            TypeSymbol? resolvedType = id.ResolvedType ?? v.PresetValue.ResolvedType;
            if (v.PresetValue is LiteralExpression lit)
            {
                return TypedNumber(literal: lit, declared: v.Type, resolvedType: resolvedType);
            }

            return InlineComposedPreset(id: id, value: v.PresetValue, resolvedType: resolvedType, declared: v.Type);
        }

        return expr;
    }

    /// <summary>
    /// The value a preset's literal inlines as. A bare number takes the preset's declared type with it
    /// (<c>preset B128_PI: B128 = 3.14...</c> stays a B128 wherever it is used), since the number alone carries no
    /// type and would read as its default where it lands (<c>B128_PI.to[B64]()</c>).
    /// </summary>
    private static LiteralExpression TypedNumber(LiteralExpression literal, TypeSymbol? declared,
        TypeSymbol? resolvedType)
    {
        if (literal.LiteralType is Tokenizer.TokenType.UndecidedInteger or Tokenizer.TokenType.UndecidedDecimal &&
            UndecidedLiteralConformance.HasNumberToken(type: declared))
        {
            return UndecidedLiteralConformance.Conform(literal: literal with { ResolvedType = declared }) with
            {
                ResolvedType = resolvedType ?? declared
            };
        }

        return literal with { ResolvedType = resolvedType };
    }

    /// <summary>
    /// <see cref="IdentifierExpression"/> is a leaf to the shared <see cref="AstRewriter"/> spine, but the whole
    /// POINT of this pass: a preset identifier is inlined to its literal value (via <see cref="LowerIdentifier"/>).
    /// Every other node kind is left to the base's structural recursion.
    /// </summary>
    public override Expression VisitExpression(Expression expr)
    {
        if (expr is IdentifierExpression id)
        {
            return LowerIdentifier(expr: expr, id: id);
        }

        return base.VisitExpression(expr: expr);
    }

    /// <summary>
    /// A bare-identifier callee is a TYPE constructor or a routine name — never a preset value (presets
    /// are scalars/aggregates, not callable). Inlining it would rewrite <c>Foo(a: 1)</c> into
    /// <c>&lt;literal&gt;(a: 1)</c> when a preset happens to share the name <c>Foo</c> (e.g. a user
    /// <c>record B</c> colliding with stdlib <c>preset B</c>). So skip the callee when it is a bare
    /// identifier; still lower a member/other callee (e.g. <c>SOME_PRESET.bit_count()</c> whose Object
    /// may be a preset) and the argument list.
    /// </summary>
    protected override Expression VisitCall(CallExpression e)
    {
        Expression callee = e.Callee is IdentifierExpression
            ? e.Callee
            : VisitExpression(expr: e.Callee);
        List<Expression> args = RewriteList(items: e.Arguments, rewrite: VisitExpression);
        bool changed = !ReferenceEquals(objA: callee, objB: e.Callee) ||
                       !ReferenceEquals(objA: args, objB: e.Arguments);
        return changed
            ? e with { Callee = callee, Arguments = args }
            : e;
    }
}
