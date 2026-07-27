// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Behaviors;

/// <summary>The behaviours attached to an entity.</summary>
/// <remarks>
///     A managed component, because a <see cref="Behavior" /> is a reference type — the case
///     [04](../../../docs/plan/04-ecs-and-scripting.md) names when it explains why managed
///     components exist at all. It is the link from the entity to its behaviours; the store's typed
///     buckets are what the per-frame loop walks, and they are what makes the loop monomorphic and
///     contiguous.
/// </remarks>
public struct BehaviorLink {
    /// <summary>The behaviours, in the order they were attached.</summary>
    public Behavior[] Items;
}
