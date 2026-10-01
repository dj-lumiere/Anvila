using Builder.Verification.Enums;
using SyntaxTree;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Information about a memberRoutine signature in a protocol.
/// </summary>
public sealed class ProtocolMemberRoutineInfo
{
    /// <summary>The name of the memberRoutine.</summary>
    public string Name { get; }

    /// <summary>
    /// Whether this is an instance memberRoutine (has me parameter) or type-level memberRoutine.
    /// </summary>
    public bool IsInstanceMemberRoutine { get; init; } = true;

    /// <summary>The mutation category for this memberRoutine.</summary>
    public MutationCategory Mutation { get; init; } = MutationCategory.Reshaping;

    /// <summary>Parameter types (excluding me for instance memberRoutines).</summary>
    public List<TypeSymbol> ParameterTypes { get; init; } = [];

    /// <summary>Parameter names.</summary>
    public List<string> ParameterNames { get; init; } = [];

    /// <summary>Return type, or null for void.</summary>
    public TypeSymbol? ReturnType { get; init; }

    /// <summary>Whether this memberRoutine can fail (has ! suffix).</summary>
    public bool IsFailable { get; init; }

    /// <summary>The generation kind for this memberRoutine (None, Generated, or Innate).</summary>
    public ProtocolRoutineKind GenerationKind { get; init; } = ProtocolRoutineKind.None;

    /// <summary>Whether this memberRoutine has a default implementation.</summary>
    public bool HasDefaultImplementation { get; init; }

    /// <summary>Source location where this memberRoutine is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolMemberRoutineInfo"/> class.
    /// </summary>
    /// <param name="name">The name of the protocol memberRoutine.</param>
    public ProtocolMemberRoutineInfo(string name)
    {
        Name = name;
    }
}
