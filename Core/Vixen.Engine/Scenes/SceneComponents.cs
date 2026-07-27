// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Scenes;

/// <summary>Which scene an entity belongs to.</summary>
/// <remarks>
///     <para>
///         On every entity a scene creates, so unloading is a query and a destroy rather than a
///         bookkeeping list that can drift out of step with the world.
///     </para>
///     <para>
///         [04](../../../docs/plan/04-ecs-and-scripting.md) has this splitting archetypes, which
///         would make "everything in scene 3" a mask test. A component's value takes no part in its
///         archetype here — the same limitation the transform depth ran into — so unload is a linear
///         scan of the entities that have the tag at all. That is a one-off cost at unload, not a
///         per-frame one, which is why it was not worth adding shared components for.
///     </para>
/// </remarks>
public struct SceneTag {
    /// <summary>The scene's id.</summary>
    public int SceneId;
}
