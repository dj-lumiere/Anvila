namespace TypeModel.Enums;

/// <summary>
/// Whether a record type is a builder-known value carrier: <c>Optional</c> (a value is there or not) or <c>Check</c>
/// (one value or the reason it is absent).
/// </summary>
public enum CarrierKind
{
    /// <summary>Not a carrier type, an ordinary record.</summary>
    None = 0,

    /// <summary>Optional[T]: a value is there or not.</summary>
    Optional,

    /// <summary>Check[T]: one value or the reason it is absent.</summary>
    Check
}
