// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core;

/// <summary>
///     A component type's dense index, assigned by the generated type registry at module
///     initialisation. Archetype masks, chunk layouts and query signatures are all keyed on it, so
///     it needs to be small, contiguous, and comparable — which a <see cref="Type" /> is not.
/// </summary>
/// <remarks>
///     <para>
///         <b>Never persist this.</b> The value depends on which assemblies were loaded and in what
///         order, so it is stable within a process and meaningless outside one. Serialised data
///         refers to component types by their <see cref="DataContractAttribute.Alias" />.
///     </para>
///     <para>
///         Ids are assigned from 1, so a zeroed struct is a detectably invalid handle rather than a
///         silent alias for whichever component type happened to register first. Bit 0 of an
///         archetype mask is unused; that is cheaper than the class of bug it removes.
///     </para>
/// </remarks>
public readonly record struct ComponentTypeId(int Value)
    : IComparable<ComponentTypeId>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<ComponentTypeId> {
    /// <summary>The id no component type has, and the value of an uninitialised handle.</summary>
    public static ComponentTypeId Invalid => default;

    /// <summary>Whether this identifies a registered component type.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc />
    public int CompareTo(ComponentTypeId other) => Value.CompareTo(other.Value);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(ComponentTypeId left, ComponentTypeId right) => left.Value < right.Value;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(ComponentTypeId left, ComponentTypeId right) => left.Value <= right.Value;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(ComponentTypeId left, ComponentTypeId right) => left.Value > right.Value;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(ComponentTypeId left, ComponentTypeId right) => left.Value >= right.Value;

    /// <summary>Renders the id as a decimal number.</summary>
    /// <returns>The id in text.</returns>
    public override string ToString() => Value.ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => Value.ToString(format, formatProvider);

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Value.TryFormat(destination, out charsWritten, format, provider);

    /// <inheritdoc />
    public bool TryFormat(
        Span<byte> utf8Destination,
        out int bytesWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Value.TryFormat(utf8Destination, out bytesWritten, format, provider);

    /// <inheritdoc />
    public static ComponentTypeId Parse(string s, IFormatProvider? provider = null) => new(int.Parse(s, provider));

    /// <inheritdoc />
    public static ComponentTypeId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        new(int.Parse(s, provider));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ComponentTypeId result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ComponentTypeId result) {
        if (int.TryParse(s, provider, out var value)) {
            result = new(value);
            return true;
        }

        result = default;
        return false;
    }
}
