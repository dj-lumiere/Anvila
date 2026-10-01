namespace Builder.Instantiation;

/// <summary>
/// Kind of generated error handling variant.
/// </summary>
public enum ErrorHandlingVariantKind
{
    /// <summary>try variant - returns Maybe&lt;T&gt;, errors become None.</summary>
    Try,

    /// <summary>try variant for None-returning routines - returns Bool (true=success, false=error/absent).</summary>
    TryBool,

    /// <summary>grab variant - returns Result&lt;T&gt;, preserves error info.</summary>
    Check,

    /// <summary>lookup variant - returns Lookup&lt;T&gt;, distinguishes error from absence.</summary>
    Lookup
}
