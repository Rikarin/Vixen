// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Unicode;

namespace Vixen.Core;

/// <summary>
///     A generation-checked handle to an entity: a dense slot index plus the version that slot was
///     on when the handle was taken. Reusing a slot bumps its version, so a stale handle compares
///     unequal to the entity that now lives there instead of silently addressing it.
/// </summary>
/// <remarks>
///     This is the *runtime* identity, valid only within one world instance and never written to a
///     scene file. Identity that survives save and load is an asset-level concern and belongs to
///     whatever authored the entity.
/// </remarks>
[DataContract]
public readonly record struct EntityId(uint Index, uint Version)
    : IComparable<EntityId>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<EntityId> {
    /// <summary>Longest text <see cref="ToString()" /> can produce: two uints and a separator.</summary>
    public const int MaxTextLength = 21;

    /// <summary>The handle to no entity. Slot 0 is never allocated, so this cannot collide.</summary>
    public static EntityId Null => default;

    /// <summary>Whether this is <see cref="Null" />.</summary>
    public bool IsNull => Index == 0;

    /// <summary>Both halves in one word — version high, index low — for hashing and packing.</summary>
    public ulong Packed => ((ulong)Version << 32) | Index;

    /// <summary>Unpacks a handle produced by <see cref="Packed" />.</summary>
    /// <param name="packed">The packed form.</param>
    /// <returns>The handle it encodes.</returns>
    public static EntityId FromPacked(ulong packed) => new((uint)packed, (uint)(packed >> 32));

    /// <inheritdoc />
    public int CompareTo(EntityId other) => Packed.CompareTo(other.Packed);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(EntityId left, EntityId right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(EntityId left, EntityId right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(EntityId left, EntityId right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(EntityId left, EntityId right) => left.CompareTo(right) >= 0;

    /// <summary>Renders the handle as <c>index:version</c>.</summary>
    /// <returns>The handle in text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Index}:{Version}");

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        destination.TryWrite(CultureInfo.InvariantCulture, $"{Index}:{Version}", out charsWritten);

    /// <inheritdoc />
    public bool TryFormat(
        Span<byte> utf8Destination,
        out int bytesWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Utf8.TryWrite(utf8Destination, CultureInfo.InvariantCulture, $"{Index}:{Version}", out bytesWritten);

    /// <inheritdoc />
    public static EntityId Parse(string s, IFormatProvider? provider = null) => Parse(s.AsSpan(), provider);

    /// <inheritdoc />
    public static EntityId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException($"'{s}' is not an entity handle.");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out EntityId result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out EntityId result) {
        var separator = s.IndexOf(':');
        if (separator >= 0
            && uint.TryParse(s[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && uint.TryParse(s[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var version)) {
            result = new(index, version);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Parses the <c>index:version</c> form.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="result">The parsed handle, or <see cref="Null" /> on failure.</param>
    /// <returns><see langword="true" /> if <paramref name="s" /> was a handle.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out EntityId result) => TryParse(s, null, out result);

    /// <inheritdoc cref="TryParse(string?, out EntityId)" />
    public static bool TryParse(ReadOnlySpan<char> s, out EntityId result) => TryParse(s, null, out result);
}
