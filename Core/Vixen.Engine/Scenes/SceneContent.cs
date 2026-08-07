// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Scenes;

/// <summary>Every entity in one block's value for one component, back to back.</summary>
/// <remarks>
///     Column-major rather than one record per entity, because that is the order the load writes in:
///     a block is created as a run of entities in one archetype, and then each component is walked
///     down that run. A record per entity would interleave the reads and gain nothing — the bytes are
///     variable-width either way, so neither shape can be indexed into without walking.
/// </remarks>
[DataContract("SceneColumn")]
public sealed class SceneColumn {
    /// <summary>Which component this is — its <c>[DataContract]</c> alias.</summary>
    public string Component { get; set; } = string.Empty;

    /// <summary>Every entity's value, in the block's entity order.</summary>
    /// <remarks>Empty for a tag, which has no value and whose presence is the whole of what it says.</remarks>
    public byte[] Data { get; set; } = [];
}

/// <summary>The entities of a scene that share one archetype, and their component data.</summary>
/// <remarks>
///     <para>
///         What "archetype-ordered blobs for bulk world load" from
///         [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) means concretely: loading a
///         block is one <see cref="World.CreateMany" /> and then a walk down each column, so a
///         two-thousand entity level of six archetypes costs six archetype lookups rather than two
///         thousand.
///     </para>
///     <para>
///         <b>The archetype is the column set and is not stored as one.</b> A dense component id is
///         assigned in the order a process first touches a type, so it is meaningless outside the
///         process that assigned it; the names are what survive, and the archetype is rebuilt from
///         them at load.
///     </para>
/// </remarks>
[DataContract("SceneBlock")]
public sealed class SceneBlock {
    /// <summary>Which entities are in it, as indices into the scene's tables, ascending.</summary>
    public int[] Entities { get; set; } = [];

    /// <summary>What they carry, one column per component, in name order.</summary>
    public SceneColumn[] Columns { get; set; } = [];
}

/// <summary>A compiled entity hierarchy: the runtime form of an authored scene or prefab.</summary>
/// <remarks>
///     <para>
///         <b>This is the shape the authoring format is compiled *into*, and the two are shaped by
///         opposite forces.</b> A <c>.vxscene</c> nests its entities, spells its numbers out and is
///         built to be read by a person and merged by git. This is flat, positional and binary, and
///         is built to be turned into a world in one pass — see <c>SceneSerializer</c> for the other
///         half of the bargain.
///     </para>
///     <para>
///         <b>Entities are stored in the order they must be created: a parent before anything that
///         hangs from it.</b> <see cref="Parents" /> holds an index rather than an identity, so
///         rebuilding the hierarchy is an array read per entity and needs no map — which is what
///         makes a load linear in the number of entities rather than a walk of a tree.
///     </para>
///     <para>
///         <b>The transform is three columns of this rather than a component anybody can name.</b>
///         Every entity in a scene has one, so it costs nothing to make it universal and it removes
///         the case where a file says two different things about where an entity is — see
///         <see cref="SceneComponentRegistry" />.
///     </para>
/// </remarks>
[DataContract("SceneContent")]
public sealed class SceneContent {
    /// <summary>How many entities it makes.</summary>
    public int Count { get; set; }

    /// <summary>Each entity's parent, as an index into these tables, or <c>-1</c> for a root.</summary>
    public int[] Parents { get; set; } = [];

    /// <summary>Each entity's local position.</summary>
    public Vector3[] Positions { get; set; } = [];

    /// <summary>Each entity's local rotation.</summary>
    public Quaternion[] Rotations { get; set; } = [];

    /// <summary>Each entity's local scale.</summary>
    public Vector3[] Scales { get; set; } = [];

    /// <summary>What each entity is called, or empty when the build stripped the names.</summary>
    /// <remarks>
    ///     ⚠ <b>A table on the asset, and never a component.</b> <c>SceneDocument</c>'s reason for
    ///     the editor holding names in a map stands unchanged — thirty bytes per entity in every
    ///     chunk of a shipping build, to serve a panel that does not exist at run time. One string
    ///     per entity in the asset is paid once and is what makes a compiled scene something a person
    ///     can still open, debug and search.
    /// </remarks>
    public string[] Names { get; set; } = [];

    /// <summary>Each entity's authored identity, or empty when the build stripped them.</summary>
    /// <remarks>
    ///     The <c>EntityId</c> the authoring format wrote, carried through so that a reference into a
    ///     scene — a prefab override, a networked scene-placed object, a save game — still means the
    ///     same entity after a rebuild. Nothing in the load uses it; it is here because it is the only
    ///     place it can survive.
    /// </remarks>
    public Guid[] Ids { get; set; } = [];

    /// <summary>Its entities, grouped by what they carry.</summary>
    public SceneBlock[] Blocks { get; set; } = [];

    /// <summary>What each entity is called, or the empty string when the names were stripped.</summary>
    /// <param name="index">Which entity.</param>
    /// <returns>The name.</returns>
    public string NameOf(int index) =>
        (uint) index < (uint) Names.Length ? Names[index] : string.Empty;

    /// <summary>Creates every entity, with its components, its transform and its place in the tree.</summary>
    /// <param name="world">Where to put them.</param>
    /// <param name="created">
    ///     Filled with one entity per node, in the asset's order. Pass an empty span not to ask;
    ///     anything else must be exactly <see cref="Count" /> long.
    /// </param>
    /// <param name="scene">
    ///     The scene to tag every entity with, or <see cref="SceneHandle.None" /> for none — which is
    ///     what a prefab template wants.
    /// </param>
    /// <returns>The roots, in the order the asset holds them.</returns>
    /// <exception cref="ArgumentException"><paramref name="created" /> is the wrong length, or the asset is malformed.</exception>
    /// <exception cref="SceneComponentException">It names a component this build does not have.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The hierarchy is linked back to front.</b> <c>Hierarchy.SetParent</c> puts a new
    ///         child at the <i>head</i> of the intrusive list — O(1), which is why the list is
    ///         intrusive at all — so linking the entities in order would leave every parent holding
    ///         its children reversed. A level whose siblings flip on every load is not visibly wrong
    ///         and is wrong: draw order, the order a script walks its children in, and what an editor
    ///         shows all depend on it. <c>SceneSerializer.Restore</c> has the same loop for the same
    ///         reason.
    ///     </para>
    ///     <para>
    ///         <b>Linking is a structural change and so is not free.</b> Each non-root gains a
    ///         <see cref="Parent" /> and a <see cref="Sibling" /> after its bulk create, which moves
    ///         it to a neighbouring archetype. Putting those in the block's archetype up front would
    ///         save the move and cost the thing that makes the bulk create sound — the links hold
    ///         entity handles, which do not exist until the entities do.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<Entity> Instantiate(World world, Span<Entity> created, SceneHandle scene = default) {
        ArgumentNullException.ThrowIfNull(world);
        Validate();

        if (!created.IsEmpty && created.Length != Count) {
            throw new ArgumentException(
                $"This scene has {Count} entities and the span is {created.Length}. Pass an empty span not to "
                + "ask which entity each node became.",
                nameof(created)
            );
        }

        var entities = created.IsEmpty ? new Entity[Count] : created;

        // ⚠ Undone rather than left, and the reason is that the checks above cannot cover this.
        //
        // Validate relates the tables to each other and cannot relate a column's *bytes* to its
        // block — only a binder knows how wide one value is, and only by reading it. So the first
        // moment a truncated column can be detected is halfway down it, by which point CreateMany
        // has already run for this block and for every block before it. Without this, a scene that
        // fails to load leaves the entities it managed to make: a level that is half there, with
        // components on some of it, and a caller that saw an exception and reasonably believes
        // nothing happened.
        //
        // Cheap because it is the failure path — a successful load never enters the catch, and a
        // failed one is destroying entities the caller was never given.
        try {
            foreach (var block in Blocks) {
                Fill(world, block, entities, scene);
            }
        } catch {
            foreach (var block in Blocks) {
                foreach (var index in block.Entities) {
                    if (world.IsAlive(entities[index])) {
                        world.Destroy(entities[index]);
                    }
                }
            }

            throw;
        }

        for (var index = 0; index < Count; index++) {
            var local = new LocalTransform {
                Position = Positions[index],

                // A zero quaternion is the identity here and not a rotation, and a zero scale is
                // one, for the reason the authoring format gives: both are what `default` looks
                // like, and both produce an entity that is present, selectable and invisible.
                Rotation = Rotations[index] == default ? Quaternion.Identity : Rotations[index],
                Scale = Scales[index] == default ? Vector3.One : Scales[index]
            };

            world.Set(entities[index], local);

            // ⚠ Both, always, and the second is the one a level cannot be seen without.
            //
            // Every consumer downstream of the transforms queries WorldTransform, not LocalTransform:
            // MeshExtractionSystem, LightExtractionSystem, the camera director, the audio listener.
            // And TransformSystem only ever *writes into* a WorldTransform — its queries are
            // `WithAll<LocalTransform, WorldTransform>` — so it cannot supply the one that is
            // missing. An entity loaded with only a local transform is therefore not a mispositioned
            // entity; it is an entity nothing in the engine can find.
            //
            // What that looked like was a level that loaded, collided and reported every entity
            // present, and drew none of them: thirty-three mesh renderers, eight lights, and a frame
            // that saw the seven things the game had made in code. The scene was there the whole time
            // and had no place in the world.
            //
            // Seeded with the local matrix rather than the identity, so an entity is in the right
            // place on the frame it appears rather than on the one after. TransformSystem corrects a
            // child against its parent on the next update — a LocalTransform just written counts as
            // changed — and for a root this is already the answer.
            world.Set(entities[index], new WorldTransform { Value = local.ToMatrix() });
        }

        for (var index = Count - 1; index >= 0; index--) {
            if (Parents[index] >= 0) {
                Hierarchy.SetParent(world, entities[index], entities[Parents[index]]);
            }
        }

        var roots = new List<Entity>();

        for (var index = 0; index < Count; index++) {
            if (Parents[index] < 0) {
                roots.Add(entities[index]);
            }
        }

        return roots;
    }

    /// <summary>Compiles a subtree of a live world into content.</summary>
    /// <param name="world">The world to read.</param>
    /// <param name="roots">The subtree roots, in the order they should load.</param>
    /// <param name="names">What each entity is called, or <see langword="null" /> to strip the names.</param>
    /// <param name="ids">Each entity's authored identity, or <see langword="null" /> to strip them.</param>
    /// <returns>The content.</returns>
    /// <remarks>
    ///     <para>
    ///         The inverse of <see cref="Instantiate" />, and what a scene compiler that has already
    ///         built the world calls — an editor saving play-mode state, a test, and the compiler's
    ///         own round-trip check. A build that goes straight from the authored file to this shape
    ///         never makes the world at all, which is why this is not on the compiling path.
    ///     </para>
    ///     <para>
    ///         Only components the registry knows are captured, and anything else is dropped
    ///         silently. That is the honest reading of "a compiled scene names components by their
    ///         contract": a component with no serializer has no name to be written under, and the
    ///         alternative — refusing — would make capturing a world impossible for anybody using one
    ///         runtime-only component.
    ///     </para>
    /// </remarks>
    public static SceneContent Capture(
        World world,
        IReadOnlyList<Entity> roots,
        IReadOnlyDictionary<Entity, string>? names = null,
        IReadOnlyDictionary<Entity, Guid>? ids = null
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(roots);

        var order = new List<Entity>();
        var parents = new List<int>();

        foreach (var root in roots) {
            Walk(world, root, -1, order, parents);
        }

        var content = new SceneContent {
            Count = order.Count,
            Parents = [.. parents],
            Positions = new Vector3[order.Count],
            Rotations = new Quaternion[order.Count],
            Scales = new Vector3[order.Count],
            Names = names is null ? [] : new string[order.Count],
            Ids = ids is null ? [] : new Guid[order.Count]
        };

        // Keyed by the block's component names joined, which is the archetype as this format sees it.
        var blocks = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<ISceneComponentBinder>>(StringComparer.Ordinal);

        for (var index = 0; index < order.Count; index++) {
            var entity = order[index];

            if (world.TryGet<LocalTransform>(entity, out var local)) {
                content.Positions[index] = local.Position;
                content.Rotations[index] = local.Rotation;
                content.Scales[index] = local.Scale;
            } else {
                content.Rotations[index] = Quaternion.Identity;
                content.Scales[index] = Vector3.One;
            }

            if (names is not null) {
                content.Names[index] = names.GetValueOrDefault(entity, string.Empty);
            }

            if (ids is not null) {
                content.Ids[index] = ids.GetValueOrDefault(entity);
            }

            var carried = new List<ISceneComponentBinder>();

            foreach (var id in world.ArchetypeOf(entity).Signature.Ids) {
                if (SceneComponentRegistry.TryGet(ComponentRegistry.Get(id).Type, out var binder)) {
                    carried.Add(binder);
                }
            }

            carried.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            var key = string.Join('\0', carried.Select(binder => binder.Name));

            if (!blocks.TryGetValue(key, out var entries)) {
                blocks[key] = entries = [];
                members[key] = carried;
            }

            entries.Add(index);
        }

        var built = new List<SceneBlock>(blocks.Count);

        foreach (var (key, entries) in blocks) {
            var columns = new List<SceneColumn>();

            foreach (var binder in members[key]) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new SerializationWriter(buffer);

                foreach (var index in entries) {
                    binder.Write(ref writer, world, order[index]);
                }

                writer.Flush();
                columns.Add(new() { Component = binder.Name, Data = buffer.WrittenSpan.ToArray() });
            }

            built.Add(new() { Entities = [.. entries], Columns = [.. columns] });
        }

        content.Blocks = [.. built];
        return content;
    }

    /// <summary>Checks that the tables agree with each other before anything is created.</summary>
    /// <remarks>
    ///     Public because a compiler calls it on what it just built: a build is where a malformed
    ///     scene can still be somebody's problem, and the alternative is a chunk that ships and fails
    ///     to load. <see cref="Instantiate" /> calls it too — the same check, at the last moment it
    ///     can be made.
    /// </remarks>
    /// <exception cref="ArgumentException">They do not.</exception>
    /// <remarks>
    ///     ⚠ <b>Checked rather than trusted, because this data came off a disk.</b> A truncated
    ///     download, a half-written build or a chunk from a future format all arrive here as arrays
    ///     of the wrong length, and the failure without this is an <c>IndexOutOfRangeException</c>
    ///     from inside the load with nothing in it that names the asset. Every check is one
    ///     comparison against a count that is already in a register.
    /// </remarks>
    public void Validate() {
        if (Count < 0) {
            throw new ArgumentException($"A compiled scene cannot hold {Count} entities.");
        }

        Same(Parents.Length, nameof(Parents));
        Same(Positions.Length, nameof(Positions));
        Same(Rotations.Length, nameof(Rotations));
        Same(Scales.Length, nameof(Scales));

        if (Names.Length != 0) {
            Same(Names.Length, nameof(Names));
        }

        if (Ids.Length != 0) {
            Same(Ids.Length, nameof(Ids));
        }

        var seen = new bool[Count];

        foreach (var block in Blocks) {
            foreach (var entity in block.Entities) {
                if ((uint) entity >= (uint) Count) {
                    throw new ArgumentException(
                        $"A block names entity {entity} and the scene has {Count}. The asset is truncated or "
                        + "was written by something that does not agree with this format."
                    );
                }

                if (seen[entity]) {
                    throw new ArgumentException(
                        $"Entity {entity} is in two blocks. A block is an archetype, so an entity is in exactly "
                        + "one of them."
                    );
                }

                seen[entity] = true;
            }
        }

        for (var index = 0; index < Count; index++) {
            if (!seen[index]) {
                throw new ArgumentException(
                    $"Entity {index} is in no block, so nothing would create it. Every entity is in exactly one "
                    + "block, including one that carries nothing but a transform."
                );
            }

            var parent = Parents[index];

            if (parent >= index) {
                throw new ArgumentException(
                    $"Entity {index}'s parent is {parent}, which is not before it. A compiled scene is ordered so "
                    + "that a parent exists before anything that hangs from it."
                );
            }

            if (parent < -1) {
                throw new ArgumentException($"Entity {index}'s parent is {parent}. A root's is -1.");
            }
        }
    }

    void Same(int length, string what) {
        if (length != Count) {
            throw new ArgumentException(
                $"This scene has {Count} entities and {length} {what.ToLowerInvariant()}. The asset is truncated "
                + "or was written by something that does not agree with this format."
            );
        }
    }

    static void Fill(World world, SceneBlock block, Span<Entity> entities, SceneHandle scene) {
        var binders = new ISceneComponentBinder[block.Columns.Length];

        // ⚠ Both transforms, in the archetype rather than added afterwards.
        //
        // WorldTransform is what every consumer downstream queries — the mesh and light extractions,
        // the camera director, the audio listener — and TransformSystem only writes into one that is
        // already there. So an entity loaded without it is not misplaced, it is invisible, and a
        // whole level of them loads, collides and draws nothing.
        //
        // Here rather than in the loop below, because that loop is what this class exists to avoid:
        // adding a component per entity is an archetype move per entity, and the whole point of a
        // block is one CreateMany and a walk down each column.
        var ids = new List<ComponentTypeId>(block.Columns.Length + 3) {
            ComponentType<LocalTransform>.Id,
            ComponentType<WorldTransform>.Id
        };

        for (var index = 0; index < block.Columns.Length; index++) {
            binders[index] = SceneComponentRegistry.Require(block.Columns[index].Component);

            // A captured world writes its transforms as ordinary columns, so the two above may
            // already be here. The column is still read below — only the archetype is deduplicated.
            if (!ids.Contains(binders[index].TypeId)) {
                ids.Add(binders[index].TypeId);
            }
        }

        if (scene.IsValid) {
            ids.Add(ComponentType<SceneTag>.Id);
        }

        var batch = new Entity[block.Entities.Length];
        world.CreateMany(world.ArchetypeOf(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ids)), batch);

        for (var index = 0; index < batch.Length; index++) {
            entities[block.Entities[index]] = batch[index];

            if (scene.IsValid) {
                world.Set(batch[index], new SceneTag { SceneId = scene.Id });
            }
        }

        for (var column = 0; column < binders.Length; column++) {
            var data = block.Columns[column].Data;
            var reader = new SerializationReader(data);

            // ⚠ Wrapped, because the reader's refusal is not this format's. A column shorter than its
            // block throws SerializationException out of the middle of the walk, naming an offset in
            // a byte array and neither the component nor the block it came from — and this method is
            // reached from Instantiate, which documents ArgumentException for an asset that does not
            // hold together.
            try {
                foreach (var entity in batch) {
                    binders[column].Read(ref reader, world, entity);
                }
            } catch (SerializationException failure) {
                throw new ArgumentException(
                    $"This scene's '{binders[column].Name}' column is {data.Length} bytes and its block has "
                    + $"{batch.Length} entities, which ran out after {reader.BytesRead}. The asset is truncated or "
                    + "was written by something that does not agree with this format.",
                    failure
                );
            }

            // ⚠ And the other direction, which is the one that fails silently. A column with bytes
            // left over is a column whose entity count disagrees with its block's, and reading the
            // first n and dropping the rest loads a scene that is wrong rather than one that
            // refuses — every entity holding a value that belongs to a different entity, with
            // nothing anywhere reporting a problem.
            if (reader.Remaining != 0) {
                throw new ArgumentException(
                    $"This scene's '{binders[column].Name}' column has {reader.Remaining} bytes left after its "
                    + $"block's {batch.Length} entities were read. The column and the block disagree about how "
                    + "many entities there are."
                );
            }
        }
    }

    static void Walk(World world, Entity entity, int parent, List<Entity> order, List<int> parents) {
        var index = order.Count;
        order.Add(entity);
        parents.Add(parent);

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Walk(world, child, index, order, parents);
        }
    }
}
