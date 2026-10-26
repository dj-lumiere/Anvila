using Builder.Diagnostics;
using Builder.Verification.Enums;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private static bool IsInlineOnlyTokenType(TypeSymbol type)
    {
        string baseName = type.BareName;
        return Wrappers.InlineOnlyTokenTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the token kind for display in error messages.
    /// </summary>
    private static string GetTokenKindDescription(TypeSymbol type)
    {
        string baseName = type.BareName;
        return baseName switch
        {
            Declaration.RuntimeContract.Viewing => "read-only token (Viewing)",
            Declaration.RuntimeContract.Modifying => "write token (Modifying)",
            _ => "token"
        };
    }

    /// <summary>
    /// Validates a token return. A routine may return a single-thread token (<c>Viewing</c>/<c>Modifying</c>):
    /// the language's token-lifetime check (RazorForge) checks where it comes from. A lock token and a block result
    /// may not.
    /// </summary>
    private void ValidateNotTokenReturnType(TypeSymbol type, SourceLocation location,
        Expression? returned = null)
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        // Exempt the canonical token constructors, which legitimately return a token (e.g.
        // `T.view() -> Viewing[T]`, `T.modify() -> Modifying[T]`). These live in the stdlib
        // wrapper modules, so scope the exemption to stdlib files — a USER routine declaring a
        // token return type is escaping a scope-bound token and must be rejected.
        if (IsStdlibFile(filePath: _currentFilePath) && _currentRoutine?.ReturnType != null &&
            IsInlineOnlyTokenType(type: _currentRoutine.ReturnType))
        {
            return;
        }

        if (!IsInlineOnlyTokenType(type: type))
        {
            return;
        }

        // A single-thread token can be returned: RazorForge's TokenLifetimeChecker checks that every return takes it from
        // `me` or from a token parameter, and gives the caller's token every argument it may come from as a source.
        if (returned != null &&
            type.BareName is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.TokenReturnNotAllowed,
            message:
            $"Cannot return {GetTokenKindDescription(type: type)} from here. A token a 'viewing' or 'modifying' block " +
            "opened lasts as long as the block, and a block result cannot be a token.",
            location: location);
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a member variable type.
    /// </summary>
    private void ValidateNotTokenMemberVariableType(TypeSymbol type, string memberVariableName,
        SourceLocation location)
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                message:
                $"Cannot store {GetTokenKindDescription(type: type)} in member variable '{memberVariableName}'. A token lives in a routine's locals and is never stored in a field.",
                location: location);
            return;
        }

        ValidateNoTokenTypeArgument(type: type, what: $"member variable '{memberVariableName}'", location: location);
    }

    /// <summary>
    /// The first token one of <paramref name="type"/>'s type arguments is or holds, at any depth
    /// (<c>Viewing[Cell]</c> in <c>List[Viewing[Cell]]</c>, in <c>(Viewing[Cell], S64)</c>, in
    /// <c>Modifying[List[Viewing[Cell]]]</c>), or null. A routine type is skipped: its parameter and result types are
    /// what a call takes and gives, and a routine value keeps none of them.
    /// </summary>
    private static TypeSymbol? TokenInTypeArguments(TypeSymbol type)
    {
        return TokenInTypeArguments(type: type, seen: []);
    }

    /// <summary>The walk behind <see cref="TokenInTypeArguments(TypeSymbol)"/>. The members of a variant written in
    /// place (<c>Viewing[Cell] | S64</c>) count as its type arguments. <paramref name="seen"/> stops a variant that
    /// holds itself through a container (<c>List[SerialValue]</c> in <c>SerialValue</c>).</summary>
    private static TypeSymbol? TokenInTypeArguments(TypeSymbol type, HashSet<TypeSymbol> seen)
    {
        if (type is RoutineTypeSymbol || !seen.Add(item: type))
        {
            return null;
        }

        IEnumerable<TypeSymbol> parts = type.TypeArguments ?? [];
        if (type is VariantTypeSymbol variant)
        {
            parts = parts.Concat(second: variant.Members.Where(predicate: m => m.Type != null)
                                                .Select(selector: m => m.Type!));
        }

        foreach (TypeSymbol argument in parts)
        {
            if (argument is RoutineTypeSymbol)
            {
                continue;
            }

            if (IsInlineOnlyTokenType(type: argument))
            {
                return argument;
            }

            if (TokenInTypeArguments(type: argument, seen: seen) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="type"/> is a recovery carrier of a token (<c>Maybe[Viewing[T]]</c>, and the
    /// <c>Check</c> / <c>Lookup</c> that <c>grab</c> / <c>lookup</c> give on a routine returning a token): what an
    /// iterator's <c>emit</c> gives under <c>try</c>, unpacked with <c>when</c> where it is made.</summary>
    private static bool IsTokenCarrier(TypeSymbol type)
    {
        return type.BareName is "Maybe" or "Check" or "Lookup" && type.TypeArguments is [{ } carried] &&
               (IsInlineOnlyTokenType(type: carried) || IsTokenTuple(type: carried));
    }

    /// <summary>Whether <paramref name="type"/> is a tuple whose elements are tokens or hold none (the
    /// <c>(U64, Modifying[T])</c> an <c>enumerate</c> step gives), with at least one token: an <c>each</c> loop takes it
    /// apart, and its tokens last as long as one pass of the loop.</summary>
    private static bool IsTokenTuple(TypeSymbol type)
    {
        return type is TupleTypeSymbol tuple && tuple.ElementTypes.Any(predicate: IsInlineOnlyTokenType) &&
               tuple.ElementTypes.All(predicate: element => TokenInTypeArguments(type: element) == null);
    }

    /// <summary>Whether <paramref name="expression"/> takes a type holding a token from one of its parts (the receiver
    /// or an argument already has it, so its declaration reported it), or is a call the builder wrote (an
    /// <c>each</c> loop's <c>iter()</c>, a block's enter): only a value the program makes here reports.</summary>
    private static bool TakesTokenTypeFromPart(Expression expression)
    {
        static bool Carries(Expression? part)
        {
            Expression? value = part is NamedArgumentExpression named ? named.Value : part;
            return value?.ResolvedType is { } type && (TokenInTypeArguments(type: type) != null || IsTokenCarrier(type: type));
        }

        return expression switch
        {
            CallExpression { IsSynthesizedLowering: true } => true,
            CallExpression call => call.Callee is MemberExpression { Object: var receiver } && Carries(part: receiver) ||
                                   call.Arguments.Any(predicate: Carries),
            GenericMemberRoutineCallExpression generic => Carries(part: generic.Object) ||
                                                          generic.Arguments.Any(predicate: Carries),
            _ => false
        };
    }

    /// <summary>The source locations a token-in-a-type-argument error was reported at, so a declaration and the
    /// expression that gives it its type report once.</summary>
    private readonly HashSet<(string File, int Line)> _tokenTypeArgumentReported = [];

    /// <summary>
    /// Rejects a token as a type argument (RF-S601): a token holds the address of the place an entity or value is in,
    /// and a container, a <c>Maybe</c>, a tuple, a variant or a generic type holding one could keep it after that
    /// place is gone (the block that held the entity ended, or the entity was torn down). A token lives in a variable
    /// of its own. <paramref name="carrierAllowed"/> lets a routine's result be <c>Maybe</c> of a token (an
    /// iterator's <c>emit</c> under <c>try</c>), which the caller unpacks with <c>when</c> where it gets it.
    /// </summary>
    private void ValidateNoTokenTypeArgument(TypeSymbol type, string what, SourceLocation location,
        bool carrierAllowed = false)
    {
        if (!_registry.Rules.ChecksAccessTokens || _isInCompilerGeneratedBody)
        {
            return;
        }

        TypeSymbol? token;
        if (IsTokenCarrier(type: type))
        {
            TypeSymbol carried = type.TypeArguments![index: 0];
            token = IsTokenTuple(type: carried) ? null : TokenInTypeArguments(type: carried);
            if (token == null && !carrierAllowed)
            {
                if (!_tokenTypeArgumentReported.Add(item: (location.FileName, location.Line)))
                {
                    return;
                }

                ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                    message:
                    $"You keep `{type.ShortTypeName}` in {what}, but it carries a token, and a token can't be kept inside " +
                    $"another value. Unpack it with `when` where you get it (`when try xs.view_at(index: i)` " +
                    "with `is None =>` and `else as v =>`), and use the token inside that branch.",
                    location: location);
                return;
            }
        }
        else
        {
            token = TokenInTypeArguments(type: type);
        }

        if (token == null || !_tokenTypeArgumentReported.Add(item: (location.FileName, location.Line)))
        {
            return;
        }

        string target = token.TypeArguments is [{ } viewed] ? viewed.ShortTypeName : "the entity";
        ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
            message:
            $"You use `{type.ShortTypeName}` for {what}, but `{token.ShortTypeName}` in it is a token, and a token can't be a type " +
            $"argument: the token points at the place `{target}` is in, and what holds the token could still use it " +
            $"after that place is gone. Keep each token in a variable of its own, or keep the `{target}` itself in " +
            $"the container (`List[{target}]`), or under a manager (`Retained[{target}]`).",
            location: location);
    }

    /// <summary>
    /// Rejects a generic routine whose type parameter a call makes a token (RF-S601): <c>same(x: l.view())</c> makes
    /// <c>T</c> a <c>Viewing[Cell]</c>, and the routine's body is free to keep a <c>T</c> (in a list, a result, a
    /// field of a value it builds), so the token could outlive the place it points at. Inferred and written type
    /// arguments alike. A builder-written body is left alone.
    /// </summary>
    private void ValidateNoTokenRoutineTypeArgument(RoutineInfo routine, List<TypeSymbol> typeArgs,
        SourceLocation location)
    {
        if (!_registry.Rules.ChecksAccessTokens || _isInCompilerGeneratedBody ||
            routine.GenericParameters is not { Count: > 0 } genParams)
        {
            return;
        }

        for (int i = 0; i < genParams.Count && i < typeArgs.Count; i++)
        {
            TypeSymbol arg = typeArgs[index: i];
            TypeSymbol? token = IsInlineOnlyTokenType(type: arg) ? arg : TokenInTypeArguments(type: arg);
            if (token == null || !_tokenTypeArgumentReported.Add(item: (location.FileName, location.Line)))
            {
                continue;
            }

            string target = token.TypeArguments is [{ } viewed] ? viewed.ShortTypeName : "the entity";
            string parameter = genParams[index: i];
            ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                message:
                $"This call makes '{parameter}' of '{routine.Name}' the type `{arg.ShortTypeName}`, and `{token.ShortTypeName}` " +
                $"is a token. A token can't be a type argument: '{routine.Name}' may keep a '{parameter}' (in a list, in " +
                $"its result), and the token would still point at the `{target}` after the place it is in is gone. Hand " +
                $"over what you need from it (a member variable read like `v.n`), or give '{routine.Name}' a " +
                $"`Viewing[{parameter}]` / `Modifying[{parameter}]` parameter and pass the `{target}` itself.",
                location: location);
            return;
        }
    }

    /// <summary>Whether <paramref name="value"/> names a token a <c>using</c> over a <c>Guarded</c> opener bound (a
    /// <c>Viewing</c> / <c>Modifying</c> backed by the lock), and if so whether it writes. Judged by where the token
    /// came from: its type is the same as any other token's.</summary>
    private bool IsLockBackedToken(Expression value, out bool writes)
    {
        writes = false;
        return value is IdentifierExpression named &&
               _registry.LookupVariable(name: named.Name) is { } variable &&
               _lockBackedTokens.TryGetValue(key: variable, value: out writes);
    }

    /// <summary>
    /// Validates that a lock-backed write token (bound by <c>modifying g as m</c>) is not passed twice in one call:
    /// the lock admits one writer, and the two parameters would be one entity changed through two names.
    /// </summary>
    private void ValidateExclusiveTokenUniqueness(List<Expression> arguments,
        SourceLocation location)
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        var seenExclusiveTokens = new HashSet<string>();
        foreach (Expression arg in arguments)
        {
            Expression value = arg is NamedArgumentExpression namedArg
                ? namedArg.Value
                : arg;
            if (!IsLockBackedToken(value: value, writes: out bool writes) || !writes)
            {
                continue;
            }

            string name = ((IdentifierExpression)value).Name;
            if (!seenExclusiveTokens.Add(item: name))
            {
                _lockTokenDuplicateCalls.Add(item: arguments);
                ReportError(code: SemanticDiagnosticCode.ExclusiveTokenDuplicate,
                    message:
                    $"You pass '{name}' twice in one call, and '{name}' is the write token a 'modifying' block opened on a Guarded, which " +
                    "admits one writer at a time: the two parameters would change one entity through two names. Pass " +
                    $"'{name}' once, or open two different Guarded handles together with 'modifying a, b'.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Checks the arguments a <c>to[Work]</c> conversion computes now against the rule for crossing into concurrently
    /// running work. Each argument must be one of:
    /// <list type="bullet">
    /// <item><c>steal</c>-moved: exclusive transfer, the caller loses access (a moved bare entity leaves exactly
    /// one live handle);</item>
    /// <item>trivially copyable: passed BY VALUE as an independent copy;</item>
    /// <item>a thread-shareable wrapper (<c>Unbroken</c>/<c>Guarded</c>/<c>Witnessed</c>), which carries its own
    /// synchronization (an atomic refcount);</item>
    /// <item>a <c>Roamed</c> handle, which is promoted before the Work is built.</item>
    /// </list>
    /// Anything else (a bare entity passed by copy, a record transitively owning a single-threaded wrapper
    /// such as <c>Retained</c>/<c>Tracked</c>/<c>Viewing</c>/<c>Modifying</c>) would alias unsynchronized state
    /// across work running in parallel, so it is rejected (RF-S632). Synchronization must be MARKED, never
    /// implied.
    /// </summary>
    /// <returns>The first argument that only one thread may use (a token, a <c>Retained</c>), described for a message,
    /// or null: a Work holding one is started with <c>start_here()</c> only, which the checks after analysis
    /// enforce (<see cref="WorkOneThreadChecker"/>).</returns>
    private string? ValidateWorkArguments(IReadOnlyList<(Expression Value, TypeSymbol Type)> arguments)
    {
        if (!ChecksOwnershipHere)
        {
            return null;
        }

        string? oneThread = null;
        foreach ((Expression value, TypeSymbol type) in arguments)
        {
            oneThread ??= ValidateWorkArgument(value: value, type: type);
        }

        return oneThread;
    }

    /// <summary>The wrappers only one thread may use that a Work started on its own thread (<c>start_here()</c>) may
    /// still hold: tokens and the single-thread reference-counted handles.</summary>
    private static readonly HashSet<string> OneThreadWrappers = new(comparer: StringComparer.Ordinal)
    {
        Declaration.RuntimeContract.Viewing,
        Declaration.RuntimeContract.Modifying,
        Declaration.RuntimeContract.Retained,
        Declaration.RuntimeContract.Tracked
    };

    /// <summary>Checks one argument of a <c>to[Work]</c> conversion (see <see cref="ValidateWorkArguments"/>).</summary>
    private string? ValidateWorkArgument(Expression value, TypeSymbol type)
    {
        if (type is ErrorTypeSymbol || Wrappers.IsThreadShareable(type: type))
        {
            return null;
        }

        // `g.view()` / `g.modify()` holds the access for the statement that passes it, and the job runs past it.
        if (Declaration.GuardAccess.LockingReceiver(expression: value, isWrite: out _) != null)
        {
            ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                message: "You pass a Guarded's access to a Work, but the access is held only for this statement, and " +
                         "the work runs on after it. Hand the Work the Guarded itself, and open it inside the " +
                         "work.",
                location: value.Location);
            return null;
        }

        // A token a `viewing` / `modifying` block opened on a Guarded lasts as long as the block, and the job can run
        // past it: hand the job the Guarded, and let it open its own access.
        if (IsLockBackedToken(value: value, writes: out _))
        {
            string token = ((IdentifierExpression)value).Name;
            ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                message: $"You pass `{token}` to a Work, but it is the token of a Guarded that this block opened, and " +
                         "the access ends with the block while the work can run on. Hand the Work the Guarded itself, " +
                         "and open it inside the work.",
                location: value.Location);
            return null;
        }

        // A `Roamed` handle crosses the boundary by being PROMOTED, not rejected: passing it into a Work IS the
        // escape event, and the builder inserts `promote()` on the argument before the Work is built (LOCAL ->
        // ESCAPED: atomic refcount + armed reentrant lock). So the same object is thread-safe by the time the work
        // touches it.
        if (type.BareName == Declaration.RuntimeContract.Roamed)
        {
            return null;
        }

        // A bare entity is a heap handle: passing it by copy copies the pointer, so the same object would be
        // aliased across work running in parallel. `steal` credits ONLY a bare entity: it is single-owner, so a
        // move leaves exactly one live handle. It does NOT credit a type that (transitively) owns a
        // single-threaded RC wrapper: moving one `Retained`/`Tracked` handle does not prove no siblings exist.
        // A value made right here (`Rx(n: 5)`, a call that hands its result over) has no other holder either.
        // What a value of the type reaches, at any depth (an entity's member variables, a list's elements, a channel
        // end's items), and what a lambda captured: a single-thread handle in any of them is the same handle in the
        // caller and in the work.
        string what = value is IdentifierExpression named
            ? $"`{named.Name}` ({type.ShortTypeName})"
            : $"this {type.ShortTypeName} argument";
        if (type is RoutineTypeSymbol)
        {
            if (OneThreadCapture(value: value) is { } captured)
            {
                return $"{what}, a lambda that captured {captured}";
            }

            // A routine value this routine was handed: its captures show only where it is made, so the parameter is
            // noted, and the calls that hand it a lambda are checked after analysis (CheckWorkBoundCallables).
            if (value is IdentifierExpression handed && _currentRoutine is { } current &&
                _registry.LookupVariable(name: handed.Name) is { IsParameter: true })
            {
                _workBoundCallables.Add(item: (current, handed.Name));
            }
            // A routine value read from where a lambda may have been put earlier (a member variable, a collection
            // element, a call's result, a variable given such a value) can be any lambda of its type stored before:
            // the lambdas of that type that captured a one-thread part are checked after analysis.
            else if (value is not LambdaExpression &&
                     !(value is IdentifierExpression known && _registry.LookupVariable(name: known.Name) is { } held &&
                       _lambdaVariables.Contains(item: held)))
            {
                _workBoundStoredRoutineTypes.TryAdd(key: RoutineShape(type: (RoutineTypeSymbol)type),
                    value: value.Location);
            }

            return null;
        }

        bool isEntity = type is EntityTypeSymbol;
        (string Wrapper, string Path)? offender = Wrappers.FindOneThreadPart(type: type);
        if (isEntity && value is StealExpression or CreatorExpression or CallExpression or GenericMemberRoutineCallExpression)
        {
            return offender is { Wrapper: var held, Path: var heldAt }
                ? $"{what}, which holds {Wrappers.DescribeOneThreadPart(wrapper: held, at: heldAt)}"
                : null;
        }

        if (!isEntity && offender == null)
        {
            return null;
        }

        // A token or a single-thread handle is fine on the thread that made the Work: it is noted, and the verb that
        // starts the Work decides.
        if (!isEntity && offender is { Wrapper: var wrapper, Path: var at } && OneThreadWrappers.Contains(item: wrapper))
        {
            return at == "<value>"
                ? what
                : $"{what}, which holds {Wrappers.DescribeOneThreadPart(wrapper: wrapper, at: at)}";
        }

        string reason = isEntity
            ? "a bare entity would be the same object in the caller and in the running work"
            : $"it holds `{offender!.Value.Wrapper}` at `{offender.Value.Path}`, which only the work that made it may use";
        string fix = isEntity
            ? "Write `steal` before it to hand it over to the Work, share it with `Guarded`/`Witnessed`/`Unbroken`, or " +
              "pass a copyable value"
            : "Share it with `Guarded`/`Witnessed`/`Unbroken`, or pass a copyable value";
        ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
            message: $"You pass {what} to a Work, but {reason}. {fix}.",
            location: value.Location);
        return null;
    }

    /// <summary>The lambda values whose captures hold a part only one thread may use, keyed by the variable that keeps
    /// the lambda (`var f = () given (r) => r.n` with `r` a <c>Retained</c>), described for a message.</summary>
    private readonly Dictionary<VariableInfo, string> _oneThreadLambdaVariables = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>What a routine value <paramref name="value"/> captured that only one thread may use: a lambda written
    /// here (its <c>given</c> names), or a variable a lambda like that was bound to. Null otherwise.</summary>
    private string? OneThreadCapture(Expression value)
    {
        switch (value)
        {
            case LambdaExpression { Captures: { } captures }:
                foreach (string name in captures)
                {
                    if (_registry.LookupVariable(name: name) is { } captured &&
                        Wrappers.FindOneThreadPart(type: captured.Type) is { Wrapper: var wrapper, Path: var at })
                    {
                        return at == "<value>"
                            ? $"`{name}` (a `{wrapper}`)"
                            : $"`{name}`, which holds {Wrappers.DescribeOneThreadPart(wrapper: wrapper, at: at)}";
                    }
                }

                return null;
            case IdentifierExpression id when _registry.LookupVariable(name: id.Name) is { } variable:
                return _oneThreadLambdaVariables.GetValueOrDefault(key: variable);
            default:
                return null;
        }
    }

    /// <summary>The routine-value parameters a routine hands to a <c>to[Work]</c> conversion, directly or through
    /// another routine's such parameter (see <see cref="CheckWorkBoundCallables"/>).</summary>
    private readonly HashSet<(RoutineInfo Routine, string Parameter)> _workBoundCallables = [];

    /// <summary>A routine-value parameter handed on as the argument of another routine's routine-value parameter.</summary>
    private readonly List<(RoutineInfo Caller, string Parameter, RoutineInfo Callee, string CalleeParameter)>
        _forwardedCallables = [];

    /// <summary>A lambda that captured a part only one thread may use, handed to a routine-value parameter.</summary>
    private readonly List<(RoutineInfo Callee, string Parameter, string Captured, SourceLocation Location)>
        _oneThreadCallableArguments = [];

    /// <summary>Notes what <see cref="CheckWorkBoundCallables"/> needs about a routine value handed to the
    /// parameter <paramref name="parameter"/> of <paramref name="callee"/>.</summary>
    private void NoteCallableArgument(RoutineInfo callee, string parameter, Expression argument)
    {
        if (!ChecksOwnershipHere)
        {
            return;
        }

        if (OneThreadCapture(value: argument) is { } captured)
        {
            _oneThreadCallableArguments.Add(item: (callee, parameter, captured, argument.Location));
        }
        else if (argument is IdentifierExpression handed && _currentRoutine is { } current &&
                 _registry.LookupVariable(name: handed.Name) is { IsParameter: true })
        {
            _forwardedCallables.Add(item: (current, handed.Name, callee, parameter));
        }
    }

    /// <summary>
    /// A lambda that captured a token or a single-thread handle (a <c>Retained</c>) can't be handed to a routine that
    /// hands that routine value to a Work: the work may run on another thread, and every call of the lambda counts the
    /// captured handle there without atomics (RF-S632). The routine values a routine hands to Work, directly or through
    /// another routine, are known once every body is analyzed.
    /// </summary>
    private void CheckWorkBoundCallables()
    {
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((RoutineInfo caller, string parameter, RoutineInfo callee, string calleeParameter) in
                     _forwardedCallables)
            {
                if (_workBoundCallables.Contains(item: (callee, calleeParameter)) &&
                    _workBoundCallables.Add(item: (caller, parameter)))
                {
                    grew = true;
                }
            }
        }

        foreach ((RoutineInfo callee, string parameter, string captured, SourceLocation location) in
                 _oneThreadCallableArguments)
        {
            if (_workBoundCallables.Contains(item: (callee, parameter)))
            {
                ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                    message: $"You hand '{callee.Name}' a lambda that captured {captured}, and '{callee.Name}' hands " +
                             $"its '{parameter}' to a Work, which may run it on another thread. Only the thread that " +
                             "made the lambda may use what it captured: capture a Guarded, an Unbroken or a copied " +
                             "value instead, or call the lambda on this thread.",
                    location: location);
            }
        }

        foreach ((string shape, (string captured, SourceLocation location)) in _oneThreadLambdas)
        {
            if (_workBoundStoredRoutineTypes.TryGetValue(key: shape, value: out SourceLocation? handedAt))
            {
                ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                    message: $"This lambda captured {captured}, and a routine value of its type ({shape}) kept " +
                             $"elsewhere (a member variable, a collection, a call's result) is handed to a Work at " +
                             $"line {handedAt.Line}, which may run it on another thread. The builder can't tell which " +
                             "lambda that value is, so it counts this one: capture a Guarded, an Unbroken or a copied " +
                             "value instead, or keep the lambdas a Work runs apart from this one's type.",
                    location: location);
            }
        }

        _workBoundCallables.Clear();
        _forwardedCallables.Clear();
        _oneThreadCallableArguments.Clear();
        _oneThreadLambdas.Clear();
        _workBoundStoredRoutineTypes.Clear();
        _lambdaVariables.Clear();
    }

    /// <summary>A routine type by its parameters and result, failable or not: a lambda that can't fail may be kept
    /// where a failable routine value goes.</summary>
    private static string RoutineShape(RoutineTypeSymbol type)
    {
        string parameters = string.Join(separator: ", ", values: type.ParameterTypes.Select(selector: p => p.Name));
        return $"Callable[({parameters}), {type.ReturnType?.Name ?? "None"}]";
    }

    /// <summary>Every lambda that captured a part only one thread may use, by its routine type, with what it captured
    /// and where it is written.</summary>
    private readonly Dictionary<string, (string Captured, SourceLocation Location)> _oneThreadLambdaLocations =
        new(comparer: StringComparer.Ordinal);

    /// <summary>The lambdas that captured a part only one thread may use, keyed by shape and location.</summary>
    private readonly List<(string Shape, (string Captured, SourceLocation Location) Lambda)> _oneThreadLambdas = [];

    /// <summary>The routine types of routine values handed to a Work from somewhere the builder does not follow (a
    /// member variable, a collection element, a call's result), with the first place.</summary>
    private readonly Dictionary<string, SourceLocation> _workBoundStoredRoutineTypes = new(comparer: StringComparer.Ordinal);

    /// <summary>The variables bound to a lambda written in place: what they hold is known.</summary>
    private readonly HashSet<VariableInfo> _lambdaVariables = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Notes a lambda that captured a part only one thread may use (see <see cref="CheckWorkBoundCallables"/>).</summary>
    private void NoteOneThreadLambda(LambdaExpression lambda, RoutineTypeSymbol type)
    {
        if (!ChecksOwnershipHere || OneThreadCapture(value: lambda) is not { } captured)
        {
            return;
        }

        string key = $"{lambda.Location.FileName}:{lambda.Location.Position}";
        if (_oneThreadLambdaLocations.TryAdd(key: key, value: (captured, lambda.Location)))
        {
            _oneThreadLambdas.Add(item: (RoutineShape(type: type), (captured, lambda.Location)));
        }
    }

    /// <summary>Notes a variable bound to a lambda that captured a part only one thread may use, so handing the
    /// variable to a job is checked like handing it the lambda.</summary>
    private void NoteOneThreadLambdaVariable(string name, Expression? initializer)
    {
        if (initializer is not LambdaExpression lambda || _registry.LookupVariable(name: name) is not { } variable)
        {
            return;
        }

        _lambdaVariables.Add(item: variable);
        if (OneThreadCapture(value: lambda) is { } captured)
        {
            _oneThreadLambdaVariables[key: variable] = captured;
        }
    }

    /// <summary>
    /// Validates that a type is not an inline-only token when used as a variant case payload.
    /// </summary>
    internal void ValidateNotTokenVariantPayload(TypeSymbol type, string caseName,
        SourceLocation location)
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        if (IsInlineOnlyTokenType(type: type))
        {
            ReportError(code: SemanticDiagnosticCode.TokenVariantPayloadNotAllowed,
                message:
                $"Cannot use {GetTokenKindDescription(type: type)} as payload for variant case '{caseName}'. A token lives in a routine's locals and is never stored in a variant.",
                location: location);
            return;
        }

        ValidateNoTokenTypeArgument(type: type, what: $"variant case '{caseName}'", location: location);
    }

    /// <summary>
    /// Gets the effective visibility for member variable write access.
    /// For posted member variables, write access is secret (only owner can write).
    /// </summary>
    /// <param name="memberVariable">The member variable to check.</param>
    /// <returns>The effective visibility for write access.</returns>
    private static VisibilityModifier GetEffectiveWriteVisibility(
        MemberVariableInfo memberVariable)
    {
        // Posted member variables have open read but secret write
        return memberVariable.Visibility == VisibilityModifier.Posted
            ? VisibilityModifier.Secret
            : memberVariable.Visibility;
    }

    /// <summary>
    /// Checks if access to a member variable is allowed from the current context.
    /// </summary>
    /// <param name="memberVariable">The member variable being accessed.</param>
    /// <param name="isWrite">Whether this is a write access (assignment).</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    private void ValidateMemberVariableAccess(MemberVariableInfo memberVariable, bool isWrite,
        SourceLocation accessLocation)
    {
        // A recovery carrier's layout (`Maybe.present`/`value`, `Check`/`Lookup` `type_id`/`payload`) is what the
        // builder builds and reads when it lowers `try`/`check`. Its own bodies reach those secret fields
        // by construction, whichever module they land in.
        if (_isInCompilerGeneratedBody && IsCarrierLayoutField(memberVariable: memberVariable))
        {
            return;
        }

        // Posted member variables: open read, module-only write
        if (isWrite && memberVariable.Visibility == VisibilityModifier.Posted &&
            !IsAccessingFromSameModule(memberModule: memberVariable.Owner?.Module))
        {
            string typeName = memberVariable.Owner?.Name ?? "type";
            ReportError(code: SemanticDiagnosticCode.PostedMemberAccess,
                message:
                $"Cannot write to posted member variable '{memberVariable.Name}' of '{typeName}' from outside its module.",
                location: accessLocation);
            return;
        }

        // For posted member variables, write access is restricted to secret (module only)
        VisibilityModifier visibility = isWrite
            ? GetEffectiveWriteVisibility(memberVariable: memberVariable)
            : memberVariable.Visibility;

        ValidateMemberAccess(visibility: visibility,
            memberKind: "member variable",
            memberName: memberVariable.Name,
            ownerType: memberVariable.Owner,
            accessLocation: accessLocation);
    }

    /// <summary>
    /// Checks if access to a routine is allowed from the current context.
    /// </summary>
    /// <param name="routine">The routine being accessed.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    /// <param name="isCompilerSynthesized">When true, suppresses the dangerous-outside-danger-block
    /// check for builder-injected calls (e.g. <c>local.destruct()</c> from teardown lowering)
    /// that are not user-written and must not be gated by a danger block.</param>
    private void ValidateRoutineAccess(RoutineInfo routine, SourceLocation accessLocation,
        bool isCompilerSynthesized = false)
    {
        // The copy step (`share`) is what `=` runs: the builder calls it at every copy, and the standard library
        // calls it where it reads a value out of raw memory. A program never names it.
        if (routine is { Kind: RoutineKind.MemberRoutine, Name: Declaration.RuntimeContract.Duplication.Share } &&
            routine.Parameters.Count == 0)
        {
            if (!isCompilerSynthesized && !_isInCompilerGeneratedBody && !IsStdlibFile(filePath: _currentFilePath))
            {
                ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                    message: "'share' is the step '=' runs to copy a value, so a program does not call it. " +
                             "Write 'var b = a' to copy (a handle gets one more holder).",
                    location: accessLocation);
            }

            return;
        }

        // A builder-written body (a derive cloned onto the program's type) calls the library's own routines, and so
        // does a call the builder writes into a program (`xs.exchange(...)` for `steal xs[i] as old inject fresh`).
        if (_isInCompilerGeneratedBody || isCompilerSynthesized)
        {
            return;
        }

        // A routine reached through a token (`node.keys_get(i)` on a `Viewing[Node]`) belongs to the value's type.
        TypeSymbol? owner = routine.WrapperForwarderInnerMemberRoutine?.OwnerType ?? routine.OwnerType;
        if (owner != null && IsAccessToken(type: owner) && owner.TypeArguments is [{ } viewed, ..])
        {
            owner = viewed;
        }

        ValidateMemberAccess(visibility: routine.Visibility,
            memberKind: routine.Kind switch
            {
                RoutineKind.Creator => "creator",
                RoutineKind.MemberRoutine => "member routine",
                _ => "routine"
            },
            memberName: routine.Name,
            ownerType: owner,
            accessLocation: accessLocation,
            memberModule: owner?.Module ?? routine.Module);

        // Dangerous routines can only be called inside danger blocks — EXCEPT a builder-synthesized
        // call (e.g. ScopeTeardownLoweringPass's injected `local.destruct()`), which is the builder's
        // own teardown, not user code invoking a dangerous routine. Under the demand pipeline teardown is
        // injected before the stdlib body is SA'd on reach, so without this exemption the re-analysis
        // would false-fire RF-S609 on every scope-exit destroy (the eager pipeline SA'd stdlib first).
        if (routine.IsDangerous && !InDangerBlock && !isCompilerSynthesized)
        {
            ReportError(code: SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
                message:
                $"Dangerous routine '{routine.Name}' can only be called inside a 'danger' block.",
                location: accessLocation);
        }
    }

    /// <summary>The RF-S420 message for a token copied from a name or a member variable (<c>w = m</c>): a token is
    /// taken from its entity where it is used, never copied from another token, which may belong to a block that ends
    /// before the copy does.</summary>
    private static string TokenCopyMessage(Expression copied, TypeSymbol type)
    {
        string name = copied is IdentifierExpression id ? $"'{id.Name}'" : "this token";
        string target = Wrappers.GetWrapperInnerType(wrapperType: type)?.ShortTypeName ?? "the entity";
        string verb = type.BareName == Declaration.RuntimeContract.Viewing ? "view" : "modify";
        return $"You copy {name}, a `{type.ShortTypeName}` token, into another place, but a token is not copied: the " +
               $"copy could outlive the block or the entity {name} belongs to. Take a token from the `{target}` " +
               $"itself where you need it (`x.{verb}()`), or keep using {name} where it is.";
    }

    /// <summary>
    /// RF-S609 for a dangerous routine the builder calls for a piece of syntax that shows no call: an operator
    /// (<c>a + b</c> on a dangerous <c>add</c>), an f-text insertion (<c>f"{p}"</c> on a dangerous <c>represent</c>).
    /// The syntax is the call, so it stands inside <c>danger</c> like the call would.
    /// </summary>
    private void ValidateDangerousImplicitCall(RoutineInfo? routine, string written, SourceLocation location)
    {
        if (routine is not { IsDangerous: true } || InDangerBlock || _isInCompilerGeneratedBody)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
            message:
            $"{written} calls '{routine.Name}', which is dangerous, so it can only be written inside a 'danger' block. " +
            "Put this line in 'danger', or make the routine safe to call anywhere (a plain routine with a 'danger' " +
            "block inside).",
            location: location);
    }

    /// <summary>
    /// A member-wise construction `T(field: value, ...)` sets the member variables it names, so one that gives a value
    /// to a `secret` or `posted` member variable is written only inside the type's own module. Outside, the routines its module
    /// opens build it, so a value whose parts must agree (a code and its payload) can't be put together wrong.
    /// </summary>
    private void ValidateMemberwiseConstructionAccess(TypeSymbol type, List<Expression> arguments,
        SourceLocation location)
    {
        if (_isInCompilerGeneratedBody || IsAccessingFromSameModule(memberModule: type.Module))
        {
            return;
        }

        List<MemberVariableInfo>? fields = type switch
        {
            EntityTypeSymbol e => e.MemberVariables,
            RecordTypeSymbol r => r.MemberVariables,
            _ => null
        };
        // A member variable left to its default gets the value its module wrote, so only the ones given a value count.
        var given = arguments.OfType<NamedArgumentExpression>()
                             .Select(selector: a => a.Name)
                             .ToHashSet();
        if (fields?.FirstOrDefault(predicate: f => given.Contains(item: f.Name) &&
                                                   f.Visibility is VisibilityModifier.Secret
                                                       or VisibilityModifier.Posted) is not { } closed)
        {
            return;
        }

        string bareName = type.BareName;
        // The routines a program can call to build one: its own creators (`Money(...)`) and the common routines that
        // return it. A routine the builder writes for itself (`from_serial`) is no way in.
        List<string> builders = _registry.GetMemberRoutinesForType(type: type)
                                         .Where(predicate: r => r.Visibility != VisibilityModifier.Secret &&
                                                                !r.IsSynthesized &&
                                                                r.Name != Instantiation.AutoWiredRegistrationPass.FromSerialRoutineName &&
                                                                (r.IsCreator && r.Parameters.Count > 0 ||
                                                                 r.IsCommon && !r.IsCreator &&
                                                                 r.ReturnType?.BareName == bareName))
                                         .Select(selector: r => r.IsCreator
                                              ? $"'{bareName}({string.Join(separator: ", ", values: r.Parameters.Select(selector: p => p.Name + ": ..."))})'"
                                              : $"'{bareName}.{r.Name}(...)'")
                                         .Distinct()
                                         .ToList();
        string fix = builders.Count > 0
            ? $"Build it with {string.Join(separator: " or ", values: builders)}."
            : $"Build it with a routine its module opens, or give '{bareName}' one.";
        string kind = closed.Visibility == VisibilityModifier.Secret ? "secret" : "posted";
        ReportError(code: closed.Visibility == VisibilityModifier.Secret
                ? SemanticDiagnosticCode.SecretMemberAccess
                : SemanticDiagnosticCode.PostedMemberAccess,
            message: $"You build '{bareName}' by giving its member variables values directly, but '{closed.Name}' " +
                     $"is {kind}, so only its own module may set it. {fix}",
            location: location);
    }

    /// <summary>Whether a member variable is one of the recovery carriers' layout fields.</summary>
    private static bool IsCarrierLayoutField(MemberVariableInfo memberVariable)
    {
        string? carrier = memberVariable.Owner?.BareName;
        return carrier is "Maybe" or "Check" &&
               memberVariable.Name is Declaration.RuntimeContract.Carrier.PresentField
                   or Declaration.RuntimeContract.Carrier.ValueField
                   or Declaration.RuntimeContract.Carrier.TypeIdField
                   or Declaration.RuntimeContract.Carrier.PayloadField;
    }

    /// <summary>
    /// Validates access to a member based on visibility rules.
    /// </summary>
    /// <param name="visibility">The visibility modifier of the member.</param>
    /// <param name="memberKind">The kind of member (member variable, memberRoutine, etc.) for error messages.</param>
    /// <param name="memberName">The name of the member.</param>
    /// <param name="ownerType">The type that owns this member, if any.</param>
    /// <param name="accessLocation">Source location of the access site.</param>
    /// <param name="memberModule">The member's module when it is not the owner type's (a free routine).</param>
    private void ValidateMemberAccess(VisibilityModifier visibility, string memberKind,
        string memberName, TypeSymbol? ownerType, SourceLocation accessLocation,
        string? memberModule = null)
    {
        // Owner secrecy CAPS member visibility: a `secret` (module-private) type's members are
        // module-private too, no matter their own modifier. The type name is already hidden cross-module,
        // but an importer can still obtain an instance by inference through a non-secret factory that
        // returns it — this closes that hole. No per-member `secret` annotation is required.
        if (ownerType is { Visibility: VisibilityModifier.Secret } &&
            !IsAccessingFromSameModule(memberModule: ownerType.Module))
        {
            ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                message:
                $"Cannot access {memberKind} '{memberName}' of secret (module-private) type '{ownerType.Name}' " +
                $"from outside its module.",
                location: accessLocation);
            return;
        }

        switch (visibility)
        {
            case VisibilityModifier.Secret:
                // Secret members are accessible within the same module
                // A free routine has no owner type, so its own module decides (it used to compare against
                // null and reject every call, even from the routine's own module).
                if (!IsAccessingFromSameModule(memberModule: memberModule ?? ownerType?.Module))
                {
                    ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                        message: ownerType != null
                            ? $"Cannot access secret {memberKind} '{memberName}' of '{ownerType.Name}' from outside its module."
                            : $"The {memberKind} '{memberName}' is secret to module '{memberModule}', so only that " +
                              "module and the modules under it can call it.",
                        location: accessLocation);
                }

                break;

            case VisibilityModifier.Posted:
            case VisibilityModifier.Open:
            case VisibilityModifier.External:
                // Open/Posted/External members are accessible from anywhere for reading
                break;
        }
    }

    /// <summary>
    /// Checks if the current access context reaches a <c>secret</c> member of <paramref name="memberModule"/>: code
    /// of that module or of a module under it (<c>Garden/Tools</c> reaches <c>Garden</c>'s secrets, not the other way
    /// around).
    /// </summary>
    private bool IsAccessingFromSameModule(string? memberModule)
    {
        string? currentModuleName = GetCurrentModuleName();

        // If both are in no module, they're in the same module
        if (string.IsNullOrEmpty(value: memberModule) &&
            string.IsNullOrEmpty(value: currentModuleName))
        {
            return true;
        }

        // If either is null/empty but not both, they're not in the same module
        if (string.IsNullOrEmpty(value: memberModule) ||
            string.IsNullOrEmpty(value: currentModuleName))
        {
            return false;
        }

        return Builder.Declaration.StandardModules.IsUnder(module: currentModuleName, parent: memberModule);
    }

    /// <summary>
    /// Validates write access to a member variable, checking setter visibility.
    /// </summary>
    /// <param name="objectType">The type of the object being accessed.</param>
    /// <param name="memberVariableName">The name of the member variable being written.</param>
    /// <param name="location">The source location of the write.</param>
    private void ValidateMemberVariableWriteAccess(TypeSymbol objectType,
        string memberVariableName, SourceLocation location)
    {
        if (TryUnwrapMarkerReceiver(type: objectType, innerType: out TypeSymbol targetType))
        {
            objectType = targetType;
        }

        MemberVariableInfo? memberVariable = objectType switch
        {
            RecordTypeSymbol record => record.LookupMemberVariable(
                memberVariableName: memberVariableName),
            EntityTypeSymbol entity => entity.LookupMemberVariable(
                memberVariableName: memberVariableName),
            _ => null
        };

        if (memberVariable != null)
        {
            ValidateMemberVariableAccess(memberVariable: memberVariable,
                isWrite: true,
                accessLocation: location);
        }
    }

    /// <summary>
    /// Checks whether a file path is inside the stdlib directory.
    /// Used to allow stdlib files to use reserved features (e.g., module System).
    /// </summary>
    /// <summary>True when <paramref name="filePath"/> names a file of the program being built: not one of the
    /// library's, and not the empty or placeholder name of a body the builder writes.</summary>
    internal bool IsProgramFile(string? filePath)
    {
        return !string.IsNullOrEmpty(value: filePath) && !filePath.StartsWith(value: '<') &&
               Frontends.Languages.HasSourceExtension(fileName: filePath) && !IsStdlibFile(filePath: filePath);
    }

    internal bool IsStdlibFile(string filePath)
    {
        return _registry.IsStandardLibraryFile(filePath: filePath);
    }

    /// <summary>Analyzes a call, then applies the inline-token source check (RF-S639).</summary>
    private TypeSymbol AnalyzeCallWithInlineTokens(CallExpression call, TypeSymbol? expectedType)
    {
        // Every pass over this call's arguments (overload resolution, then the per-parameter check) knows which
        // call they belong to, so a use that lost to a `steal` in the same call can say so.
        _callArgumentsInAnalysis.Push(item: call.Arguments);
        try
        {
            return AnalyzeCallExpression(call: call, expectedType: expectedType);
        }
        finally
        {
            _callArgumentsInAnalysis.Pop();
        }
    }

    /// <summary>
    /// RF-S639 for a token passed inline to one call (<c>f(m: a.modify(), x: steal a)</c>,
    /// <c>a.modify().absorb(x: steal a)</c>): the token lives for that call, so a <c>steal</c> of its source
    /// elsewhere in the same call is rejected. (A statement working on an entity element in place is
    /// checked after all bodies are analyzed, see <c>CheckShapeEffects</c>.) Runs after the call is
    /// analyzed, when argument types are known.
    /// </summary>
    /// <summary>Every expression inside <paramref name="expr"/>, itself included.</summary>
    private static List<Expression> CollectSubexpressions(Expression expr)
    {
        var seen = new List<Expression>();
        AstWalker.WalkExpressions(root: expr, visit: seen.Add);
        return seen;
    }
}
