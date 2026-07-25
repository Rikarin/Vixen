namespace Vixen.Raven.Diagnostics;

/// <summary>
///     Stable descriptors for code generation. The <c>RVN4xxx</c> range is reserved
///     for backends; <c>RVN1xxx</c> is syntax, <c>RVN2xxx</c> semantics and
///     <c>RVN3xxx</c> lowering.
/// </summary>
public static class BackendDiagnostics {
    const string CodeGen = "CodeGen";

    /// <summary>
    ///     The IR is valid, but this particular target has no way to express it —
    ///     an unsized array in a uniform, say.
    /// </summary>
    public static readonly DiagnosticDescriptor NotExpressible = new(
        "RVN4001",
        "Not expressible in the target language",
        "{0} cannot be expressed in {1}",
        CodeGen,
        DiagnosticSeverity.Error
    );

    /// <summary>A construct the backend has not implemented yet.</summary>
    public static readonly DiagnosticDescriptor NotImplemented = new(
        "RVN4002",
        "Not implemented by this backend",
        "{0} is not implemented by the {1} backend yet",
        CodeGen,
        DiagnosticSeverity.Error
    );

    /// <summary>Something the target silently drops; worth saying out loud.</summary>
    public static readonly DiagnosticDescriptor Dropped = new(
        "RVN4003",
        "Dropped by the target",
        "{0}",
        CodeGen,
        DiagnosticSeverity.Info
    );
}
