using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// <c>@property</c> routines: read without parentheses like a member variable (<c>foe.alive</c>), never called with
/// them, never assigned. A read is analyzed as the call it stands for (<c>foe.alive()</c>), kept on the read
/// (<see cref="MemberExpression.PropertyCall"/>), and the first lowering pass puts the call in its place.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>The <c>@property</c> routine <paramref name="name"/> reads on <paramref name="type"/>, through a token
    /// or a handle to the value too, or null when the name is a member variable or no such property exists.</summary>
    private RoutineInfo? PropertyOf(TypeSymbol type, string name)
    {
        if (HasMemberVariable(type: type, name: name))
        {
            return null;
        }

        RoutineInfo? routine = _registry.LookupMemberRoutine(type: type, memberRoutineName: name) ??
                               _registry.LookupMemberRoutine(type: type, memberRoutineName: name, isFailable: true);
        // A value of a type parameter has the routines its constraints give it.
        if (routine == null)
        {
            ResolveConstrainedMemberRoutine(isFailableMemberRoutineCall: false, callLookupName: name,
                dispatchType: type, memberRoutine: ref routine);
            ResolveProjectionMemberRoutine(isFailableMemberRoutineCall: false, callLookupName: name,
                dispatchType: type, memberRoutine: ref routine);
        }

        if (routine == null && HandledValueOf(type: type) is { } inner)
        {
            return PropertyOf(type: inner, name: name);
        }

        return routine != null && IsPropertyRoutine(routine: routine) ? routine : null;
    }

    /// <summary>The value a token or a shared entity's handle stands for, or null for any other type.</summary>
    private static TypeSymbol? HandledValueOf(TypeSymbol type)
    {
        if (type is RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [{ } entity] })
        {
            return entity;
        }

        return Wrappers.IsWrapperType(type: type) ? Wrappers.GetWrapperInnerType(wrapperType: type) : null;
    }

    private static bool HasMemberVariable(TypeSymbol type, string name)
    {
        return type switch
        {
            TupleTypeSymbol tuple => tuple.GetField(memberVariableName: name) != null,
            RecordTypeSymbol record => record.LookupMemberVariable(memberVariableName: name) != null,
            EntityTypeSymbol entity => entity.LookupMemberVariable(memberVariableName: name) != null,
            _ => false
        } || (Wrappers.IsWrapperType(type: type) &&
              Wrappers.LookupMemberVariableOnWrapperInnerType(wrapperType: type, memberVariableName: name) != null);
    }

    /// <summary>Whether <paramref name="routine"/> is a <c>@property</c>: itself, or the routine it was made from (a
    /// recovery variant, a concrete copy of a generic routine, a forwarder through a token).</summary>
    private bool IsPropertyRoutine(RoutineInfo routine)
    {
        // A forwarder through a token is one per routine name, made from whichever type reached it first, so its own
        // annotations say nothing about this one: the routine of the value the token reaches does.
        if (routine.WrapperForwarderInnerMemberRoutine is { } inner)
        {
            return routine.OwnerType is { } owner && HandledValueOf(type: owner) is { } value and not
                       GenericParameterTypeSymbol &&
                   _registry.LookupMemberRoutine(type: value, memberRoutineName: routine.Name) is
                       { WrapperForwarderInnerMemberRoutine: null } own
                ? IsPropertyRoutine(routine: own)
                : IsPropertyRoutine(routine: inner);
        }

        return routine.IsProperty || routine.RecoveryOf?.IsProperty == true ||
               routine.GenericDefinition is { } definition && IsPropertyRoutine(routine: definition);
    }

    /// <summary>A read of a <c>@property</c> on <paramref name="objectType"/> (or on the <c>T</c> of a <c>T?</c>
    /// under <c>try</c>/<c>check</c>), analyzed as its call. Null when <paramref name="member"/> reads no property.</summary>
    private TypeSymbol? TryAnalyzePropertyRead(MemberExpression member, TypeSymbol objectType, TypeSymbol lookupType)
    {
        if (PropertyOf(type: lookupType, name: member.MemberName) == null &&
            !(OptionalReceiverPayloadUnderRecovery(objectType: objectType, name: member.MemberName) is { } payload &&
              PropertyOf(type: payload, name: member.MemberName) != null))
        {
            member.PropertyCall = null;
            return null;
        }

        var call = new CallExpression(
            Callee: new MemberExpression(Object: member.Object, MemberName: member.MemberName, Location: member.Location),
            Arguments: [],
            Location: member.Location) { IsPropertyRead = true };
        TypeSymbol result = AnalyzeExpression(expression: call);
        member.PropertyCall = call;
        return result;
    }

    /// <summary>RV-S720: a <c>@property</c> is read as <c>foe.alive</c>, never called as <c>foe.alive()</c>.</summary>
    private void ReportPropertyCalled(CallExpression call, MemberExpression member, RoutineInfo routine)
    {
        if (call.IsPropertyRead || call.IsSynthesizedLowering || _isInCompilerGeneratedBody ||
            !IsPropertyRoutine(routine: routine) || string.IsNullOrEmpty(value: call.Location.FileName) ||
            call.Location.FileName.StartsWith(value: '<'))
        {
            return;
        }

        string receiver = member.Object is IdentifierExpression { Name: var named } ? named : "value";
        ReportError(code: SemanticDiagnosticCode.PropertyCalled,
            message: $"'{member.MemberName}' is a @property, read without parentheses: write " +
                     $"'{receiver}.{member.MemberName}'.",
            location: call.Location);
    }

    /// <summary>RV-S721: a <c>@property</c> is only read. A change is a routine of its own.</summary>
    private void ReportPropertyWrite(MemberExpression target, SourceLocation location)
    {
        if (target.Object.ResolvedType is not { } objectType ||
            PropertyOf(type: objectType, name: target.MemberName) == null)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.PropertyWritten,
            message: $"'{target.MemberName}' is a @property, so it is only read. Change the value it reads through a " +
                     "routine that does that.",
            location: location);
    }

    /// <summary>RV-S718: a <c>@property</c> reads <c>me</c> and changes nothing in it. Only its own body is checked:
    /// a write to a member variable of <c>me</c>, at any depth (<c>me.pos.x = 0</c>).</summary>
    private void ReportPropertyChangesMe(MemberExpression target, SourceLocation location)
    {
        if (_currentRoutine is not { IsProperty: true } property || _isInCompilerGeneratedBody)
        {
            return;
        }

        Expression root = target;
        while (root switch
               {
                   MemberExpression m => m.Object,
                   IndexExpression i => i.Object,
                   _ => null
               } is { } inner)
        {
            root = inner;
        }

        if (root is not IdentifierExpression { Name: "me" })
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.PropertyChangesMe,
            message: $"'{property.Name}' is a @property, which reads 'me' and changes nothing in it, but this changes " +
                     $"'{target.MemberName}'. Make it a plain routine if it has to change 'me'.",
            location: location);
    }
}
