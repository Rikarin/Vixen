// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Unicode;

namespace Vixen.Core;

/// <summary>
///     A generation-checked handle to an entity: a dense slot index, the version that slot was on
///     when the handle was taken, and the world it belongs to. Reusing a slot bumps its version, so
///     a stale handle compares unequal to the entity that now lives there instead of silently
///     addressing it.
/// </summary>
/// <param name="Id">The dense slot index. Slot 0 is never allocated, so it means "no entity".</param>
/// <param name="Version">The slot's version when the handle was taken.</param>
/// <param name="WorldId">Which world the slot lives in.</param>
/// <remarks>
///     <para>
///         This is the *runtime* identity and is never written to a scene file: ids are dense and
///         reused, so a saved one means nothing when loaded back. Identity that survives save and
///         load is a component (see <c>docs/plan/04-ecs-and-scripting.md</c> § Determinism).
///     </para>
///     <para>
///         The world id is carried in the handle rather than checked by convention because passing
///         an entity from the editor's world to the play world is a real mistake with no other way
///         to detect it — both handles are live, both slots exist, and the versions agree far more
///         often than anyone expects.
///     </para>
///     <para>
///         It lives in <c>Vixen.Core</c> beside <see cref="ComponentTypeId" /> and
///         <see cref="ComponentAttribute" /> rather than in <c>Vixen.Ecs</c>, for the same reason
///         those do: the reflection and serialization generators name it, and they cannot reference
///         the ECS.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct Entity(int Id, int Version, short WorldId)
    : IComparable<Entity>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<Entity> {
    /// <summary>Longest text <see cref="ToString()" /> can produce: three numbers and two separators.</summary>
    public const int MaxTextLength = 30;

    /// <summary>The handle to no entity. Slot 0 is never allocated, so this cannot collide.</summary>
    public static Entity Null => default;

    /// <summary>Whether this is <see cref="Null" />.</summary>
    public bool IsNull => Id == 0;

    /// <summary>
    ///     Slot and version in one word — version high, id low — for hashing and for packing into a
    ///     sort key.
    /// </summary>
    /// <remarks>
    ///     The world is deliberately not in here. Every use is a key that orders or groups entities
    ///     within one world — a command buffer's playback order, a hierarchy sort — and a comparison
    ///     across worlds is meaningless rather than merely unordered.
    /// </remarks>
    public ulong Packed => ((ulong)(uint)Version << 32) | (uint)Id;

    /// <summary>Unpacks a handle produced by <see cref="Packed" />.</summary>
    /// <param name="packed">The packed form.</param>
    /// <param name="worldId">The world it came from, which <see cref="Packed" /> does not carry.</param>
    /// <returns>The handle it encodes.</returns>
    public static Entity FromPacked(ulong packed, short worldId = 0) =>
        new((int)(uint)packed, (int)(uint)(packed >> 32), worldId);

    /// <inheritdoc />
    /// <remarks>Ordered by world first, so entities of one world stay contiguous when sorted.</remarks>
    public int CompareTo(Entity other) {
        var world = WorldId.CompareTo(other.WorldId);
        return world != 0 ? world : Packed.CompareTo(other.Packed);
    }

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(Entity left, Entity right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(Entity left, Entity right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(Entity left, Entity right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(Entity left, Entity right) => left.CompareTo(right) >= 0;

    /// <summary>Renders the handle as <c>id:version@world</c>.</summary>
    /// <returns>The handle in text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id}:{Version}@{WorldId}");

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        destination.TryWrite(CultureInfo.InvariantCulture, $"{Id}:{Version}@{WorldId}", out charsWritten);

    /// <inheritdoc />
    public bool TryFormat(
        Span<byte> utf8Destination,
        out int bytesWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        Utf8.TryWrite(utf8Destination, CultureInfo.InvariantCulture, $"{Id}:{Version}@{WorldId}", out bytesWritten);

    /// <inheritdoc />
    public static Entity Parse(string s, IFormatProvider? provider = null) => Parse(s.AsSpan(), provider);

    /// <inheritdoc />
    public static Entity Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException($"'{s}' is not an entity handle.");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Entity result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Entity result) {
        result = default;

        var colon = s.IndexOf(':');
        var at = s.IndexOf('@');

        if (colon < 0 || at < colon) {
            return false;
        }

        if (!int.TryParse(s[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !int.TryParse(s[(colon + 1)..at], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !short.TryParse(s[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var world)) {
            return false;
        }

        result = new(id, version, world);
        return true;
    }

    /// <summary>Parses the <c>id:version@world</c> form.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="result">The parsed handle, or <see cref="Null" /> on failure.</param>
    /// <returns><see langword="true" /> if <paramref name="s" /> was a handle.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out Entity result) => TryParse(s, null, out result);

    /// <inheritdoc cref="TryParse(string?, out Entity)" />
    public static bool TryParse(ReadOnlySpan<char> s, out Entity result) => TryParse(s, null, out result);
}
