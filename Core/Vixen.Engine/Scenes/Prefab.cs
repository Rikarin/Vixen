// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Scenes;

/// <summary>
///     An entity subtree, captured once and stamped out many times.
/// </summary>
/// <remarks>
///     <para>
///         Held as a world of its own — the template — rather than as a pile of blobs. That gives the
///         capture nothing to serialise and nothing to reinterpret: the components are already laid
///         out exactly as they will be in the target, so instantiating is a run of
///         <see cref="World.CreateMany" /> calls, one per distinct archetype, and a row copy each.
///         [04](../../../docs/plan/04-ecs-and-scripting.md) asks for "one archetype write per
///         archetype, not entity-at-a-time"; this is that, and it also means a prefab can be
///         inspected and edited with the same API as anything else.
///     </para>
///     <para>
///         <b>The hierarchy is rebuilt, not remapped.</b> <see cref="Parent" />, <see cref="Child" />
///         and <see cref="Sibling" /> hold entity handles, and a handle copied into another world
///         names a slot in the world it came from. Rather than translate them — which would need to
///         know which fields of which components are handles — the capture records the tree as
///         indices and the instantiation re-parents. Any *other* component holding an
///         <see cref="Entity" /> comes across verbatim and is a bug in the prefab, which is why doc 04
///         says never to store one.
///     </para>
/// </remarks>
public sealed class Prefab : IDisposable {
    readonly World template;
    readonly Entity[] nodes;
    readonly int[] parents;
    readonly Archetype[] archetypes;
    readonly int[] archetypeOf;
    readonly int[][] groups;

    /// <summary>A name, for diagnostics and for the asset that will own it.</summary>
    public string Name { get; }

    /// <summary>How many entities one instance creates.</summary>
    public int EntityCount => nodes.Length;

    /// <summary>How many distinct archetypes it touches — how many bulk creates an instantiation is.</summary>
    public int ArchetypeCount => archetypes.Length;

    Prefab(string name, World template, Entity[] nodes, int[] parents) {
        Name = name;
        this.template = template;
        this.nodes = nodes;
        this.parents = parents;

        var distinct = new List<Archetype>();
        archetypeOf = new int[nodes.Length];

        for (var index = 0; index < nodes.Length; index++) {
            var archetype = template.ArchetypeOf(nodes[index]);
            var position = distinct.IndexOf(archetype);

            if (position < 0) {
                position = distinct.Count;
                distinct.Add(archetype);
            }

            archetypeOf[index] = position;
        }

        archetypes = [.. distinct];

        // Which nodes share an archetype is fixed when the prefab is captured, so the grouping is
        // worked out once here rather than rebuilt on every instantiation. The root is left out of
        // it: it is created on its own, because instantiating over a stand-in has to widen its
        // archetype and the bulk path cannot.
        var members = new List<int>[archetypes.Length];

        for (var index = 0; index < members.Length; index++) {
            members[index] = [];
        }

        for (var index = 1; index < nodes.Length; index++) {
            members[archetypeOf[index]].Add(index);
        }

        groups = new int[archetypes.Length][];

        for (var index = 0; index < groups.Length; index++) {
            groups[index] = [.. members[index]];
        }
    }

    /// <summary>Whether one of the captured nodes carries a component.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="node">Which node, in capture order, root first.</param>
    /// <returns>Whether it has one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="node" /> is not one of them.</exception>
    /// <remarks>
    ///     For deciding something about an instance's shape once, when the prefab is registered,
    ///     rather than per instantiation. Networked spawning uses it to work out which of a prefab's
    ///     entities want an id of their own — a hundred-entity set piece where only the turret moves
    ///     should cost one id, not a hundred.
    /// </remarks>
    public bool NodeHas<T>(int node) {
        ArgumentOutOfRangeException.ThrowIfNegative(node);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(node, nodes.Length);

        return template.Has<T>(nodes[node]);
    }

    /// <summary>Captures an entity and everything below it.</summary>
    /// <param name="world">The world to read.</param>
    /// <param name="root">The subtree root.</param>
    /// <param name="name">A name for the prefab.</param>
    /// <returns>The prefab.</returns>
    /// <remarks>
    ///     The capture is a copy: changing the source afterwards does not change the prefab, and
    ///     destroying the source does not invalidate it.
    /// </remarks>
    public static Prefab CaptureFrom(World world, Entity root, string name = "Prefab") {
        ArgumentNullException.ThrowIfNull(world);

        var sources = new List<Entity>();
        var parents = new List<int>();
        Walk(world, root, -1, sources, parents);

        var template = new World($"Prefab:{name}");
        var nodes = new Entity[sources.Count];

        for (var index = 0; index < sources.Count; index++) {
            // Same archetype as the source, minus the hierarchy, which is rebuilt from `parents`.
            var signature = Without(world.ArchetypeOf(sources[index]).Signature);
            nodes[index] = template.Create(template.ArchetypeOf(signature.Ids));
            template.CopyComponentsFrom(nodes[index], world, sources[index]);
        }

        return new(name, template, nodes, [.. parents]);
    }

    /// <summary>Stamps out an instance.</summary>
    /// <param name="world">Where to put it.</param>
    /// <param name="at">Where to put the root, or <see langword="null" /> to keep the captured transform.</param>
    /// <returns>The instance's root.</returns>
    public Entity Instantiate(World world, LocalTransform? at = null) => Instantiate(world, default, at);

    /// <summary>Stamps out an instance and says which entity each of its nodes became.</summary>
    /// <param name="world">Where to put it.</param>
    /// <param name="created">
    ///     Filled with one entity per node, in capture order, root first. Pass an empty span not to
    ///     ask; anything else must be exactly <see cref="EntityCount" /> long.
    /// </param>
    /// <param name="at">Where to put the root, or <see langword="null" /> to keep the captured transform.</param>
    /// <returns>The instance's root.</returns>
    /// <exception cref="ArgumentException"><paramref name="created" /> is the wrong length.</exception>
    /// <remarks>
    ///     The order is what lets a caller address the nodes without a table: networked spawning
    ///     numbers an instance's entities from one reserved id, and this is the order it numbers them
    ///     in.
    /// </remarks>
    public Entity Instantiate(World world, Span<Entity> created, LocalTransform? at = null) =>
        Build(world, Entity.Null, created, at);

    /// <summary>Stamps out an instance over an entity that was already standing in for its root.</summary>
    /// <param name="world">Where to put it.</param>
    /// <param name="standIn">The entity to absorb. Destroyed; the returned root replaces it.</param>
    /// <param name="created">As <see cref="Instantiate(World, Span{Entity}, LocalTransform?)" />.</param>
    /// <param name="at">Where to put the root, or <see langword="null" /> to keep the captured transform.</param>
    /// <returns>The instance's root, which is <b>not</b> <paramref name="standIn" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="created" /> is the wrong length.</exception>
    /// <remarks>
    ///     <para>
    ///         For a receiver that had to make an entity before it knew what the entity was — which is
    ///         what a networked client does when a snapshot describes an object whose spawn record has
    ///         not caught up yet. The stand-in's components are real and current and the prefab's are
    ///         defaults, so <b>the stand-in wins wherever the two overlap</b>: the position that
    ///         arrived over the wire is where the object is, and the position the artist saved into
    ///         the prefab is where it would have been.
    ///     </para>
    ///     <para>
    ///         <b>The stand-in's handle does not survive.</b> Its archetype has to grow by everything
    ///         the prefab's root has, and the root has to be one entity rather than two, so the result
    ///         is a new row and the old one is destroyed. Callers holding the old handle are holding a
    ///         dead one — which for the networking is exactly one map entry, rebound here.
    ///     </para>
    ///     <para>
    ///         Its hierarchy links are not absorbed. A stand-in with children would be a subtree the
    ///         prefab knows nothing about, and grafting one onto an instance root is a decision this
    ///         has no basis for making.
    ///     </para>
    /// </remarks>
    public Entity InstantiateOnto(World world, Entity standIn, Span<Entity> created, LocalTransform? at = null) =>
        Build(world, standIn, created, at);

    Entity Build(World world, Entity standIn, Span<Entity> created, LocalTransform? at) {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(template.IsDisposed, this);

        if (!created.IsEmpty && created.Length != nodes.Length) {
            throw new ArgumentException(
                $"'{Name}' has {nodes.Length} entities and the span is {created.Length}. Pass an empty span not to "
                + "ask which entity each node became.",
                nameof(created)
            );
        }

        var instances = created.IsEmpty ? new Entity[nodes.Length] : created;

        instances[0] = world.IsAlive(standIn) ? Absorb(world, standIn) : Fresh(world, 0);

        // One bulk create per archetype for everything below the root, not one per entity. A
        // two-hundred entity prefab of four archetypes is four of these and one for the root.
        for (var group = 0; group < groups.Length; group++) {
            var members = groups[group];

            if (members.Length == 0) {
                continue;
            }

            var batch = new Entity[members.Length];
            world.CreateMany(world.ArchetypeOf(archetypes[group].Signature.Ids), batch);

            for (var index = 0; index < members.Length; index++) {
                instances[members[index]] = batch[index];
                world.CopyComponentsFrom(batch[index], template, nodes[members[index]]);
            }
        }

        // ⚠ Back to front, and the sibling-order test is what holds this honest. `Hierarchy.SetParent`
        // puts a new child at the *head* of the intrusive list — O(1), which is the reason the list is
        // intrusive at all — so linking the nodes in capture order leaves every instance holding its
        // children reversed. Nothing about that looks wrong until draw order, a script's walk over its
        // children, or what an editor shows depends on it; and a prefab that does not stamp out what
        // was captured is not a prefab. The compiled-scene load has the same loop for the same reason.
        for (var index = nodes.Length - 1; index >= 0; index--) {
            if (parents[index] >= 0) {
                Hierarchy.SetParent(world, instances[index], instances[parents[index]]);
            }
        }

        if (at is { } local && world.Has<LocalTransform>(instances[0])) {
            world.Set(instances[0], local);
        }

        return instances[0];
    }

    Entity Fresh(World world, int node) {
        var entity = world.Create(world.ArchetypeOf(archetypes[archetypeOf[node]].Signature.Ids));
        world.CopyComponentsFrom(entity, template, nodes[node]);

        return entity;
    }

    Entity Absorb(World world, Entity standIn) {
        // The union of what the prefab's root has and what the stand-in already had, minus the
        // hierarchy — which is rebuilt from `parents` and would otherwise arrive as a handle naming
        // a row that is about to be freed.
        var signature = template.ArchetypeOf(nodes[0]).Signature;

        foreach (var id in Without(world.ArchetypeOf(standIn).Signature).Ids) {
            signature = signature.With(id);
        }

        var root = world.Create(world.ArchetypeOf(signature.Ids));

        // Twice, and the order is the rule: the prefab lays down its defaults and the stand-in
        // overwrites the ones it has an answer for.
        world.CopyComponentsFrom(root, template, nodes[0]);
        world.CopyComponentsFrom(root, world, standIn);

        Hierarchy.SetParent(world, standIn, Entity.Null);
        world.Destroy(standIn);

        return root;
    }

    /// <inheritdoc />
    public void Dispose() => template.Dispose();

    /// <summary>Renders the name and what an instance costs.</summary>
    /// <returns>The prefab in text.</returns>
    public override string ToString() => $"{Name}: {EntityCount} entities in {ArchetypeCount} archetypes";

    static void Walk(World world, Entity entity, int parent, List<Entity> sources, List<int> parents) {
        var index = sources.Count;
        sources.Add(entity);
        parents.Add(parent);

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Walk(world, child, index, sources, parents);
        }
    }

    static ComponentSignature Without(ComponentSignature signature) {
        var kept = new List<ComponentTypeId>(signature.Count);

        foreach (var id in signature.Ids) {
            if (id != ComponentType<Parent>.Id && id != ComponentType<Child>.Id && id != ComponentType<Sibling>.Id) {
                kept.Add(id);
            }
        }

        return ComponentSignature.Of(kept.ToArray());
    }
}
