// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// ======================================================================================
// The second language surface the linked .vxinput front end has to hold on.
//
// Vixen.Input/Assets is net10.0 source compiled here for netstandard2.1. The gap is small
// by construction — that folder was written to be linked, so it touches no file system, no
// environment and no console — and what is missing is two compiler contracts and one throw
// helper that lives *on* a framework exception type.
//
// The helper cannot be an extension method: it is a static on a sealed-by-convention
// framework type. So the simple name is aliased, compilation-wide, onto a subclass that
// carries it. Every `throw new ArgumentNullException(...)` in the linked source still
// constructs something a `catch (ArgumentNullException)` catches, because the alias target
// derives from the framework type rather than replacing it. The idiomatic form is what
// CA1510 asks for in the runtime assembly, so the runtime assembly is where it is written.
//
// ⚠ Vixen.Ui.Markup.Generators/Compat/Netstandard.cs is the other instance of this and needs
// rather more, because it links a compiler front end that was not written with a second
// surface in mind. Aliases are compilation-scoped; nothing outside this project sees them.
// ======================================================================================

global using ArgumentNullException = Vixen.Input.Generators.Compat.ArgumentNullException;

using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices {
    /// <summary>What an <c>init</c> accessor compiles against.</summary>
    /// <remarks>
    ///     A compiler contract rather than a library one — the compiler looks the type up by name
    ///     and any assembly may declare it. .NET Standard 2.1 predates it and the linked schema is
    ///     records all the way down.
    /// </remarks>
    internal static class IsExternalInit;

    /// <summary>What <c>[CallerArgumentExpression]</c> compiles against.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute {
        public string ParameterName { get; } = parameterName;
    }
}

namespace Vixen.Input.Generators.Compat {
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
}
