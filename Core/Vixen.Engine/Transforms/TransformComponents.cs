// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Transforms;

/// <summary>Where an entity is, relative to its parent. The authored value.</summary>
/// <remarks>
///     Position, rotation and scale rather than a matrix, because this is what a human and an
///     inspector edit, and because interpolating a matrix is not a thing anyone means to do. The
///     matrix is derived — see <see cref="WorldTransform" />.
/// </remarks>
public struct LocalTransform {
    /// <summary>Position relative to the parent, or to the world for a root.</summary>
    public Vector3 Position;

    /// <summary>Rotation relative to the parent.</summary>
    public Quaternion Rotation;

    /// <summary>Scale relative to the parent.</summary>
    public Vector3 Scale;

    /// <summary>The transform that changes nothing: origin, identity rotation, unit scale.</summary>
    /// <remarks>
    ///     A property and not a <c>default</c>, because a zeroed <see cref="LocalTransform" /> has a
    ///     zero scale and a zero quaternion — an entity that collapses to a point and whose matrix is
    ///     all zeroes. Anything creating one by hand starts here.
    /// </remarks>
    public static LocalTransform Identity => new() {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    /// <summary>A transform at a position, unrotated and unscaled.</summary>
    /// <param name="position">Where.</param>
    /// <returns>The transform.</returns>
    public static LocalTransform At(Vector3 position) => new() {
        Position = position,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    /// <summary>This transform as a matrix, without reference to any parent.</summary>
    /// <returns>The local matrix.</returns>
    public readonly Matrix4x4 ToMatrix() => Matrix4x4.Compose(Scale, Rotation, Position);
}

/// <summary>Where an entity is in the world. Derived every frame from the hierarchy.</summary>
/// <remarks>
///     Never written by game code. <c>TransformSystem</c> owns it, and anything that writes it
///     directly will have its write thrown away the next time an ancestor moves.
/// </remarks>
public struct WorldTransform {
    /// <summary>The local-to-world matrix.</summary>
    public Matrix4x4 Value;

    /// <summary>The world-space position — the translation row.</summary>
    public readonly Vector3 Position => Value.Translation;
}

/// <summary>The entity this one hangs from. Absent on a root.</summary>
/// <remarks>
///     Absent rather than <see cref="Entity.Null" />, so "is a root" is an archetype question and
///     the roots can be found with a mask test instead of a scan.
/// </remarks>
public struct Parent {
    /// <summary>The parent.</summary>
    public Entity Value;
}

/// <summary>The head of an entity's child list. Absent when it has no children.</summary>
public struct Child {
    /// <summary>The first child, or <see cref="Entity.Null" />.</summary>
    public Entity First;
}

/// <summary>An entity's place in its parent's child list.</summary>
/// <remarks>
///     Intrusive and doubly linked, so adding or removing a child is O(1) with no allocation — the
///     property that makes reparenting cheap enough to do in gameplay code rather than only at load.
/// </remarks>
public struct Sibling {
    /// <summary>The next child of the same parent, or <see cref="Entity.Null" />.</summary>
    public Entity Next;

    /// <summary>The previous child of the same parent, or <see cref="Entity.Null" />.</summary>
    public Entity Previous;
}

/// <summary>How far an entity is from a root. Zero for a root.</summary>
/// <remarks>
///     <para>
///         Maintained by <see cref="Hierarchy" /> on every reparent, so the transform pass can visit
///         parents before children without recursing and without sorting.
///     </para>
///     <para>
///         [04](../../../docs/plan/04-ecs-and-scripting.md) calls for this to split archetypes by
///         depth. It does not, and cannot yet: a component's <i>value</i> takes no part in its
///         archetype, and making it do so means shared components, which the ECS does not have. What
///         that costs and what is done instead is written up in <c>TransformSystem</c>.
///     </para>
/// </remarks>
public struct HierarchyDepth {
    /// <summary>The depth.</summary>
    public short Value;
}
