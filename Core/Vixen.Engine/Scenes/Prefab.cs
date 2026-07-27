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
    public Entity Instantiate(World world, LocalTransform? at = null) {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(template.IsDisposed, this);

        var created = new Entity[nodes.Length];
        var perArchetype = new List<int>[archetypes.Length];

        for (var index = 0; index < archetypes.Length; index++) {
            perArchetype[index] = [];
        }

        for (var index = 0; index < nodes.Length; index++) {
            perArchetype[archetypeOf[index]].Add(index);
        }

        // One bulk create per archetype, not one per entity. A two-hundred entity prefab of four
        // archetypes is four of these.
        for (var group = 0; group < archetypes.Length; group++) {
            var members = perArchetype[group];
            var batch = new Entity[members.Count];
            world.CreateMany(world.ArchetypeOf(archetypes[group].Signature.Ids), batch);

            for (var index = 0; index < members.Count; index++) {
                created[members[index]] = batch[index];
                world.CopyComponentsFrom(batch[index], template, nodes[members[index]]);
            }
        }

        for (var index = 0; index < nodes.Length; index++) {
            if (parents[index] >= 0) {
                Hierarchy.SetParent(world, created[index], created[parents[index]]);
            }
        }

        if (at is { } local && world.Has<LocalTransform>(created[0])) {
            world.Set(created[0], local);
        }

        return created[0];
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
