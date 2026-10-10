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
    /// Read tokens bound from an index (<c>var x = s[a]</c>), by variable name, with the index written out
    /// (<c>s[a]</c>), so a write through one can name the exact fix. A hint only: a name rebound in another scope
    /// keeps its last entry.
    /// </summary>
    private readonly Dictionary<string, string> _elementTokenSources = new();

    /// <summary>
    /// Writes out an index read the way the user wrote it (<c>boxes[i]</c>, <c>me.cells[0]</c>), or a placeholder
    /// when a part is not a name, a member, or a literal.
    /// </summary>
    private static string DescribeIndexRead(IndexExpression element)
    {
        static string Part(Expression e)
        {
            return e switch
            {
                IdentifierExpression id => id.Name,
                MemberExpression m => $"{Part(e: m.Object)}.{m.MemberName}",
                LiteralExpression lit => lit.Value.ToString() ?? "…",
                _ => "…"
            };
        }

        return $"{Part(e: element.Object)}[{Part(e: element.Index)}]";
    }

    /// <summary>
    /// Validates that a memberRoutine can be called through a read-only wrapper.
    /// Read-only wrappers (Viewing, Consulting) can only call @readonly memberRoutines.
    /// </summary>
    /// <param name="wrapperType">The wrapper type being used.</param>
    /// <param name="memberRoutine">The memberRoutine being called.</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <param name="receiver">The expression the routine is called on, used to name the fix when it is a read token
    /// bound from an index (<c>var x = s[a]</c>).</param>
    private void ReportGuardedReachedWithoutLock(TypeSymbol wrapperType, string memberName, Expression receiver,
        SourceLocation location, bool isCall = false)
    {
        string holder = receiver is IdentifierExpression named ? named.Name : "the handle";
        string policy = Wrappers.GuardedPolicy(type: wrapperType)!;
        string open = policy == "MultiRead"
            ? $"'using {holder}.consult() as v' to read it, 'using {holder}.amend() as v' to change it"
            : $"'using {holder}.amend() as v'";
        ReportError(code: SemanticDiagnosticCode.GuardedReachedWithoutLock,
            message: $"'{memberName}' is on the value inside '{holder}', a Guarded with the {policy} policy, and that value " +
                     $"is reached only while its lock is held: open it with {open}, then use 'v.{memberName}{(isCall ? "()" : "")}'. " +
                     "Only a Guarded with the ReadOnly policy, which has no lock, is read directly.",
            location: location);
    }

    private void ValidateReadOnlyWrapperMemberRoutineAccess(TypeSymbol wrapperType,
        RoutineInfo memberRoutine, SourceLocation location, Expression? receiver = null)
    {
        if (!Wrappers.IsReadOnlyWrapper(type: wrapperType))
        {
            return; // Modifiable wrappers can access all memberRoutines
        }

        // Read-only wrappers can only access @readonly memberRoutines
        if (!memberRoutine.IsReadOnly)
        {
            string wrapperName = wrapperType.BareName;
            if (receiver is IdentifierExpression id &&
                _elementTokenSources.TryGetValue(key: id.Name, value: out string? source))
            {
                ReportError(code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                    message:
                    $"'{id.Name}' reads the element in place ('var {id.Name} = {source}' gives a read token), so it " +
                    $"cannot call '{memberRoutine.Name}', which changes the element. To change it through " +
                    $"'{id.Name}', bind a write token: 'var {id.Name} = {source}.modify()'.",
                    location: location);
                return;
            }

            string holder = receiver is IdentifierExpression named ? $"'{named.Name}'" : "This value";
            if (Wrappers.GuardedPolicy(type: wrapperType) == "ReadOnly")
            {
                ReportError(code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                    message:
                    $"{holder} is a Guarded with the ReadOnly policy, so its value is only read: it can call only " +
                    $"routines marked @readonly, and '{memberRoutine.Name}' is not. To change the value, share it " +
                    "under the Exclusive or MultiRead policy and open it with 'using ... amend()', or mark " +
                    $"'{memberRoutine.Name}' @readonly if it does not change anything.",
                    location: location);
                return;
            }

            string writeToken = wrapperName == "Consulting" ? "an Amending token ('amend()')" : "a write token ('.modify()')";
            ReportError(code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                message:
                $"{holder} is a read token ({wrapperName}), so it can call only routines marked @readonly, and " +
                $"'{memberRoutine.Name}' is not. Change the value through {writeToken}, or mark " +
                $"'{memberRoutine.Name}' @readonly if it does not change anything.",
                location: location);
        }
    }
}
