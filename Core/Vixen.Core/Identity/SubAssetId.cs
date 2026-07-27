// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vixen.Core;

/// <summary>
///     Identity of one object inside an imported asset — a mesh in an FBX, a clip in an animation
///     set — stable across re-imports of the source file.
/// </summary>
/// <remarks>
///     <para>
///         Thirty-two bits, written as eight lowercase hex digits, derived from what the sub-asset
///         <i>is</i> rather than from where it landed. That is the one part of Unity's behaviour
///         worth keeping while discarding its representation: Unity's <c>fileID</c> values are
///         internal magic numbers, so re-exporting an FBX whose mesh order changed silently breaks
///         every reference to it. "My prefab lost its mesh after an artist re-exported" is one of the
///         most common failures in real projects, and it is avoidable by construction.
///     </para>
///     <para>
///         <b>It carries no hash function</b>, the same way <see cref="ObjectId" /> does not.
///         <c>Vixen.Core</c> is BCL-only; the code that derives one has the importer type, the kind
///         and the name in front of it and lives where hashing is allowed. This type is identity,
///         formatting and parsing.
///     </para>
/// </remarks>
/// <param name="Value">The raw thirty-two bits.</param>
[DataContract]
public readonly record struct SubAssetId(uint Value)
    : IComparable<SubAssetId>, ISpanFormattable, ISpanParsable<SubAssetId> {
    /// <summary>Number of characters <see cref="ToString()" /> writes.</summary>
    public const int TextLength = 8;

    /// <summary>No sub-asset: the asset's main object.</summary>
    public static SubAssetId Main => default;

    /// <summary>Whether this names the asset's main object rather than a sub-asset.</summary>
    public bool IsMain => Value == 0;

    /// <inheritdoc />
    public int CompareTo(SubAssetId other) => Value.CompareTo(other.Value);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(SubAssetId left, SubAssetId right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(SubAssetId left, SubAssetId right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(SubAssetId left, SubAssetId right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(SubAssetId left, SubAssetId right) => left.CompareTo(right) >= 0;

    /// <summary>Renders the id as eight lowercase hex digits.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Value.ToString("x8", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(format ?? "x8", formatProvider ?? CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Value.TryFormat(
            destination,
            out charsWritten,
            format.IsEmpty ? "x8" : format,
            provider ?? CultureInfo.InvariantCulture
        );

    /// <inheritdoc />
    public static SubAssetId Parse(string s, IFormatProvider? provider = null) =>
        Parse((s ?? throw new ArgumentNullException(nameof(s))).AsSpan(), provider);

    /// <inheritdoc />
    public static SubAssetId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not eight hex digits.");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out SubAssetId result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out SubAssetId result) {
        if (uint.TryParse(s, NumberStyles.HexNumber, provider ?? CultureInfo.InvariantCulture, out var value)) {
            result = new(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Parses eight hex digits.</summary>
    /// <param name="s">The text.</param>
    /// <param name="result">The id.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out SubAssetId result) => TryParse(s, null, out result);

    /// <summary>Parses eight hex digits.</summary>
    /// <param name="s">The text.</param>
    /// <param name="result">The id.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out SubAssetId result) => TryParse(s, null, out result);
}
