// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Core.Mathematics;

/// <summary>
///     Renders the vector and matrix types as <c>(x, y, z)</c> without allocating, so that a debug
///     overlay drawing a thousand values a frame is not the reason the frame missed its budget.
/// </summary>
/// <remarks>
///     Every type here formats the same way and honours the same per-component format string, which
///     is worth one shared helper rather than the same loop written eight times slightly differently.
/// </remarks>
static class VectorFormat {
    /// <summary>Culture used when the caller does not supply one. Never the current culture: a
    ///     vector in a log file that reads <c>(1,5, 2,0)</c> in one region and <c>(1.5, 2.0)</c> in
    ///     another is a diffing and parsing problem nobody needs.</summary>
    public static IFormatProvider DefaultProvider => CultureInfo.InvariantCulture;

    public static bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider,
        ReadOnlySpan<float> components
    ) {
        // Counted separately and published only on success: ISpanFormattable requires charsWritten
        // to be 0 when the destination was too small, not however far we got before running out.
        charsWritten = 0;
        provider ??= DefaultProvider;
        var count = 0;

        if (!TryAppend(destination, ref count, "(")) {
            return false;
        }

        for (var i = 0; i < components.Length; i++) {
            if (i > 0 && !TryAppend(destination, ref count, ", ")) {
                return false;
            }

            if (!components[i].TryFormat(destination[count..], out var written, format, provider)) {
                return false;
            }

            count += written;
        }

        if (!TryAppend(destination, ref count, ")")) {
            return false;
        }

        charsWritten = count;
        return true;
    }

    public static string ToString(string? format, IFormatProvider? provider, ReadOnlySpan<float> components) {
        // Enough for sixteen "G"-formatted floats and the delimiters, so every type here fits. A
        // custom format wide enough to overflow it falls back rather than truncating.
        Span<char> buffer = stackalloc char[640];
        return TryFormat(buffer, out var written, format, provider, components)
            ? new(buffer[..written])
            : FormatSlow(format, provider, components);
    }

    static string FormatSlow(string? format, IFormatProvider? provider, ReadOnlySpan<float> components) {
        provider ??= DefaultProvider;
        var builder = new System.Text.StringBuilder("(");

        for (var i = 0; i < components.Length; i++) {
            if (i > 0) {
                builder.Append(", ");
            }

            builder.Append(components[i].ToString(format, provider));
        }

        return builder.Append(')').ToString();
    }

    static bool TryAppend(Span<char> destination, ref int charsWritten, ReadOnlySpan<char> text) {
        if (destination.Length - charsWritten < text.Length) {
            return false;
        }

        text.CopyTo(destination[charsWritten..]);
        charsWritten += text.Length;
        return true;
    }
}
