// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>A box of collision, in the level's own units.</summary>
/// <remarks>
///     <para>
///         <b>Why the level does not carry a <c>Collider</c> directly.</b> <c>Collider.Shape</c> is a
///         <c>ShapeId</c> — a one-based index into the physics world's shape registry — and the
///         registry hands out ids in registration order, deduplicating identical descriptions as it
///         goes. A compound shape registers its children first, so the ids interleave, and a level
///         file holding <c>shape: 7</c> would be correct only for one exact registration sequence.
///         <c>ShapeId</c>'s own documentation says a scene can carry one; what it cannot do is say
///         which one without knowing the code that ran first.
///     </para>
///     <para>
///         So the level says what the volume <i>is</i> — a box, this big, this far off the entity's
///         origin — and <c>Arena</c> turns each into a registered shape and a real <c>Collider</c>.
///         The numbers stay where a designer edits them, and no id is ever written down.
///     </para>
/// </remarks>
[Component]
[DataContract("BoxCollision")]
public struct BoxCollision {
    /// <summary>Half the box's size on each axis.</summary>
    public Vector3 HalfExtents;

    /// <summary>Where its centre is, relative to the entity's own origin.</summary>
    /// <remarks>
    ///     A crate's mesh sits on the ground and its entity origin is at its feet, so the box that
    ///     covers it is a metre up. A collider with no offset would bury half of every prop.
    /// </remarks>
    public Vector3 Centre;

    /// <summary>Friction, on <c>Collider</c>'s scale of roughly 0 (ice) to 1 (rubber).</summary>
    public float Friction;
}

/// <summary>Where a player comes in.</summary>
/// <remarks>
///     <para>
///         <b>A component and not a name, and that is a fact about the engine rather than a
///         preference.</b> Entity names are a table on the <c>SceneAsset</c> and never a component —
///         thirty bytes per entity in every chunk of a shipping build is the wrong place for them —
///         so a running game cannot look an entity up by what the editor calls it. Anything the game
///         needs to <i>find</i> therefore has to say so in a component.
///     </para>
///     <para>
///         It is declared here, in the game's own assembly, and the engine needs no registration
///         call: <c>[Component]</c> plus <c>[DataContract]</c> is what admits a type to a scene file,
///         and <c>Vixen.Engine.Generators</c> emits the module initializer that tells
///         <c>SceneComponentRegistry</c> about it. That is the whole of what a game does to add a
///         component a level can place.
///     </para>
/// </remarks>
[Component]
[DataContract("SpawnPoint")]
public struct SpawnPoint {
    /// <summary>Which start this is, so a match can hand them out in order.</summary>
    public int Index;
}

/// <summary>Marks the segmented body a player's visuals hang from.</summary>
/// <remarks>
///     The parts — torso, head, arms, legs — are children of this, one <c>.obj</c> each, because
///     <c>.obj</c> carries no rig and so nothing here is skinned. The joint entities are what the
///     animation drives, and the meshes go along because they are parented to them.
/// </remarks>
[Component]
[DataContract("CharacterVisuals")]
public struct CharacterVisuals {
    /// <summary>How far the visuals are turned from the body, in radians about the world's up axis.</summary>
    /// <remarks>
    ///     A character that turns to face where it is moving turns its <i>visuals</i>, not its
    ///     capsule: a capsule that rotated would change what its sweep hits, which is a physical
    ///     consequence of a purely cosmetic decision.
    /// </remarks>
    public float Facing;
}
