// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Samples.ThirdPersonShooter;

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
