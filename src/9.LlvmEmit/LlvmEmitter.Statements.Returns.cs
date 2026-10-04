using System.Text;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Statement code generation for return, throw, absent, and variant-return paths.
/// </summary>
public partial class LlvmEmitter
{
    private const string TracePop = "  call void @_rf_trace_pop()";
    private const string RetVoid = "  ret void";

    #region Return Statements

    private void EmitReturn(StringBuilder sb, ReturnStatement ret)
    {
        if (ret.Value == null)
        {
            EmitNullValueReturn(sb: sb);
            return;
        }

        string earlyType = _currentRoutineReturnType != null
            ? GetLlvmType(type: _currentRoutineReturnType)
            : "void";
        if (earlyType == "void")
        {
            // A `return <expr>` in a None-returning routine still EVALUATES a side-effecting <expr>
            // for its effects — only the RESULT is dropped. Skipping this silently swallows a crash:
            // `routine f!()` with `return a // b` (b == 0) desugars to `-> None` + `return
            // a.floordiv(b)`, and the failing division must still fire. After lowering, side effects
            // live in calls (checked arithmetic, failable/floordiv, etc.); a bare `None`/identifier/
            // literal has no effect to preserve and `None` is not an emittable value, so evaluate
            // only a call. The SSA result is discarded.
            if (ret.Value is CallExpression)
            {
                EmitExpression(sb: sb, expr: ret.Value);
            }

            EmitNoneExpressionReturn(sb: sb);
            return;
        }

        EmitValueReturn(sb: sb, ret: ret);
    }

    /// <summary>Emits the IR for a return that carries a non-void, non-crashable value.</summary>
    private void EmitValueReturn(StringBuilder sb, ReturnStatement ret)
    {
        string value = EmitExpression(sb: sb, expr: ret.Value!);
        TypeSymbol? retType = _currentRoutineReturnType ?? GetExpressionType(expr: ret.Value!);
        if (retType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine return type for return statement");
        }

        string llvmType = GetLlvmType(type: retType);

        if (_traceCurrentRoutine)
        {
            EmitLine(sb: sb, line: TracePop);
        }

        // MaybeReturnLoweringPass wrapped every bare value an optional-returning routine returns.
        TypeSymbol? exprType = GetExpressionType(expr: ret.Value!);
        if (IsMaybeType(type: retType) && value != "zeroinitializer" &&
            (exprType == null || !IsMaybeType(type: exprType)))
        {
            throw new InvalidOperationException(
                message: $"A bare value returned as '{retType.FullName}' reached the LLVM emitter in " +
                         $"[{_currentRoutineDiagName}].");
        }

        // Indirect (sret) return: the struct value is stored through the hidden %sret pointer and
        // the function returns void (see _currentReturnViaSret / GenerateRoutineDefinition).
        if (_currentReturnViaSret)
        {
            EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr %sret");
            EmitLine(sb: sb, line: RetVoid);
            return;
        }

        // Coerced (Phase 2) return: reinterpret the struct value into its ABI register type.
        if (_currentReturnCoerceType != null)
        {
            string coerced = CoerceStructToAbi(sb: sb,
                structValue: value,
                structLlvm: llvmType,
                abiType: _currentReturnCoerceType);
            EmitLine(sb: sb, line: $"  ret {_currentReturnCoerceType} {coerced}");
            return;
        }

        EmitLine(sb: sb, line: $"  ret {llvmType} {value}");
    }

    private void EmitNullValueReturn(StringBuilder sb)
    {
        if (_traceCurrentRoutine)
        {
            EmitLine(sb: sb, line: TracePop);
        }

        if (_currentRoutineReturnType == null)
        {
            EmitLine(sb: sb, line: RetVoid);
            return;
        }

        string retLlvmType = GetLlvmType(type: _currentRoutineReturnType);
        if (retLlvmType == "void")
        {
            EmitLine(sb: sb, line: RetVoid);
        }
        else
        {
            string retZero = GetZeroValue(type: _currentRoutineReturnType);
            EmitLine(sb: sb, line: $"  ret {retLlvmType} {retZero}");
        }
    }

    private void EmitNoneExpressionReturn(StringBuilder sb)
    {
        if (_traceCurrentRoutine)
        {
            EmitLine(sb: sb, line: TracePop);
        }

        EmitLine(sb: sb, line: RetVoid);
    }

    #endregion

    #region Throw / Absent

    private void EmitAbsent(StringBuilder sb, AbsentStatement absentStmt)
    {
        // An `absent` in a failable routine is a crash (CrashLoweringPass); here it is a recovery
        // variant's empty carrier.
        if (_currentRoutineIsFailable)
        {
            throw new InvalidOperationException(
                message:
                $"An absent in failable routine '{_currentEmittingRoutine?.RegistryKey}' reached codegen -> CrashLoweringPass must lower it.");
        }

        TypeSymbol absentRetType = _currentEmittingRoutine!.ReturnType!;
        string absentCarrierType = GetLlvmType(type: absentRetType);
        // Balance the routine-entry trace_push. Missing this leaks a frame on the shadow stack
        // every time a `try_X` variant returns absent (which happens at every for-loop exit).
        // Subsequent `_rf_trace_update_loc` calls in the caller then update the leaked frame's
        // slot instead of the caller's, corrupting the stack trace.
        if (_traceCurrentRoutine)
        {
            EmitLine(sb: sb, line: TracePop);
        }

        EmitLine(sb: sb, line: $"  ret {absentCarrierType} zeroinitializer");
    }

    #endregion
}
