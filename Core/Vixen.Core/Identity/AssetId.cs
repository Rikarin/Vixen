// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core;

/// <summary>
///     Identity of an asset, assigned once when the asset is first imported and never changed.
///     It is what a <c>.meta</c> sidecar carries and what every reference between assets stores,
///     so moving or renaming a file breaks nothing.
/// </summary>
/// <remarks>
///     The default text form is 32 undelimited hex digits (<c>"N"</c>) because that form is what
///     goes into sidecars, catalogues, and paths; parsing accepts every layout
///     <see cref="Guid" /> accepts.
/// </remarks>
[DataContract]
public readonly record struct AssetId(Guid Value)
    : IComparable<AssetId>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<AssetId> {
    /// <summary>Number of characters <see cref="ToString()" /> writes.</summary>
    public const int TextLength = 32;

    /// <summary>The id of no asset — a null reference in a serialised graph.</summary>
    public static AssetId Empty => default;

    /// <summary>Whether this is <see cref="Empty" />.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Mints an id for a newly imported asset.</summary>
    /// <returns>A fresh, random id.</returns>
    public static AssetId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public int CompareTo(AssetId other) => Value.CompareTo(other.Value);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(AssetId left, AssetId right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(AssetId left, AssetId right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(AssetId left, AssetId right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(AssetId left, AssetId right) => left.CompareTo(right) >= 0;

    /// <summary>Renders the id as 32 undelimited hex digits.</summary>
    /// <returns>The id in <c>"N"</c> form.</returns>
    public override string ToString() => Value.ToString("N", null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(Normalize(format), formatProvider);

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Value.TryFormat(destination, out charsWritten, Normalize(format));

    /// <inheritdoc />
    public bool TryFormat(
        Span<byte> utf8Destination,
        out int bytesWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Value.TryFormat(utf8Destination, out bytesWritten, Normalize(format));

    /// <inheritdoc />
    public static AssetId Parse(string s, IFormatProvider? provider = null) =>
        new(Guid.Parse(s, provider));

    /// <inheritdoc />
    public static AssetId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        new(Guid.Parse(s, provider));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out AssetId result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out AssetId result) {
        if (Guid.TryParse(s, provider, out var guid)) {
            result = new(guid);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Parses an id in any layout <see cref="Guid" /> accepts.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="result">The parsed id, or <see cref="Empty" /> on failure.</param>
    /// <returns><see langword="true" /> if <paramref name="s" /> was an id.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out AssetId result) => TryParse(s, null, out result);

    /// <inheritdoc cref="TryParse(string?, out AssetId)" />
    public static bool TryParse(ReadOnlySpan<char> s, out AssetId result) => TryParse(s, null, out result);

    // Guid's own default is "D" (with dashes). Ours is "N", so an empty format has to be
    // rewritten rather than passed through.
    static string Normalize(string? format) => string.IsNullOrEmpty(format) ? "N" : format;

    static ReadOnlySpan<char> Normalize(ReadOnlySpan<char> format) => format.IsEmpty ? "N" : format;
}
