using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// A member routine of a generic type may be written for some of the type's instances only, by a kind on the type's
/// parameter (<c>routine List[T].sorted_by(...)</c> with <c>needs RecordType T</c>: it copies the elements). A call of
/// one on an instance its kinds leave out (<c>sorted_by</c> on a <c>List</c> of entities) is RF-S151 at the call.
/// Without it the call builds and the routine's body is never made for that instance. Runs over the analyzed user
/// bodies.
/// </summary>
internal sealed class MemberKindGateChecker(TypeRegistry registry, DiagnosticReporter report)
{
    /// <summary>Checks every call in <paramref name="programs"/>.</summary>
    internal void Check(IEnumerable<SyntaxTree.Program> programs)
    {
        foreach (SyntaxTree.Program program in programs)
        {
            AstWalker.WalkExpressions(root: program, visit: e =>
            {
                if (e is CallExpression { ResolvedRoutine: { } routine } call)
                {
                    CheckCall(call: call, routine: routine);
                }
            });
        }
    }

    private void CheckCall(CallExpression call, RoutineInfo routine)
    {
        RoutineInfo definition = routine.GenericDefinition ?? routine;
        // Only a routine written on the generic type itself (`List[T].sorted_by`), whose kinds are on that type's
        // parameters: a routine written on a bare `T` (`T.modify()`) takes its kinds on the receiver as a whole.
        if (routine.OwnerType is not { IsGenericResolution: true } owner ||
            definition.OwnerType is not { IsGenericDefinition: true } declaredOwner ||
            TypeRegistry.DeriveKindGates(constraints: definition.GenericConstraints)
                        .Where(predicate: g => declaredOwner.GenericParameters!.Contains(item: g.ParameterName))
                        .ToList() is not { Count: > 0 } gates ||
            registry.HasKindVariants(routine: routine) ||
            registry.OwnerKindGatesAdmit(routine: routine, owner: owner))
        {
            return;
        }

        string needs = string.Join(separator: "', '",
            values: gates.Select(selector: g => $"needs {g.ConstraintType} {g.ParameterName}"));
        report(code: SemanticDiagnosticCode.ValueTypeConstraintViolation,
            message: $"'{definition.Name}' is written only for the instances of its type that '{needs}' admits, and " +
                     $"'{owner.Name}' is not one of them, so there is no '{definition.Name}' to call on it. Use a " +
                     "routine that works on these elements in place, or keep values in the collection.",
            location: call.Location);
    }
}
