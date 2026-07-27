// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Transforms;

/// <summary>
///     Turns <see cref="LocalTransform" /> plus the hierarchy into <see cref="WorldTransform" />,
///     parents before children, and touches nothing that did not move.
/// </summary>
/// <remarks>
///     <para>
///         <b>A static scene costs nothing.</b> The pass starts from the chunks whose
///         <see cref="LocalTransform" /> column has been written since it last ran, and walks down
///         from there; a frame in which nothing moved visits nothing. That is the whole reason the
///         ECS carries change versions.
///     </para>
///     <para>
///         Reparenting is caught for free: it moves the entity to a different archetype, and an
///         archetype move stamps every column of the destination row. So is creation. Neither needs
///         a dirty flag of its own.
///     </para>
///     <para>
///         <b>What differs from [04](../../../docs/plan/04-ecs-and-scripting.md).</b> That design
///         splits archetypes by <see cref="HierarchyDepth" /> so each level is a set of chunks and
///         every level is a sequential sweep. A component's <i>value</i> takes no part in its
///         archetype here, and making it do so means shared components, which the ECS does not have.
///         So depth 0 — the roots, which are an archetype question (<c>WithNone&lt;Parent&gt;</c>) —
///         <i>is</i> a sequential sweep over spans, and the levels below it are walked through the
///         child lists into per-depth buckets. The cost is random access below the roots rather than
///         sequential; the work is still one visit per moved entity, and the buckets are reused, so
///         a steady state allocates nothing. Shared components would let the levels below become
///         sweeps too, without changing anything a caller sees.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class TransformSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription movedRoots = new QueryDescription()
        .WithAll<LocalTransform, WorldTransform>()
        .WithNone<Parent>()
        .WithChanged<LocalTransform>();

    readonly QueryDescription movedChildren = new QueryDescription()
        .WithAll<LocalTransform, WorldTransform, Parent, HierarchyDepth>()
        .WithChanged<LocalTransform>();

    List<Entity>[] buckets = [[], []];
    uint lastSeen;

    /// <inheritdoc />
    /// <remarks>
    ///     Declared here rather than with attributes, because naming a component type in a generic
    ///     call is what assigns it an id — an attribute can only look one up, and on the first frame
    ///     there is nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<LocalTransform>()
        .Read<Parent>()
        .Read<Child>()
        .Read<Sibling>()
        .Read<HierarchyDepth>()
        .Write<WorldTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Resolve(context.World);
        return dependency;
    }

    /// <summary>Resolves every world transform that is out of date, and no others.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>Public so a test, a tool or an editor can resolve without standing up a runner.</remarks>
    public void Resolve(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var since = lastSeen;
        lastSeen = world.Version;

        foreach (var bucket in buckets) {
            bucket.Clear();
        }

        // Depth 0. Roots are an archetype, so this is the sequential sweep the design wants:
        // two spans, one multiply each, no indirection.
        foreach (var chunk in world.Chunks(movedRoots, since)) {
            var locals = chunk.ReadValues<LocalTransform>();
            var worlds = chunk.Values<WorldTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                worlds[index].Value = locals[index].ToMatrix();
                Enqueue(world, entities[index], 1);
            }
        }

        // Entities below a root that moved on their own. An ancestor that also moved will push them
        // again; that costs one extra multiply and saves keeping a visited set, and because buckets
        // are walked in depth order the second visit is still after the parent's.
        foreach (var chunk in world.Chunks(movedChildren, since)) {
            var entities = chunk.Entities;
            var depths = chunk.ReadValues<HierarchyDepth>();

            for (var index = 0; index < chunk.Count; index++) {
                Add(depths[index].Value, entities[index]);
            }
        }

        for (var depth = 1; depth < buckets.Length; depth++) {
            var bucket = buckets[depth];

            // Grows while it is being walked, so this is an index loop rather than a foreach.
            for (var index = 0; index < bucket.Count; index++) {
                var entity = bucket[index];

                if (!world.IsAlive(entity)) {
                    continue;
                }

                var parent = world.Read<Parent>(entity).Value;
                var local = world.Read<LocalTransform>(entity).ToMatrix();

                // Row-vector convention: the child's own transform applies first, then the parent's.
                // See Vixen.Core.Mathematics/Conventions.md.
                world.Get<WorldTransform>(entity).Value = local * world.Read<WorldTransform>(parent).Value;
                Enqueue(world, entity, depth + 1);
            }
        }
    }

    void Enqueue(World world, Entity entity, int depth) {
        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Add(depth, child);
        }
    }

    void Add(int depth, Entity entity) {
        if (depth >= buckets.Length) {
            var grown = new List<Entity>[Math.Max(depth + 1, buckets.Length * 2)];
            Array.Copy(buckets, grown, buckets.Length);

            for (var index = buckets.Length; index < grown.Length; index++) {
                grown[index] = [];
            }

            buckets = grown;
        }

        buckets[depth].Add(entity);
    }
}
