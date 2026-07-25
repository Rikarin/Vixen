using System.Runtime.CompilerServices;

namespace Vixen.Core.Syntax;

/// <summary>
///     Guards for states the code believes cannot happen.
/// </summary>
/// <remarks>
///     Not syntax-specific — this belongs in a general <c>Vixen.Core</c> primitives
///     assembly once one exists. It lives here because this is the lowest project in
///     the graph today and both the shared tree and Raven need it.
/// </remarks>
public static class ExceptionUtilities {
    /// <summary>
    ///     Thrown from a branch believed unreachable, reporting where. Preferred over a
    ///     bare <c>NotSupportedException</c> in an exhaustive switch: when the assumption
    ///     turns out to be wrong, the message names the file and line.
    /// </summary>
    public static Exception Unreachable([CallerFilePath] string? path = null, [CallerLineNumber] int line = 0) =>
        new InvalidOperationException($"This program location is thought to be unreachable. File='{path}' Line={line}");
}
