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
            Declaration.RuntimeContract.Modifying => "exclusive write token (Modifying)",
            Declaration.RuntimeContract.Consulting => "shared read token (Consulting)",
            Declaration.RuntimeContract.Amending => "exclusive shared write token (Amending)",
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

        // A single-thread token can be returned: RazorForge's TokenLifetimeChecker checks that every return takes it from one
        // and the same parameter (`me` included), so the caller knows which object it points at.
        if (returned != null &&
            type.BareName is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.TokenReturnNotAllowed,
            message:
            $"Cannot return {GetTokenKindDescription(type: type)} from here. A lock token is opened with 'using', " +
            "and a block result cannot be a token.",
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
        }
    }

    /// <summary>
    /// Validates that exclusive tokens (Amending) are not passed multiple times in a single call.
    /// </summary>
    private void ValidateExclusiveTokenUniqueness(List<Expression> arguments,
        SourceLocation location)
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        // Track which exclusive token expressions we've seen
        var seenExclusiveTokens = new HashSet<string>();

        foreach (Expression arg in arguments)
        {
            // Get the expression's type
            if (arg.ResolvedType == null)
            {
                continue;
            }

            // Convert AST TypeSymbol back to get the type name
            string baseName = arg.ResolvedType.BareName;

            if (!Wrappers.ExclusiveTokenTypes.Contains(value: baseName))
            {
                continue;
            }

            // Get a string representation of the expression for uniqueness checking
            string? exprKey = GetExpressionKey(expression: arg);
            if (string.IsNullOrEmpty(value: exprKey))
            {
                continue;
            }

            if (!seenExclusiveTokens.Add(item: exprKey))
            {
                ReportError(code: SemanticDiagnosticCode.ExclusiveTokenDuplicate,
                    message:
                    $"Cannot pass the same {baseName} token '{exprKey}' multiple times in a single call. Exclusive tokens require unique access.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Checks the arguments an <c>agent</c> call computes now against the rule for crossing into concurrently
    /// running work. Each argument must be one of:
    /// <list type="bullet">
    /// <item><c>steal</c>-moved: exclusive transfer, the caller loses access (a moved bare entity leaves exactly
    /// one live handle);</item>
    /// <item>trivially copyable: passed BY VALUE as an independent copy;</item>
    /// <item>a thread-shareable wrapper (<c>Atomic</c>/<c>Guarded</c>/<c>Witnessed</c>/<c>Consulting</c>/
    /// <c>Amending</c>), which carries its own synchronization (atomic refcount or lock);</item>
    /// <item>a <c>Roamed</c> handle, which is promoted before the Agent is built.</item>
    /// </list>
    /// Anything else (a bare entity passed by copy, a record transitively owning a single-threaded wrapper
    /// such as <c>Retained</c>/<c>Tracked</c>/<c>Viewing</c>/<c>Modifying</c>) would alias unsynchronized state
    /// across work running in parallel, so it is rejected (RF-S632). Synchronization must be MARKED, never
    /// implied.
    /// </summary>
    /// <returns>The first argument that only one thread may use (a token, a <c>Retained</c>), described for a message,
    /// or null: an Agent holding one is started with <c>start_here()</c> only, which the checks after analysis
    /// enforce (<see cref="AgentOneThreadChecker"/>).</returns>
    private string? ValidateAgentArguments(IReadOnlyList<(Expression Value, TypeSymbol Type)> arguments)
    {
        if (!ChecksOwnershipHere)
        {
            return null;
        }

        string? oneThread = null;
        foreach ((Expression value, TypeSymbol type) in arguments)
        {
            oneThread ??= ValidateAgentArgument(value: value, type: type);
        }

        return oneThread;
    }

    /// <summary>The wrappers only one thread may use that an Agent started on its own thread (<c>start_here()</c>) may
    /// still hold: tokens and the single-thread reference-counted handles.</summary>
    private static readonly HashSet<string> OneThreadWrappers = new(comparer: StringComparer.Ordinal)
    {
        Declaration.RuntimeContract.Viewing,
        Declaration.RuntimeContract.Modifying,
        Declaration.RuntimeContract.Retained,
        Declaration.RuntimeContract.Tracked
    };

    /// <summary>Checks one argument of an <c>agent</c> call (see <see cref="ValidateAgentArguments"/>).</summary>
    private string? ValidateAgentArgument(Expression value, TypeSymbol type)
    {
        if (type is ErrorTypeSymbol || Wrappers.IsThreadShareable(type: type))
        {
            return null;
        }

        // A `Roamed` handle crosses the boundary by being PROMOTED, not rejected: passing it into an Agent IS the
        // escape event, and the builder inserts `promote()` on the argument before the Agent is built (LOCAL ->
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
        bool isEntity = type is EntityTypeSymbol;
        if (isEntity && value is StealExpression)
        {
            return null;
        }

        (string Wrapper, string Path)? offender = isEntity
            ? null
            : Wrappers.FindThreadUnsafeWrapper(type: type);
        if (!isEntity && offender == null)
        {
            return null;
        }

        string what = value is IdentifierExpression named
            ? $"`{named.Name}` ({type.Name})"
            : $"this {type.Name} argument";
        // A token or a single-thread handle is fine on the thread that made the Agent: it is noted, and the verb that
        // starts the Agent decides.
        if (offender is { Wrapper: var wrapper, Path: var at } && OneThreadWrappers.Contains(item: wrapper))
        {
            return at == "<value>"
                ? what
                : $"{what}, which holds a `{wrapper}` at `{at}`";
        }

        string reason = isEntity
            ? "a bare entity would be the same object in the caller and in the running work"
            : $"it holds `{offender!.Value.Wrapper}` at `{offender.Value.Path}`, which only the work that made it may use";
        string fix = isEntity
            ? "Write `steal` before it to hand it over to the agent, share it with `Guarded`/`Witnessed`/`Atomic`, or " +
              "pass a copyable value"
            : "Share it with `Guarded`/`Witnessed`/`Atomic`, or pass a copyable value";
        ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
            message: $"You pass {what} to an agent, but {reason}. {fix}.",
            location: value.Location);
        return null;
    }

    /// <summary>
    /// Gets a string key representing an expression for uniqueness checking.
    /// Returns null for complex expressions that can't be easily tracked.
    /// </summary>
    private static string? GetExpressionKey(Expression expression)
    {
        return expression switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member =>
                $"{GetExpressionKey(expression: member.Object)}.{member.MemberName}",
            _ => null
        };
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
        }
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
        List<string> builders = _registry.GetMemberRoutinesForType(type: type)
                                         .Where(predicate: r => r.IsCommon && r.Visibility != VisibilityModifier.Secret &&
                                                                r.ReturnType?.BareName == bareName)
                                         .Select(selector: r => $"'{bareName}.{r.Name}(...)'")
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
                    string typeName = ownerType?.Name ?? "type";
                    ReportError(code: SemanticDiagnosticCode.SecretMemberAccess,
                        message:
                        $"Cannot access secret {memberKind} '{memberName}' of '{typeName}' from outside its module.",
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
    /// Checks if the current access context is within the same module as the member.
    /// Module comparison is exact (sub-modules are different modules).
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

        // Module comparison is exact - sub-modules are different modules
        return currentModuleName == memberModule;
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
    /// Used to allow stdlib files to use reserved features (e.g., module Core).
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
