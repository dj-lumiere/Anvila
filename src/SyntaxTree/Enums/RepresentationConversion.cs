namespace SyntaxTree;

/// <summary>
/// What a <see cref="BackendCastExpression"/> does to the bits, decided before the backend from the two
/// types' representations (<c>RepresentationCastPass</c>), so a backend only carries it out.
/// </summary>
public enum RepresentationConversion
{
    /// <summary>The two representations are the same: the value passes through.</summary>
    Same,

    /// <summary>A wider integer to a narrower one: the low bits.</summary>
    Truncate,

    /// <summary>A narrower integer to a wider unsigned one: zero-filled.</summary>
    ZeroExtend,

    /// <summary>A narrower integer to a wider signed one: sign-filled.</summary>
    SignExtend,

    /// <summary>Integers of the same width that are different types: the same bits.</summary>
    Bitcast,

    /// <summary>An integer to a pointer.</summary>
    IntToPointer,

    /// <summary>A pointer to an integer.</summary>
    PointerToInt,

    /// <summary>A wider float to a narrower one.</summary>
    FloatTruncate,

    /// <summary>A narrower float to a wider one.</summary>
    FloatExtend,

    /// <summary>A float to a signed integer.</summary>
    FloatToSigned,

    /// <summary>A float to an unsigned integer.</summary>
    FloatToUnsigned,

    /// <summary>A signed integer to a float.</summary>
    SignedToFloat,

    /// <summary>An unsigned integer to a float.</summary>
    UnsignedToFloat
}
