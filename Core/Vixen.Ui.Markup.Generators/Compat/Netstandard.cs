// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// ======================================================================================
// The second language surface the linked front end has to hold on, minus the part that is
// not specific to this generator.
//
// Vixen.Core.Syntax and Vixen.Ui.Markup are net10.0 source compiled here for
// netstandard2.1, and the gap between the two is smaller than it looks: no file system, no
// environment, no console, no generic math. What is missing is a handful of guard-clause
// helpers that live *on* framework exception types — 116 call sites, most of them in
// generated node classes nobody can edit — and two types the compiler looks up by name.
//
// The compiler contracts, and the ArgumentNullException helper every linking generator
// needs, live in Core/Vixen.Generators.Shared and are linked in by the csproj. What is left
// here is the part only this generator needs: the ArgumentOutOfRangeException helpers.
//
// The helpers cannot be extension methods: they are statics on a sealed-by-convention
// framework type. So the simple name is aliased, compilation-wide, onto a subclass that
// carries them. Every `throw new ArgumentOutOfRangeException(...)` in the linked source
// still constructs something a `catch (ArgumentOutOfRangeException)` catches, because the
// alias target derives from the framework type rather than replacing it. The runtime
// assemblies keep the idiomatic form, which is what CA1510 asks for and what a reader
// expects. Aliases are compilation-scoped, so nothing outside this project can see them.
// ======================================================================================

global using ArgumentOutOfRangeException = Vixen.Ui.Markup.Generators.Compat.ArgumentOutOfRangeException;

using System.Runtime.CompilerServices;

namespace Vixen.Ui.Markup.Generators.Compat;

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
