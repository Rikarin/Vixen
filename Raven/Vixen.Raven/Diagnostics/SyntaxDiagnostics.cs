using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Diagnostics;

/// <summary>
///     Stable descriptors for syntax (lexer/parser) diagnostics. The <c>RVN1xxx</c>
///     range is reserved for syntax; later phases claim their own ranges (semantics,
///     lowering, emit).
/// </summary>
public static class SyntaxDiagnostics {
    const string Category = "Syntax";

    /// <summary>A parser error: the token stream did not match the grammar.</summary>
    public static readonly DiagnosticDescriptor SyntaxError = new(
        "RVN1001",
        "Syntax error",
        "Syntax error: {0}",
        Category,
        DiagnosticSeverity.Error
    );

    /// <summary>A lexer error: the input contained a character the lexer could not tokenize.</summary>
    public static readonly DiagnosticDescriptor InvalidCharacter = new(
        "RVN1002",
        "Invalid character",
        "Invalid character in input: {0}",
        Category,
        DiagnosticSeverity.Error
    );
}
