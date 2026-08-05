// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Worlds;

/// <summary>Turns a whole world into <see cref="WorldContent" /> and back.</summary>
/// <remarks>
///     <para>
///         <b>The item [14](../../../docs/plan/14-roadmap.md) parks behind the scene format, and the
///         dependency was real rather than administrative.</b> A world is entities and the bytes in
///         their chunks; what it is <i>not</i> is anything that knows what those bytes mean. Naming
///         a component and finding its serializer is <c>SceneComponentRegistry</c>'s job, and until
///         that existed a world serialiser could only have written raw chunk memory — which is
///         <c>WorldSnapshot</c>, is the right answer for play mode, and is not a format.
///     </para>
///     <para>
///         <b>It lives here and not in <c>Vixen.Ecs</c> because the pieces do.</b> The ECS references
///         no serializer by design; the binders and the transforms are the engine's. A seam in the ECS
///         filled from here would be a second way to say the same thing, and the layer boundary is
///         what would have to hold it up.
///     </para>
///     <para>
///         ⚠ <b>Three components are never written, and are rebuilt instead: <see cref="Parent" />,
///         <see cref="Child" /> and <see cref="Sibling" />.</b> All three hold <see cref="Entity" />
///         handles, which are slots in a running process. Storing them would mean either remapping
///         bytes nothing can identify as handles, or promising to hand back the same slot numbers —
///         and the second is a promise a fresh world cannot keep. The hierarchy travels as a table of
///         indices and the links are made by <see cref="Hierarchy.SetParent" />, which is the same
///         bargain <c>SceneContent</c> struck and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>A game component holding an <c>Entity</c> is not solved by that</b>, and cannot be
///         from here: nothing generic knows which of a component's fields are handles.
///         <c>World.CopyComponentsFrom</c> says the same and leaves the fix-up to its caller. What
///         this does is give a caller what it needs to do the fix-up — <see cref="Capture" /> fills an
///         optional list with the entity at each index and <see cref="Restore" /> returns the same
///         thing, so zipping the two is the translation table.
///     </para>
/// </remarks>
public static class WorldSerializer {
    /// <summary>What a column of a captured world is, whichever kind of component it holds.</summary>
    /// <remarks>
    ///     Not <see cref="ISceneComponentBinder" />, and the difference is the point: a scene may name
    ///     only components carrying both <c>[Component]</c> and <c>[DataContract]</c>, and a world
    ///     contains several of the engine's own that carry neither on purpose —
    ///     <see cref="LocalTransform" /> most of all, which is absent from the scene registry
    ///     precisely so a <c>.vxscene</c> cannot say two different things about where an entity is.
    ///     A world has no authored form and no such hazard, so it writes them itself.
    /// </remarks>
    interface IWorldColumn {
        string Name { get; }

        ComponentTypeId TypeId { get; }

        void Write(ref SerializationWriter writer, World world, Entity entity);

        void Read(ref SerializationReader reader, World world, Entity entity);
    }

    /// <summary>A column backed by a component's own generated serializer.</summary>
    sealed class ContractColumn(ISceneComponentBinder binder) : IWorldColumn {
        public string Name => binder.Name;

        public ComponentTypeId TypeId => binder.TypeId;

        public void Write(ref SerializationWriter writer, World world, Entity entity) =>
            binder.Write(ref writer, world, entity);

        public void Read(ref SerializationReader reader, World world, Entity entity) =>
            binder.Read(ref reader, world, entity);
    }

    /// <summary>The transform, as three vectors: ten floats and forty bytes.</summary>
    /// <remarks>
    ///     ⚠ <b>Longhand rather than through <see cref="SerializerRegistry" />, although
    ///     <see cref="Vector3" /> and <see cref="Matrix4x4" /> both have serializers there.</b> These
    ///     four columns are the format's own — a reader of this file should be able to see exactly
    ///     what a transform costs and in what order without following a generated partial into another
    ///     assembly. There are four and they will not grow: a fifth engine component a world has to
    ///     carry is one that should have earned a <c>[DataContract]</c> instead.
    ///     <para>
    ///         A class each rather than one generic closed over two delegates, because
    ///         <see cref="SerializationWriter" /> is a <c>ref struct</c> and cannot cross a delegate
    ///         boundary — which is the constraint that makes the longhand the only shape as well as
    ///         the clearest.
    ///     </para>
    /// </remarks>
    sealed class LocalTransformColumn : IWorldColumn {
        public string Name => "$local";

        public ComponentTypeId TypeId => ComponentType<LocalTransform>.Id;

        public void Write(ref SerializationWriter writer, World world, Entity entity) {
            ref readonly var value = ref world.Read<LocalTransform>(entity);

            Vector(ref writer, value.Position);
            writer.WriteSingle(value.Rotation.X);
            writer.WriteSingle(value.Rotation.Y);
            writer.WriteSingle(value.Rotation.Z);
            writer.WriteSingle(value.Rotation.W);
            Vector(ref writer, value.Scale);
        }

        public void Read(ref SerializationReader reader, World world, Entity entity) {
            var position = Vector(ref reader);
            var rotation = new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );

            var value = new LocalTransform { Position = position, Rotation = rotation, Scale = Vector(ref reader) };

            world.Set(entity, in value);
        }
    }

    /// <summary>The resolved matrix, as sixteen floats.</summary>
    /// <remarks>
    ///     ⚠ <b>Stored rather than recomputed, although <c>TransformSystem</c> would fill it on the
    ///     next frame.</b> A capture is meant to be readable without running a frame — a determinism
    ///     checkpoint compared field by field, a bug report opened in a tool — and "this column is
    ///     correct once you have stepped the world" is a footnote every reader of it would have to
    ///     know. Sixty-four bytes an entity is the price of the capture meaning what it says.
    /// </remarks>
    sealed class WorldTransformColumn : IWorldColumn {
        public string Name => "$world";

        public ComponentTypeId TypeId => ComponentType<WorldTransform>.Id;

        public void Write(ref SerializationWriter writer, World world, Entity entity) {
            foreach (var component in world.Read<WorldTransform>(entity).Value.AsSpan()) {
                writer.WriteSingle(component);
            }
        }

        public void Read(ref SerializationReader reader, World world, Entity entity) {
            Span<float> components = stackalloc float[Matrix4x4.ComponentCount];

            for (var index = 0; index < components.Length; index++) {
                components[index] = reader.ReadSingle();
            }

            var value = new WorldTransform {
                Value = new(
                    components[0], components[1], components[2], components[3],
                    components[4], components[5], components[6], components[7],
                    components[8], components[9], components[10], components[11],
                    components[12], components[13], components[14], components[15]
                )
            };

            world.Set(entity, in value);
        }
    }

    /// <summary>How far from a root, as one <see cref="short" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Written even though the link pass recomputes it.</b> A root with a depth and no
    ///     children is never touched by that pass, and dropping the column would take the component
    ///     off it — a difference in the archetype, which is a difference in what a query matches.
    /// </remarks>
    sealed class DepthColumn : IWorldColumn {
        public string Name => "$depth";

        public ComponentTypeId TypeId => ComponentType<HierarchyDepth>.Id;

        public void Write(ref SerializationWriter writer, World world, Entity entity) =>
            writer.WriteInt16(world.Read<HierarchyDepth>(entity).Value);

        public void Read(ref SerializationReader reader, World world, Entity entity) {
            var value = new HierarchyDepth { Value = reader.ReadInt16() };
            world.Set(entity, in value);
        }
    }

    /// <summary>Which scene an entity came from, as one <see cref="int" />.</summary>
    sealed class SceneTagColumn : IWorldColumn {
        public string Name => "$scene";

        public ComponentTypeId TypeId => ComponentType<SceneTag>.Id;

        public void Write(ref SerializationWriter writer, World world, Entity entity) =>
            writer.WriteInt32(world.Read<SceneTag>(entity).SceneId);

        public void Read(ref SerializationReader reader, World world, Entity entity) {
            var value = new SceneTag { SceneId = reader.ReadInt32() };
            world.Set(entity, in value);
        }
    }

    static void Vector(ref SerializationWriter writer, Vector3 value) {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    static Vector3 Vector(ref SerializationReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>The prefix a built-in column's name carries, and a contract alias may not.</summary>
    /// <remarks>
    ///     ⚠ <b>Checked on capture rather than assumed.</b> Nothing stops somebody writing
    ///     <c>[DataContract("$local")]</c>, and the failure without the check is a component quietly
    ///     restored as a transform — which is a corrupt world rather than an error.
    /// </remarks>
    public const string BuiltInPrefix = "$";

    /// <summary>The engine's own components a world carries and a scene may not name.</summary>
    static readonly Dictionary<ComponentTypeId, IWorldColumn> BuiltIns = Reserved();

    /// <summary>The three the hierarchy owns, which are rebuilt from the parent table.</summary>
    static readonly HashSet<ComponentTypeId> Structural = [
        ComponentType<Parent>.Id,
        ComponentType<Child>.Id,
        ComponentType<Sibling>.Id
    ];

    /// <summary>Writes every live entity in a world.</summary>
    /// <param name="world">The world to read.</param>
    /// <param name="order">
    ///     Filled with the entity at each index, if given. What a caller needs to translate handles it
    ///     is holding across the round trip.
    /// </param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The hierarchy does not reach every entity, or a component claims a reserved name.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The order is a depth-first walk from the roots, and it has to be.</b> A restore
    ///         links a child to a parent that must already exist, so a parent has to come first; and
    ///         sibling order is only recoverable if the walk preserves it, because the intrusive list
    ///         is what holds that order and the list is not written down. Sorting by entity id instead
    ///         would be canonical and would lose both.
    ///     </para>
    ///     <para>
    ///         An entity in no hierarchy at all — no <see cref="Parent" />, no children — is a root by
    ///         the same test a root passes, so a world of pure data entities captures as a flat list
    ///         and needs no special case.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order the <i>roots</i> go in is the one thing a world does not itself
    ///         define, and it is chosen here rather than discovered.</b> Children are a list and the
    ///         list is the order; roots are not a list at all — the scene finds them by asking every
    ///         entity whether it has a parent, which is the same reason <c>ReparentCommand</c> cannot
    ///         put a root back where it was. So this orders them by entity id: deterministic for one
    ///         world, and a fact about the order that world was <i>built</i> in rather than about
    ///         what it holds.
    ///     </para>
    ///     <para>
    ///         <b>Which is sound only because <see cref="Restore" /> numbers what it creates in this
    ///         content's order</b>, so a restored world's ids agree with its indices and capturing it
    ///         again produces these bytes exactly. Capture ∘ restore is the identity on content, which
    ///         is the property <c>WorldSerializerTests</c> gates and the reason the restore does not
    ///         create a block at a time. Two worlds that reached one state by different routes agree
    ///         wherever their root order does, which for a single-rooted world — a level, a save — is
    ///         always.
    ///     </para>
    /// </remarks>
    public static WorldContent Capture(World world, IList<Entity>? order = null) {
        ArgumentNullException.ThrowIfNull(world);

        var live = Live(world);
        var ordered = Order(world, live);

        if (ordered.Count != live.Count) {
            throw new InvalidOperationException(
                $"The walk from the roots reached {ordered.Count} of {live.Count} entities, so the hierarchy "
                + "has a cycle in it or a link to an entity that is not alive. A world in that state cannot be "
                + "written down, because the order a restore would have to create them in does not exist."
            );
        }

        var index = new Dictionary<Entity, int>(ordered.Count);

        for (var position = 0; position < ordered.Count; position++) {
            index[ordered[position]] = position;
        }

        var content = new WorldContent { Count = ordered.Count, Parents = new int[ordered.Count] };
        var dropped = new SortedSet<string>(StringComparer.Ordinal);

        // Keyed by the block's column names joined, which is the archetype as this format sees it —
        // and which is deliberately coarser than the real one, since the structural components are
        // not in it and two entities differing only in whether they have a parent are one block.
        var blocks = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<IWorldColumn>>(StringComparer.Ordinal);

        for (var position = 0; position < ordered.Count; position++) {
            var entity = ordered[position];
            var parent = Hierarchy.ParentOf(world, entity);

            content.Parents[position] = parent.IsNull ? -1 : index[parent];

            var carried = new List<IWorldColumn>();

            foreach (var id in world.ArchetypeOf(entity).Signature.Ids) {
                if (Structural.Contains(id)) {
                    continue;
                }

                if (BuiltIns.TryGetValue(id, out var reserved)) {
                    carried.Add(reserved);
                    continue;
                }

                var type = ComponentRegistry.Get(id).Type;

                if (!SceneComponentRegistry.TryGet(type, out var binder)) {
                    dropped.Add(type.FullName ?? type.Name);
                    continue;
                }

                if (binder.Name.StartsWith(BuiltInPrefix, StringComparison.Ordinal)) {
                    throw new InvalidOperationException(
                        $"'{type}' is called '{binder.Name}', and a name beginning with '{BuiltInPrefix}' is "
                        + "reserved for the components a captured world writes itself. Rename its "
                        + "[DataContract] — the alternative is a component silently restored as a transform."
                    );
                }

                carried.Add(new ContractColumn(binder));
            }

            carried.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            var key = string.Join(' ', carried.Select(column => column.Name));

            if (!blocks.TryGetValue(key, out var entries)) {
                blocks[key] = entries = [];
                members[key] = carried;
            }

            entries.Add(position);
        }

        var built = new List<SceneBlock>(blocks.Count);

        foreach (var (key, entries) in blocks) {
            var columns = new List<SceneColumn>();

            foreach (var column in members[key]) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new SerializationWriter(buffer);

                foreach (var position in entries) {
                    column.Write(ref writer, world, ordered[position]);
                }

                writer.Flush();
                columns.Add(new() { Component = column.Name, Data = buffer.WrittenSpan.ToArray() });
            }

            built.Add(new() { Entities = [.. entries], Columns = [.. columns] });
        }

        content.Blocks = [.. built];
        content.Dropped = [.. dropped];

        if (order is not null) {
            order.Clear();

            foreach (var entity in ordered) {
                order.Add(entity);
            }
        }

        return content;
    }

    /// <summary>Makes the captured world again, in place of whatever the target holds.</summary>
    /// <param name="content">What <see cref="Capture" /> wrote.</param>
    /// <param name="world">The world to overwrite.</param>
    /// <returns>The entity at each of the content's indices, in its order.</returns>
    /// <exception cref="ArgumentException">The content's tables disagree with each other.</exception>
    /// <exception cref="SceneComponentException">It names a component this build does not have.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="World.Clear" /> first, and that is a promise rather than a
    ///         convenience.</b> <c>WorldSnapshot.Restore</c> makes the same one for the same reason:
    ///         restoring on top of what is there keeps every entity the target had acquired since,
    ///         and "restore" would then mean "merge" — which is a different operation, is not what any
    ///         caller of this wants, and has no answer for two entities claiming one index.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The hierarchy is linked back to front</b>, because <see cref="Hierarchy.SetParent" />
    ///         prepends: linking in order would leave every parent holding its children reversed.
    ///         <c>SceneContent.Instantiate</c> has the same loop and spells out why a level whose
    ///         siblings flip on every load is wrong in ways nothing looks wrong about.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<Entity> Restore(WorldContent content, World world) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(world);

        content.Validate();
        world.Clear();

        var entities = new Entity[content.Count];
        var columns = new IWorldColumn[content.Blocks.Length][];
        var archetypes = new Archetype[content.Blocks.Length];
        var blockOf = new int[content.Count];

        for (var block = 0; block < content.Blocks.Length; block++) {
            columns[block] = Columns(content.Blocks[block]);
            archetypes[block] = world.ArchetypeOf([.. columns[block].Select(column => column.TypeId)]);

            foreach (var index in content.Blocks[block].Entities) {
                blockOf[index] = block;
            }
        }

        // ⚠ **In index order, one at a time, and not `CreateMany` a block at a time — which is what a
        // compiled scene does and what the archetype-major layout is for.** `Clear` resets the id
        // counter, so a world restored in index order hands slot n to index n; capturing that world
        // again therefore orders its roots exactly as this content does, and the round trip is a
        // fixed point. Creating block by block would instead number the entities by block, so a
        // second capture would emit the roots in a different order and the same world would produce
        // two different files. The archetype is still resolved once per block, which is the part of
        // the bulk create that costs anything.
        for (var index = 0; index < content.Count; index++) {
            entities[index] = world.Create(archetypes[blockOf[index]]);
        }

        // ⚠ Emptied rather than left half-restored. This already cleared the world above, so there is
        // nothing to put back and "the restore did not happen" cannot mean "the world is as it was" —
        // but it can mean an empty world rather than a partial one, which is a state a caller can
        // tell apart from a successful load and a partial one is not.
        try {
            for (var block = 0; block < content.Blocks.Length; block++) {
                Fill(content.Blocks[block], columns[block], world, entities);
            }
        } catch {
            world.Clear();

            throw;
        }

        for (var index = content.Count - 1; index >= 0; index--) {
            if (content.Parents[index] >= 0) {
                Hierarchy.SetParent(world, entities[index], entities[content.Parents[index]]);
            }
        }

        return entities;
    }

    /// <summary>What each of a block's columns is.</summary>
    static IWorldColumn[] Columns(SceneBlock block) {
        var columns = new IWorldColumn[block.Columns.Length];

        for (var index = 0; index < block.Columns.Length; index++) {
            columns[index] = Column(block.Columns[index].Component);
        }

        return columns;
    }

    /// <summary>Reads one block's columns onto its already-created entities.</summary>
    /// <remarks>
    ///     A column is written in the block's own entity order, so it is read in that order too —
    ///     which is what makes the bytes a run rather than a table of offsets.
    /// </remarks>
    static void Fill(SceneBlock block, IWorldColumn[] columns, World world, Entity[] entities) {
        for (var column = 0; column < columns.Length; column++) {
            var data = block.Columns[column].Data;
            var reader = new SerializationReader(data);

            // ⚠ The same pair of checks SceneContent.Fill makes, and for the reason WorldContent's
            // own remarks give: this data came off a disk, and a column's length is the one thing
            // Validate cannot relate to a block because only a column knows how wide a value is.
            try {
                foreach (var index in block.Entities) {
                    columns[column].Read(ref reader, world, entities[index]);
                }
            } catch (SerializationException failure) {
                throw new ArgumentException(
                    $"This world's '{columns[column].Name}' column is {data.Length} bytes and its block has "
                    + $"{block.Entities.Length} entities, which ran out after {reader.BytesRead}. The data is "
                    + "truncated or was written by something that does not agree with this format.",
                    failure
                );
            }

            // Bytes left over means the column and the block disagree about how many entities there
            // are, and reading the first n of them restores a world that is quietly wrong.
            if (reader.Remaining != 0) {
                throw new ArgumentException(
                    $"This world's '{columns[column].Name}' column has {reader.Remaining} bytes left after its "
                    + $"block's {block.Entities.Length} entities were read."
                );
            }
        }
    }

    /// <summary>The column a stored name means.</summary>
    /// <remarks>
    ///     Built-ins first, so a contract that has since claimed a reserved name cannot shadow one —
    ///     the capture refuses to write such a component, and this refuses to read it as anything else.
    /// </remarks>
    static IWorldColumn Column(string name) {
        if (name.StartsWith(BuiltInPrefix, StringComparison.Ordinal)) {
            foreach (var reserved in BuiltIns.Values) {
                if (string.Equals(reserved.Name, name, StringComparison.Ordinal)) {
                    return reserved;
                }
            }

            throw new SceneComponentException(
                $"This build has no built-in world column called '{name}'. The capture was written by a newer "
                + "engine than the one reading it."
            );
        }

        return new ContractColumn(SceneComponentRegistry.Require(name));
    }

    /// <summary>Every live entity, in no particular order.</summary>
    static List<Entity> Live(World world) {
        List<Entity> entities = [];

        foreach (var archetype in world.Archetypes) {
            foreach (var chunk in archetype.Chunks) {
                foreach (var entity in chunk.Entities[..chunk.Count]) {
                    entities.Add(entity);
                }
            }
        }

        return entities;
    }

    /// <summary>The entities in the order a restore has to create them.</summary>
    /// <remarks>
    ///     Roots by ascending id and then depth-first through the child lists, so the order is a
    ///     function of the world's state rather than of the order its chunks happen to be walked in —
    ///     the same property <c>WorldDigest</c> needs, and what makes capturing twice produce the same
    ///     bytes.
    /// </remarks>
    static List<Entity> Order(World world, List<Entity> live) {
        var roots = live.Where(entity => Hierarchy.ParentOf(world, entity).IsNull).ToList();
        roots.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        List<Entity> ordered = new(live.Count);
        var visited = new HashSet<Entity>();

        foreach (var root in roots) {
            Walk(world, root, ordered, visited);
        }

        return ordered;
    }

    /// <summary>
    ///     ⚠ <b>Guarded against revisiting, so a corrupt hierarchy stops rather than recurses for
    ///     ever.</b> <see cref="Hierarchy.SetParent" /> refuses cycles, so nothing built through the
    ///     supported API can be in one — which is exactly why a world that <i>is</i> should be
    ///     reported by the count check rather than hang the process that noticed.
    /// </summary>
    static void Walk(World world, Entity entity, List<Entity> ordered, HashSet<Entity> visited) {
        if (!visited.Add(entity)) {
            return;
        }

        ordered.Add(entity);

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Walk(world, child, ordered, visited);
        }
    }

    static Dictionary<ComponentTypeId, IWorldColumn> Reserved() {
        IWorldColumn[] columns = [
            new LocalTransformColumn(),
            new WorldTransformColumn(),
            new DepthColumn(),
            new SceneTagColumn()
        ];

        return columns.ToDictionary(column => column.TypeId);
    }
}
