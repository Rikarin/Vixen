// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.TextureGraph;

/// <summary>
///     What a <c>Source/Text</c> or <c>Source/Vector</c> node asks a host to draw, written into the reference of the
///     external image it reads — Patina #139.
/// </summary>
/// <remarks>
///     <para>
///         <b>A picture a host fills, like a bitmap, and for doc 48 § 4.1's reason.</b> A compilation runs on every
///         edit and must not open a file, so a font or an SVG crosses as its name. Everything else the node was told
///         — the string, the size, the alignment, where it sits — crosses with it in one reference, so no host
///         re-reads a node's settings and no second answer to "what does letter spacing mean" is written anywhere.
///         <c>Vixen.TextureGraph.Drawing</c> reads this back, asks the host for the file's bytes and draws.
///     </para>
///     <para>
///         ⚠ <b>The scheme, then the fields as a query.</b> Every value is escaped, so a string with an
///         ampersand in it is one field, and the reference a diagnostic quotes is still readable. The order is
///         the order the node wrote them in, which makes the reference — and so the emission record a slider's
///         refresh compares — a function of the values alone.
///     </para>
/// </remarks>
static class TextureDrawing {
    /// <summary>What a <c>Source/Text</c> reference starts with.</summary>
    public const string TextScheme = "text:";

    /// <summary>What a <c>Source/Vector</c> reference starts with.</summary>
    public const string VectorScheme = "vector:";

    /// <summary>Writes a reference.</summary>
    /// <param name="scheme">One of the two schemes.</param>
    /// <param name="fields">The fields, in the order they are written.</param>
    /// <returns>The reference.</returns>
    public static string Reference(string scheme, params ReadOnlySpan<(string Key, string Value)> fields) {
        StringBuilder text = new(scheme);

        for (var at = 0; at < fields.Length; at++) {
            if (at > 0) {
                text.Append('&');
            }

            text.Append(fields[at].Key).Append('=').Append(Uri.EscapeDataString(fields[at].Value));
        }

        return text.ToString();
    }

    /// <summary>A number as a reference writes it: invariant, and round-tripping.</summary>
    /// <param name="value">The number.</param>
    /// <returns>Its text.</returns>
    public static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Reads a reference back.</summary>
    /// <param name="reference">What a node wrote.</param>
    /// <param name="scheme">Which of the two schemes it carries, or empty for neither.</param>
    /// <returns>Its fields, or an empty table for a reference that is not a drawing.</returns>
    public static IReadOnlyDictionary<string, string> Parse(string reference, out string scheme) {
        ArgumentNullException.ThrowIfNull(reference);

        scheme = reference.StartsWith(TextScheme, StringComparison.Ordinal)
            ? TextScheme
            : reference.StartsWith(VectorScheme, StringComparison.Ordinal)
                ? VectorScheme
                : string.Empty;

        Dictionary<string, string> fields = new(StringComparer.Ordinal);

        if (scheme.Length == 0) {
            return fields;
        }

        foreach (var field in reference[scheme.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var equals = field.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0) {
                fields[field[..equals]] = Uri.UnescapeDataString(field[(equals + 1)..]);
            }
        }

        return fields;
    }
}
