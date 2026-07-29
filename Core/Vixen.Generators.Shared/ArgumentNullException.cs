// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// ======================================================================================
// The .NET 6 throw helper, for the generators that compile net10.0 source for netstandard2.1.
//
// The helper cannot be an extension method: it is a static on a sealed-by-convention
// framework type. So the simple name is aliased, compilation-wide, onto a subclass that
// carries it. Every `throw new ArgumentNullException(...)` in the linked source still
// constructs something a `catch (ArgumentNullException)` catches, because the alias target
// derives from the framework type rather than replacing it. The idiomatic form is what
// CA1510 asks for in the runtime assembly, so the runtime assembly is where it is written.
//
// ⚠ This is the only place in the repository where a global using alias shadows a framework
// type, and it is deliberately confined to the generators that link source: link it only
// where the linked front end needs it, not into every generator. Aliases are
// compilation-scoped, so nothing outside the linking project can see them.
// ======================================================================================

global using ArgumentNullException = Vixen.Generators.Shared.Compat.ArgumentNullException;

using System.Runtime.CompilerServices;

namespace Vixen.Generators.Shared.Compat;

/// <summary>
///     <see cref="System.ArgumentNullException" /> with the .NET 6 throw helper on it.
/// </summary>
internal sealed class ArgumentNullException : System.ArgumentNullException {
    public ArgumentNullException() { }

    public ArgumentNullException(string? paramName) : base(paramName) { }

    public ArgumentNullException(string? paramName, string? message) : base(paramName, message) { }

    /// <summary>Throws when <paramref name="argument" /> is null.</summary>
    /// <param name="argument">What to check.</param>
    /// <param name="paramName">Filled in by the compiler.</param>
    public static void ThrowIfNull(
        object? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null
    ) {
        if (argument is null) {
            throw new System.ArgumentNullException(paramName);
        }
    }
}
