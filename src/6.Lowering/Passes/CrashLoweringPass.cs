using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Writes every crash out as a call of Core's <c>crash_report</c>, so the emitter only translates a call.
///
/// <para>A <c>throw</c> nothing recovers, an <c>absent</c> in a failable routine and a crashable returned
/// from a failable routine each become a <see cref="CrashStatement"/>:
/// <c>crash_report(type_name: "&lt;error type&gt;", message: error.crash_message(), file: .., line: .., column: ..)</c>.
/// An <c>absent</c> reports <c>AbsentValueError</c> with the routine's name. A recovery variant's
/// <c>absent</c> (it returns the empty carrier) is left as it is.</para>
///
/// <para>Runs at Phase 9 over each body that reaches the emitter, after monomorphization, so a thrown
/// value's type is concrete. A <c>throw</c> whose error type is still a generic parameter belongs to a
/// generic template the emitter never translates, and is left as it is. The demand collector makes
/// <c>crash_report</c> and the thrown type's <c>crash_message</c> live for every reached crash.</para>
/// </summary>
internal sealed class CrashLoweringPass : AstRewriter
{
    /// <summary>The error type an <c>absent</c> in a failable routine reports.</summary>
    private const string AbsentValueError = "AbsentValueError";

    private readonly TypeRegistry _registry;
    private readonly RoutineInfo? _routine;
    private readonly RoutineInfo _crashReport;
    private readonly TypeSymbol _text;
    private readonly TypeSymbol _s32;

    private CrashLoweringPass(TypeRegistry registry, RoutineInfo? routine, RoutineInfo crashReport,
        TypeSymbol text, TypeSymbol s32)
    {
        _registry = registry;
        _routine = routine;
        _crashReport = crashReport;
        _text = text;
        _s32 = s32;
    }

    /// <summary>Lowers the crashes of one routine body in place. <paramref name="routine"/> is the routine
    /// the body belongs to (null for a recovery-variant body, which is never failable).</summary>
    public static void Run(Statement body, RoutineInfo? routine, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"The body of '{routine?.RegistryKey}' must be a block, got {body.GetType().Name}.");
        }

        (TypeSymbol text, TypeSymbol s32, RoutineInfo crashReport) = CrashReportParts(registry: registry);
        var pass = new CrashLoweringPass(registry: registry,
            routine: routine,
            crashReport: crashReport,
            text: text,
            s32: s32);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    public override Statement VisitStatement(Statement stmt)
    {
        return stmt switch
        {
            ThrowStatement { Error.ResolvedType: { } errorType } throwStmt
                when errorType is not (ErrorTypeSymbol or GenericParameterTypeSymbol) =>
                Crash(typeName: errorType.Name,
                    message: CrashMessage(error: throwStmt.Error, errorType: errorType),
                    location: throwStmt.Location),
            ReturnStatement { Value: { ResolvedType: CrashableTypeSymbol crashable } error } ret
                when _routine is { IsFailable: true } =>
                Crash(typeName: crashable.Name,
                    message: CrashMessage(error: error, errorType: crashable),
                    location: ret.Location),
            AbsentStatement absent when _routine is { IsFailable: true } =>
                Crash(typeName: AbsentValueError,
                    message: Text(value: $"Routine '{_routine.BaseName}' signaled absent.", location: absent.Location),
                    location: absent.Location),
            _ => base.VisitStatement(stmt: stmt)
        };
    }

    /// <summary><c>error.crash_message()</c>, or an empty text when the error type has none.</summary>
    private Expression CrashMessage(Expression error, TypeSymbol errorType)
    {
        RoutineInfo? crashMessage = _registry.LookupMemberRoutineOverload(type: errorType,
            memberRoutineName: RuntimeContract.CrashMessage,
            argTypes: []);
        if (crashMessage is not { IsGenericDefinition: false })
        {
            return Text(value: "", location: error.Location);
        }

        return new CallExpression(
            Callee: new MemberExpression(Object: error, MemberName: RuntimeContract.CrashMessage,
                Location: error.Location) { ResolvedType = crashMessage.ReturnType },
            Arguments: [],
            Location: error.Location) { ResolvedRoutine = crashMessage, ResolvedType = _text };
    }

    private CrashStatement Crash(string typeName, Expression message, SourceLocation location)
    {
        return new CrashStatement(
            Report: Report(crashReport: _crashReport, text: _text, s32: _s32, typeName: typeName, message: message,
                location: location),
            Location: location);
    }

    /// <summary>
    /// <c>crash_report(type_name: "&lt;typeName&gt;", message: "&lt;message&gt;", file: .., line: .., column: ..)</c>
    /// for a crash at <paramref name="location"/> with a fixed message. Other passes that write a crash
    /// (StealGuardLoweringPass) build it here, so every crash goes through the same call.
    /// </summary>
    internal static CallExpression Report(TypeRegistry registry, string typeName, string message,
        SourceLocation location)
    {
        (TypeSymbol text, TypeSymbol s32, RoutineInfo crashReport) = CrashReportParts(registry: registry);
        return Report(crashReport: crashReport, text: text, s32: s32, typeName: typeName,
            message: Text(value: message, text: text, location: location), location: location);
    }

    private static CallExpression Report(RoutineInfo crashReport, TypeSymbol text, TypeSymbol s32, string typeName,
        Expression message, SourceLocation location)
    {
        return new CallExpression(
            Callee: new IdentifierExpression(Name: RuntimeContract.CrashReport, Location: location)
            {
                ResolvedRoutine = crashReport
            },
            Arguments:
            [
                Named(name: "type_name", value: Text(value: typeName, text: text, location: location)),
                Named(name: "message", value: message),
                Named(name: "file", value: Text(value: location.FileName, text: text, location: location)),
                Named(name: "line", value: S32(value: location.Line, s32: s32, location: location)),
                Named(name: "column", value: S32(value: location.Column, s32: s32, location: location))
            ],
            Location: location) { ResolvedRoutine = crashReport };
    }

    private static (TypeSymbol Text, TypeSymbol S32, RoutineInfo CrashReport) CrashReportParts(TypeRegistry registry)
    {
        TypeSymbol text = registry.LookupType(name: "Text") ??
                          throw new InvalidOperationException(message: "Core.Text is not registered.");
        TypeSymbol s32 = registry.LookupType(name: "S32") ??
                         throw new InvalidOperationException(message: "Core.S32 is not registered.");
        RoutineInfo crashReport = registry.LookupRoutineOverload(
                                      baseName: $"Core.{RuntimeContract.CrashReport}",
                                      argTypes: [text, text, text, s32, s32]) ??
                                  throw new InvalidOperationException(
                                      message: $"Core.{RuntimeContract.CrashReport} is not registered.");
        return (text, s32, crashReport);
    }

    private static NamedArgumentExpression Named(string name, Expression value)
    {
        return new NamedArgumentExpression(Name: name, Value: value, Location: value.Location)
        {
            ResolvedType = value.ResolvedType
        };
    }

    private LiteralExpression Text(string value, SourceLocation location)
    {
        return Text(value: value, text: _text, location: location);
    }

    private static LiteralExpression Text(string value, TypeSymbol text, SourceLocation location)
    {
        return new LiteralExpression(Value: value, LiteralType: Builder.Tokenizer.TokenType.TextLiteral,
            Location: location) { ResolvedType = text };
    }

    private static LiteralExpression S32(int value, TypeSymbol s32, SourceLocation location)
    {
        return new LiteralExpression(Value: value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            LiteralType: Builder.Tokenizer.TokenType.S32Literal,
            Location: location) { ResolvedType = s32 };
    }
}
