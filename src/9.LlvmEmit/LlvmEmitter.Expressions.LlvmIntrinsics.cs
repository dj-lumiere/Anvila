using System.Text;
using DebugUtils.Repr;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// LLVM intrinsic call emission — template-based IR generation for
/// routines annotated with <c>@llvm_ir("...")</c>.
/// </summary>
public partial class LlvmEmitter
{
    /// <summary>
    /// Emits a call to an LLVM intrinsic routine using its <c>@llvm_ir</c> template.
    /// Called from <see cref="EmitRoutineCall"/> and <see cref="EmitMemberRoutineCall"/> when
    /// <c>resolvedRoutine.LlvmIrTemplate != null</c>.
    /// </summary>
    private string EmitLlvmIntrinsicCall(StringBuilder sb, RoutineInfo routine, string? receiver,
        List<Expression> arguments, List<TypeExpression>? typeArguments,
        TypeSymbol? resolvedReturnType = null)
    {
        // Emit argument values.
        var argValues = new List<string>();
        if (receiver != null)
        {
            argValues.Add(item: receiver);
        }

        foreach (Expression arg in arguments)
        {
            argValues.Add(item: EmitExpression(sb: sb, expr: arg));
        }

        // The template's type arguments: the instantiated routine's own, else the ones the call writes.
        List<string> llvmTypeArgs = routine.TypeArguments is { Count: > 0 }
            ? InstantiatedIntrinsicTypeArguments(routine: routine)
            : (typeArguments ?? []).Select(selector: ResolveTypeExpressionToLlvm)
                                   .ToList();

        string mold = routine.LlvmIrTemplate!;
        return EmitFromTemplate(sb: sb,
            mold: mold,
            memberRoutine: routine,
            llvmTypeArgs: llvmTypeArgs,
            args: argValues);
    }

    /// <summary>The LLVM spelling of an instantiated intrinsic's type arguments: a const-generic parameter is its
    /// number, a type parameter bound to a constant is the constant's underlying type.</summary>
    private List<string> InstantiatedIntrinsicTypeArguments(RoutineInfo routine)
    {
        List<string>? declaredParams = routine.GenericParameters ?? routine.GenericDefinition?.GenericParameters;
        return routine.TypeArguments!
                      .Select(selector: (ta, i) =>
                           ta is ConstGenericValueTypeSymbol constArg &&
                           !IsConstIntrinsicParam(routine: routine,
                               paramName: declaredParams is { } dp && i < dp.Count ? dp[index: i] : null)
                               ? GetLlvmType(type: ResolveConstGenericUnderlyingType(constVal: constArg))
                               : GetLlvmIntrinsicTypeArgument(type: ta))
                      .ToList();
    }

    /// <summary>True when the intrinsic declares <paramref name="paramName"/> as a CONST-generic parameter
    /// (`needs U64 N`) — its argument renders as the number. Any other type parameter (`unsigned_lt[T]`)
    /// bound to a constant is a runtime value of the constant's underlying type. An unknown name keeps the
    /// old number rendering.</summary>
    private static bool IsConstIntrinsicParam(RoutineInfo routine, string? paramName)
    {
        if (paramName == null)
        {
            return true;
        }

        IEnumerable<GenericConstraintDeclaration> constraints =
            (routine.GenericConstraints ?? []).Concat(
                second: routine.GenericDefinition?.GenericConstraints ?? []);
        return constraints.Any(predicate: c =>
            c.ParameterName == paramName && c.ConstraintType == ConstraintKind.ConstGeneric);
    }

    /// <summary>A template's type argument: a value type (a None is the empty record <c>{}</c>, so
    /// <c>LLVM::load[None]</c> is a legal empty load), or a const-generic's number.</summary>
    private string GetLlvmIntrinsicTypeArgument(TypeSymbol type)
    {
        return type is ConstGenericValueTypeSymbol constValue
            ? constValue.Value.ToString()
            : GetValueLlvmType(type: type);
    }

    /// <summary>
    /// Emits LLVM IR from a template mold string with <c>{hole}</c> substitution.
    /// Supports multi-line templates (for overflow intrinsics, alloca/GEP patterns, etc.).
    /// </summary>
    /// LLVM rejects bitcast between integer and pointer types. The reinterpret_bits intrinsic
    /// template emits `bitcast {From} {value} to {To}`, which is invalid when one side is `ptr`
    /// and the other is `iN`. Rewrite those cases to use `inttoptr` / `ptrtoint`.
    /// <summary>
    /// Detects `%result = bitcast %Record.Foo %val to ptr` (struct -> ptr) which LLVM
    /// rejects, and returns the parts needed to rewrite as `alloca + store` so the result
    /// name aliases a fresh stack slot of the same kind (ptr). Returns null for any line
    /// that isn't a struct -> ptr bitcast.
    /// </summary>
    private string EmitFromTemplate(StringBuilder sb, string mold, RoutineInfo memberRoutine,
        List<string> llvmTypeArgs, List<string> args)
    {
        string[] lines =
            mold.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);
        string? lastResult = null;
        string? prevResult = null;
        string? firstResult = null;
        // `{$name}`: a temporary of the template's own, the same one wherever the template names it.
        var named = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            line = System.Text.RegularExpressions.Regex.Replace(input: line,
                pattern: @"\{\$(\w+)\}",
                evaluator: m =>
                {
                    string key = m.Groups[groupnum: 1].Value;
                    if (!named.TryGetValue(key: key, value: out string? temp))
                    {
                        temp = NextTemp();
                        named[key: key] = temp;
                    }

                    return temp;
                });
            line = SubstituteTypeStores(line: line, routine: memberRoutine);

            string currentResult = NextTemp();
            bool hasResult = line.Contains(value: "{result}");

            string substituted = SubstituteTemplateLine(line: line,
                memberRoutine: memberRoutine,
                llvmTypeArgs: llvmTypeArgs,
                args: args,
                currentResult: currentResult,
                prevResult: prevResult,
                firstResult: firstResult);

            EmitLine(sb: sb, line: $"  {substituted}");

            if (hasResult)
            {
                firstResult ??= currentResult;
                prevResult = currentResult;
                lastResult = currentResult;
            }
        }

        // Overflow intrinsics return anonymous struct types like { i128, i1 }.
        // If the memberRoutine DECLARES a tuple return, coerce via extractvalue/insertvalue so the caller receives
        // the named LLVM type (%"Record.Tuple[...]"). A routine returning a type parameter that an instance made a
        // tuple (`Hijacked[T].peek()` with `T = (S64, Text)`) already yields the named type: coercing it again would
        // read a named value as an anonymous one.
        if (lastResult != null && memberRoutine.ReturnType is TupleTypeSymbol tupleReturn &&
            (memberRoutine.GenericDefinition?.ReturnType ?? memberRoutine.ReturnType) is TupleTypeSymbol)
        {
            return CoerceAnonStructToNamedTuple(sb: sb,
                tupleReturn: tupleReturn,
                lastResult: lastResult);
        }

        return lastResult ?? (args.Count > 0
            ? args[index: 0]
            : "undef");
    }

    /// <summary>The type stores made so far, by type.</summary>
    private readonly Dictionary<string, string> _typeStores = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Replaces each <c>{store T}</c> of a template line with the address of the zeroed 32-byte global that belongs to
    /// the type the instantiated routine binds <c>T</c> to: one per type in the program, the same one in every module
    /// (an entity type's slot store, Link.rvr).
    /// </summary>
    private string SubstituteTypeStores(string line, RoutineInfo routine)
    {
        if (!line.Contains(value: "{store "))
        {
            return line;
        }

        List<string>? parameters = routine.GenericParameters ?? routine.GenericDefinition?.GenericParameters;
        List<TypeSymbol>? arguments = routine.TypeArguments;
        if (parameters == null || arguments == null)
        {
            throw new InvalidOperationException(
                message: $"The template of '{routine.Name}' names a type store, but the routine is not instantiated, in " +
                         $"[{_currentRoutineDiagName}].");
        }

        for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
        {
            string hole = $"{{store {parameters[index: i]}}}";
            if (!line.Contains(value: hole))
            {
                continue;
            }

            string typeName = arguments[index: i].FullName;
            if (!_typeStores.TryGetValue(key: typeName, value: out string? global))
            {
                global = $"@\"rv.store.{typeName.Replace(oldValue: "\"", newValue: "")}\"";
                EmitLine(sb: _globalDeclarations, line: $"{global} = linkonce_odr global [4 x i64] zeroinitializer, align 16");
                _typeStores[key: typeName] = global;
            }

            line = line.Replace(oldValue: hole, newValue: global);
        }

        return line;
    }

    /// <summary>
    /// Performs all <c>{hole}</c> substitutions on one template line: the <c>{result}</c>/<c>{prev}</c>/
    /// <c>{first}</c> temporaries, named generic-parameter types (and their <c>{sizeof T}</c> byte
    /// widths), positional <c>{paramName}</c> argument values, arithmetic const-generic holes, and the
    /// int↔ptr bitcast fixup.
    /// </summary>
    private string SubstituteTemplateLine(string line, RoutineInfo memberRoutine,
        List<string> llvmTypeArgs, List<string> args, string currentResult,
        string? prevResult, string? firstResult)
    {
        string substituted = line;
        substituted = substituted.Replace(oldValue: "{result}", newValue: currentResult);

        if (prevResult != null)
        {
            substituted = substituted.Replace(oldValue: "{prev}", newValue: prevResult);
        }

        if (firstResult != null)
        {
            substituted = substituted.Replace(oldValue: "{first}", newValue: firstResult);
        }

        // {T}, {From}, {To}, etc. — named generic parameters -> LLVM types
        List<string>? genericParameters = memberRoutine.GenericParameters ??
                                          memberRoutine.GenericDefinition?.GenericParameters;
        if (genericParameters != null)
        {
            for (int i = 0; i < genericParameters.Count && i < llvmTypeArgs.Count; i++)
            {
                string paramName = genericParameters[index: i];
                substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                    newValue: llvmTypeArgs[index: i]);

                string sizeofPattern = $"{{sizeof {paramName}}}";
                if (substituted.Contains(value: sizeofPattern))
                {
                    substituted = substituted.Replace(oldValue: sizeofPattern,
                        newValue: (GetTypeBitWidth(llvmType: llvmTypeArgs[index: i]) / 8)
                       .ToString());
                }
            }
        }

        // {paramName} -> emitted arg value (positional by parameter list order)
        for (int i = 0; i < memberRoutine.Parameters.Count && i < args.Count; i++)
        {
            string paramName = memberRoutine.Parameters[index: i].Name;
            substituted = substituted.Replace(oldValue: $"{{{paramName}}}",
                newValue: args[index: i]);
        }

        return substituted;
    }

    /// <summary>
    /// Coerces an anonymous-struct intrinsic result (e.g. overflow intrinsics returning
    /// <c>{ i128, i1 }</c>) into the named tuple LLVM type via per-element extractvalue/insertvalue,
    /// Bool-zext'ing each element to its i8 storage form. Returns the built named-tuple SSA value.
    /// </summary>
    private string CoerceAnonStructToNamedTuple(StringBuilder sb, TupleTypeSymbol tupleReturn,
        string lastResult)
    {
        string namedType = GetLlvmType(type: tupleReturn);
        string anonType =
            $"{{ {string.Join(separator: ", ", values: tupleReturn.ElementTypes.Select(selector: GetLlvmType))} }}";
        string tupleVal = "undef";
        for (int i = 0; i < tupleReturn.ElementTypes.Count; i++)
        {
            string elem = NextTemp();
            EmitLine(sb: sb, line: $"  {elem} = extractvalue {anonType} {lastResult}, {i}");
            // The named tuple stores a Bool element as i8 — zext the i1 from the anon result.
            TypeSymbol elemType = tupleReturn.ElementTypes[index: i];
            elem = CoerceBoolToStorage(sb: sb, value: elem, fieldType: elemType);
            string ins = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {ins} = insertvalue {namedType} {tupleVal}, {GetFieldStorageLlvmType(type: elemType)} {elem}, {i}");
            tupleVal = ins;
        }

        return tupleVal;
    }

    /// <summary>
    /// The LLVM type of a stamped <see cref="TypeExpression"/>.
    /// </summary>
    private string ResolveTypeExpressionToLlvm(TypeExpression typeExpr)
    {
        return typeExpr.ResolvedType is { } resolvedType and not ErrorTypeSymbol
            ? GetLlvmType(type: resolvedType)
            : throw new InvalidOperationException(
                message: $"The type '{typeExpr.Name}' at {typeExpr.Location} reached the LLVM emitter unresolved in " +
                         $"[{_currentRoutineDiagName}].");
    }

    /// <summary>
    /// A <see cref="GenericMemberRoutineCallExpression"/> is lowered before any backend (GenericCallLoweringPass).
    /// </summary>
    private string EmitGmceFallback(StringBuilder sb, GenericMemberRoutineCallExpression gmc)
    {
        throw new InvalidOperationException(
            message: $"A generic member routine call '{gmc.MemberRoutineName}' at {gmc.Location} reached the LLVM " +
                     $"emitter unlowered in [{_currentRoutineDiagName}].");
    }
}
