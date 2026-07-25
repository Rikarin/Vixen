namespace Vixen.Raven.Diagnostics;

/// <summary>
/// Stable descriptors for lowering and IR verification. The <c>RVN3xxx</c> range
/// is reserved for this phase; <c>RVN1xxx</c> is syntax and <c>RVN2xxx</c>
/// semantics.
/// </summary>
public static class LoweringDiagnostics {
    const string Lowering = "Lowering";
    const string Verification = "IR";

    /// <summary>
    /// The semantic model accepted the type, but it has no GPU representation —
    /// <c>string</c>, a tuple, a nullable, a lambda.
    /// </summary>
    public static readonly DiagnosticDescriptor TypeNotRepresentable = new(
        "RVN3001",
        "Type is not representable on the target",
        "Type '{0}' cannot be lowered: it has no representation on the target",
        Lowering,
        DiagnosticSeverity.Error);

    /// <summary>A construct the binder understands but lowering does not implement yet.</summary>
    public static readonly DiagnosticDescriptor ConstructNotSupported = new(
        "RVN3002",
        "Construct is not supported in lowering",
        "{0} cannot be lowered yet",
        Lowering,
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor NotAddressable = new(
        "RVN3003",
        "Target is not addressable",
        "The target of this assignment has no storage the target can address",
        Lowering,
        DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor MissingBody = new(
        "RVN3004",
        "Member has no body",
        "'{0}' has no body, so there is nothing to lower",
        Lowering,
        DiagnosticSeverity.Error);

    // --- Verification -----------------------------------------------------

    public static readonly DiagnosticDescriptor MalformedIr = new(
        "RVN3010",
        "Malformed IR",
        "{0}",
        Verification,
        DiagnosticSeverity.Error);
}
