// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// ======================================================================================
// The second language surface the linked front end has to hold on.
//
// Vixen.Core.Syntax and Vixen.Ui.Markup are net10.0 source compiled here for
// netstandard2.1, and the gap between the two is smaller than it looks: no file system, no
// environment, no console, no generic math. What is missing is a handful of guard-clause
// helpers that live *on* framework exception types — 116 call sites, most of them in
// generated node classes nobody can edit — and one type the compiler looks up by name.
//
// The helpers cannot be extension methods: they are statics on a sealed-by-convention
// framework type. So the simple name is aliased, compilation-wide, onto a subclass that
// carries them. Every `throw new ArgumentOutOfRangeException(...)` in the linked source
// still constructs something a `catch (ArgumentOutOfRangeException)` catches, because the
// alias target derives from the framework type rather than replacing it.
//
// ⚠ This file is the only place in the repository where a global using alias shadows a
// framework type, and it is deliberately confined to the generator: the runtime assemblies
// keep the idiomatic form, which is what CA1510 asks for and what a reader expects. Aliases
// are compilation-scoped, so nothing outside this project can see them.
// ======================================================================================

global using ArgumentNullException = Vixen.Ui.Markup.Generators.Compat.ArgumentNullException;
global using ArgumentOutOfRangeException = Vixen.Ui.Markup.Generators.Compat.ArgumentOutOfRangeException;

using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices {
    /// <summary>What an <c>init</c> accessor compiles against.</summary>
    /// <remarks>
    ///     A compiler contract rather than a library one — the compiler looks the type up by name
    ///     and any assembly may declare it. .NET Standard 2.1 predates it and the linked front end
    ///     is full of records.
    /// </remarks>
    internal static class IsExternalInit;

    /// <summary>What <c>[CallerArgumentExpression]</c> compiles against.</summary>
    /// <remarks>
    ///     Declared for the same reason and read the same way: the compiler matches on the name, so
    ///     the helpers below report the caller's own expression rather than a hard-coded name.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute {
        public string ParameterName { get; } = parameterName;
    }
}

namespace Vixen.Ui.Markup.Generators.Compat {
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

    /// <summary>
    ///     <see cref="System.ArgumentOutOfRangeException" /> with the .NET 8 throw helpers on it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The framework's are generic over <c>INumberBase&lt;T&gt;</c>, which netstandard2.1 has
    ///     no interface for. These take <see cref="int" />, which is what every call site in the
    ///     linked source passes — a narrower polyfill that fails to compile if that stops being
    ///     true, rather than a wider one that silently accepts something it cannot compare.
    /// </remarks>
    internal sealed class ArgumentOutOfRangeException : System.ArgumentOutOfRangeException {
        public ArgumentOutOfRangeException() { }

        public ArgumentOutOfRangeException(string? paramName) : base(paramName) { }

        public ArgumentOutOfRangeException(string? paramName, string? message) : base(paramName, message) { }

        public ArgumentOutOfRangeException(string? paramName, object? actualValue, string? message)
            : base(paramName, actualValue, message) { }

        /// <summary>Throws when <paramref name="value" /> is negative.</summary>
        /// <param name="value">What to check.</param>
        /// <param name="paramName">Filled in by the compiler.</param>
        public static void ThrowIfNegative(
            int value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null
        ) {
            if (value < 0) {
                throw new System.ArgumentOutOfRangeException(paramName, value, "The value must not be negative.");
            }
        }

        /// <summary>Throws when <paramref name="value" /> is less than <paramref name="other" />.</summary>
        /// <param name="value">What to check.</param>
        /// <param name="other">The floor.</param>
        /// <param name="paramName">Filled in by the compiler.</param>
        public static void ThrowIfLessThan(
            int value,
            int other,
            [CallerArgumentExpression(nameof(value))] string? paramName = null
        ) {
            if (value < other) {
                throw new System.ArgumentOutOfRangeException(paramName, value, $"The value must be at least {other}.");
            }
        }
    }
}
