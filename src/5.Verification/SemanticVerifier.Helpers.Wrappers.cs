using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private bool IsNestedModifying(Expression source)
    {
        while (true)
        {
            // Check if source is a member access expression (e.g., p.child)
            if (source is not MemberExpression member)
            {
                return false;
            }

            // Check if the object being accessed is an identifier
            if (member.Object is not IdentifierExpression id)
            {
                // Could be a chained member access, check recursively
                source = member.Object;
                continue;
            }

            // Look up the variable and check if its type is Modifying<T>
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            return varInfo != null &&
                   // Check if the variable's type is Modifying<T>
                   Wrappers.IsModifyingType(type: varInfo.Type);
        }
    }

    /// <summary>
    /// Validates that a memberRoutine can be called through a read-only wrapper.
    /// Read-only wrappers (Viewing, Consulting) can only call @readonly memberRoutines.
    /// </summary>
    /// <param name="wrapperType">The wrapper type being used.</param>
    /// <param name="memberRoutine">The memberRoutine being called.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateReadOnlyWrapperMemberRoutineAccess(TypeSymbol wrapperType,
        RoutineInfo memberRoutine, SourceLocation location)
    {
        if (!Wrappers.IsReadOnlyWrapper(type: wrapperType))
        {
            return; // Modifiable wrappers can access all memberRoutines
        }

        // Read-only wrappers can only access @readonly memberRoutines
        if (!memberRoutine.IsReadOnly)
        {
            string wrapperName = wrapperType.BareName;
            ReportError(code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                message:
                $"Cannot call writable member routine '{memberRoutine.Name}' through read-only wrapper '{wrapperName}[T]'. " +
                $"Only @readonly member routines are accessible.",
                location: location);
        }
    }
}
