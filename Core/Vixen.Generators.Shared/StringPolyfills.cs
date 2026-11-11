// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Generators.Shared;

/// <summary>The <see cref="string" /> overloads net11.0 source uses and netstandard2.0 lacks.</summary>
/// <remarks>
///     Extensions, so they apply only where the instance method is missing: compiled for net11.0 the
///     same call binds to the framework's own overload, which is what CA1865 asks of the runtime
///     assemblies. Each one is ordinal, which is the only comparison any linked call site passes.
/// </remarks>
internal static class StringPolyfills {
    /// <summary>Whether <paramref name="text" /> starts with <paramref name="value" />.</summary>
    public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;

    /// <summary>Whether <paramref name="text" /> ends with <paramref name="value" />.</summary>
    public static bool EndsWith(this string text, char value) => text.Length > 0 && text[text.Length - 1] == value;

    /// <summary>Whether <paramref name="text" /> contains <paramref name="value" />.</summary>
    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;

    /// <summary>The first position of <paramref name="value" />, compared ordinally.</summary>
    public static int IndexOf(this string text, char value, StringComparison comparison) =>
        comparison == StringComparison.Ordinal
            ? text.IndexOf(value)
            : text.IndexOf(value.ToString(), comparison);

    /// <summary>Replaces every <paramref name="oldValue" />, compared ordinally.</summary>
    public static string Replace(this string text, string oldValue, string newValue, StringComparison comparison) =>
        comparison == StringComparison.Ordinal
            ? text.Replace(oldValue, newValue)
            : throw new ArgumentException("Only an ordinal replacement is polyfilled.", nameof(comparison));
}
