namespace Vixen.Core.Syntax.Diagnostics;

/// <summary>
///     The stable, reusable definition of a diagnostic: its id, a message template,
///     its category and default severity. One descriptor is shared by every
///     <see cref="Diagnostic" /> instance of that kind (à la Roslyn).
/// </summary>
public sealed class DiagnosticDescriptor(
    string id,
    string title,
    string messageFormat,
    string category,
    DiagnosticSeverity defaultSeverity
) {
    /// <summary>Stable identifier, e.g. <c>RVN1001</c>.</summary>
    public string Id { get; } = id;

    /// <summary>Short human-readable title.</summary>
    public string Title { get; } = title;

    /// <summary>Composite format string filled with the diagnostic's arguments.</summary>
    public string MessageFormat { get; } = messageFormat;

    /// <summary>Grouping category, e.g. <c>Syntax</c>.</summary>
    public string Category { get; } = category;

    /// <summary>Severity applied unless a caller overrides it.</summary>
    public DiagnosticSeverity DefaultSeverity { get; } = defaultSeverity;
}
