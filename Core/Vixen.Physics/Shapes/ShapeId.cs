// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Physics.Shapes;

/// <summary>A registered shape, as a number small enough to live in a component.</summary>
/// <param name="Value">The one-based index into the registry. Zero is no shape.</param>
/// <remarks>
///     <para>
///         An id and not a reference, so <c>Collider</c> stays blittable and lives in a chunk column
///         rather than in the world's managed store — see <c>ManagedComponentStore</c> on why that
///         matters. It is also what a network message and a scene file can carry, because the same
///         description registered into a fresh registry in the same order gets the same id.
///     </para>
///     <para>
///         Ids are only meaningful against the <see cref="PhysicsShapes" /> that issued them. Using
///         one against a different registry is caught: the registry checks the range, and the
///         description it finds there is almost never the one the caller meant.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct ShapeId(int Value) : IComparable<ShapeId> {
    /// <summary>No shape.</summary>
    public static ShapeId None => default;

    /// <summary>Whether this names a shape at all.</summary>
    public bool IsNone => Value == 0;

    /// <inheritdoc />
    public int CompareTo(ShapeId other) => Value.CompareTo(other.Value);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(ShapeId left, ShapeId right) => left.Value < right.Value;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(ShapeId left, ShapeId right) => left.Value <= right.Value;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(ShapeId left, ShapeId right) => left.Value > right.Value;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(ShapeId left, ShapeId right) => left.Value >= right.Value;

    /// <summary>Renders the id.</summary>
    /// <returns>The id in text.</returns>
    public override string ToString() => IsNone ? "shape none" : $"shape #{Value}";
}
