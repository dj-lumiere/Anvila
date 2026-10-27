namespace TypeModel.Enums;

/// <summary>
/// Whether a record type is a builder-known error-handling carrier (Optional, Result, Lookup).
/// </summary>
public enum CarrierKind
{
    /// <summary>Not a carrier type — ordinary record.</summary>
    None = 0,

    /// <summary>Optional[T] — optional value (None or value).</summary>
    Optional,

    /// <summary>Result[T] — error or value.</summary>
    Result
}
