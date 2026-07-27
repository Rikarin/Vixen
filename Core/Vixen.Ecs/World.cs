// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core;

namespace Vixen.Ecs;

/// <summary>
///     Everything that exists: the entities, the archetypes their components are stored in, and the
///     version counter that lets a system skip what did not change.
/// </summary>
/// <remarks>
///     <para>
///         A world is not thread-safe and does not pretend to be. Structural change — creating,
///         destroying, adding, removing — happens on the main thread or through a
///         <c>CommandBuffer</c> played back at a sync point; reads and component writes parallelise
///         across chunks under the scheduler's read/write declarations. A lock here would make every
///         one of those cost something to buy safety in a case the design already rules out.
///     </para>
///     <para>
///         Worlds are numbered, and the number is in every entity handle, so passing an entity from
///         the editor's world to the play world is caught rather than silently addressing whatever
///         shares the slot.
///     </para>
/// </remarks>
public sealed class World : IDisposable {
    internal struct EntityInfo {
        public Archetype? Archetype;
        public Chunk? Chunk;
        public int Row;
        public int Version;
    }

    static readonly Lock WorldsGate = new();
    static World?[] worlds = new World?[4];

    readonly Dictionary<ComponentSignature, Archetype> archetypesBySignature = [];
    readonly List<Archetype> archetypes = [];
    readonly Dictionary<QueryDescription, Query> queries = new(ReferenceEqualityComparer.Instance);

    EntityInfo[] infos = new EntityInfo[64];
    int[] freeIds = new int[16];
    int freeCount;
    int nextId = 1;
    IManagedComponentStore?[] managedStores = new IManagedComponentStore?[8];
    Archetype?[] cachedArchetypes = new Archetype?[16];

    /// <summary>Which world this is. Present in every entity handle it hands out.</summary>
    public short Id { get; }

    /// <summary>An optional name, for diagnostics and for the editor's world list.</summary>
    public string Name { get; }

    /// <summary>How many entities are alive.</summary>
    public int EntityCount { get; private set; }

    /// <summary>
    ///     The version writes are stamped with. A system advances it at its sync point, and a query
    ///     with a change filter compares against the value it last saw.
    /// </summary>
    public uint Version { get; private set; } = 1;

    /// <summary>
    ///     Bumped whenever an archetype is created, so a query knows its matched set may be stale
    ///     without re-testing every archetype's mask.
    /// </summary>
    public int StructuralVersion { get; private set; }

    /// <summary>The archetypes that exist, in creation order.</summary>
    public IReadOnlyList<Archetype> Archetypes => archetypes;

    /// <summary>The archetype a bare entity with no components lives in.</summary>
    public Archetype EmptyArchetype { get; }

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Creates a world and gives it the lowest free id.</summary>
    /// <param name="name">A name for diagnostics.</param>
    public World(string name = "World") {
        Name = name;
        Id = Claim(this);
        EmptyArchetype = GetOrCreateArchetype(ComponentSignature.Empty);
    }

    /// <summary>The world with an id, if it is still alive.</summary>
    /// <param name="id">The world id, as carried by an entity handle.</param>
    /// <returns>The world, or <see langword="null" />.</returns>
    public static World? Find(short id) {
        lock (WorldsGate) {
            return (uint)id < (uint)worlds.Length ? worlds[id] : null;
        }
    }

    /// <summary>
    ///     Moves the version forward, so writes from here on are distinguishable from writes before.
    /// </summary>
    /// <returns>The new version.</returns>
    /// <remarks>
    ///     Called once per system-graph sync point rather than per write: the granularity that
    ///     matters to "did this change since I last looked" is the phase, and a per-write counter
    ///     would make two writes in the same frame look like different generations.
    /// </remarks>
    public uint AdvanceVersion() => ++Version;

    // ---------------------------------------------------------------- creation

    /// <summary>Creates an entity with no components.</summary>
    /// <returns>Its handle.</returns>
    public Entity Create() => Create(EmptyArchetype);

    /// <summary>Creates an entity with one component.</summary>
    /// <typeparam name="T0">The component type.</typeparam>
    /// <param name="component0">Its value.</param>
    /// <returns>Its handle.</returns>
    public Entity Create<T0>(in T0 component0) {
        var entity = Create(CachedArchetype(ArchetypeKey<T0>.Index, [ComponentType<T0>.Id]));
        Write(entity, component0);
        return entity;
    }

    /// <summary>Creates an entity with two components.</summary>
    /// <typeparam name="T0">The first component type.</typeparam>
    /// <typeparam name="T1">The second component type.</typeparam>
    /// <param name="component0">The first value.</param>
    /// <param name="component1">The second value.</param>
    /// <returns>Its handle.</returns>
    public Entity Create<T0, T1>(in T0 component0, in T1 component1) {
        var entity = Create(
            CachedArchetype(ArchetypeKey<T0, T1>.Index, [ComponentType<T0>.Id, ComponentType<T1>.Id])
        );

        Write(entity, component0);
        Write(entity, component1);
        return entity;
    }

    /// <summary>Creates an entity with three components.</summary>
    /// <typeparam name="T0">The first component type.</typeparam>
    /// <typeparam name="T1">The second component type.</typeparam>
    /// <typeparam name="T2">The third component type.</typeparam>
    /// <param name="component0">The first value.</param>
    /// <param name="component1">The second value.</param>
    /// <param name="component2">The third value.</param>
    /// <returns>Its handle.</returns>
    public Entity Create<T0, T1, T2>(in T0 component0, in T1 component1, in T2 component2) {
        var entity = Create(
            CachedArchetype(
                ArchetypeKey<T0, T1, T2>.Index,
                [ComponentType<T0>.Id, ComponentType<T1>.Id, ComponentType<T2>.Id]
            )
        );

        Write(entity, component0);
        Write(entity, component1);
        Write(entity, component2);
        return entity;
    }

    /// <summary>Creates an entity with four components.</summary>
    /// <typeparam name="T0">The first component type.</typeparam>
    /// <typeparam name="T1">The second component type.</typeparam>
    /// <typeparam name="T2">The third component type.</typeparam>
    /// <typeparam name="T3">The fourth component type.</typeparam>
    /// <param name="component0">The first value.</param>
    /// <param name="component1">The second value.</param>
    /// <param name="component2">The third value.</param>
    /// <param name="component3">The fourth value.</param>
    /// <returns>Its handle.</returns>
    public Entity Create<T0, T1, T2, T3>(
        in T0 component0,
        in T1 component1,
        in T2 component2,
        in T3 component3
    ) {
        var entity = Create(
            CachedArchetype(
                ArchetypeKey<T0, T1, T2, T3>.Index,
                [ComponentType<T0>.Id, ComponentType<T1>.Id, ComponentType<T2>.Id, ComponentType<T3>.Id]
            )
        );

        Write(entity, component0);
        Write(entity, component1);
        Write(entity, component2);
        Write(entity, component3);
        return entity;
    }

    /// <summary>Creates an entity directly in an archetype, which is what bulk instantiation wants.</summary>
    /// <param name="archetype">Where to put it. Its components are zeroed.</param>
    /// <returns>Its handle.</returns>
    public Entity Create(Archetype archetype) {
        ArgumentNullException.ThrowIfNull(archetype);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (!ReferenceEquals(archetype.World, this)) {
            throw new ArgumentException(
                $"Archetype {archetype.Signature} belongs to world {archetype.World.Id} "
                + $"('{archetype.World.Name}'), not to this one. Archetypes hold chunks, and chunks "
                + "hold entities of exactly one world.",
                nameof(archetype)
            );
        }

        int id;

        if (freeCount > 0) {
            id = freeIds[--freeCount];
        } else {
            id = nextId++;
            EnsureInfoCapacity(id);
        }

        ref var info = ref infos[id];
        var entity = new Entity(id, info.Version, Id);
        var (chunk, row) = archetype.Allocate(entity);

        info.Archetype = archetype;
        info.Chunk = chunk;
        info.Row = row;
        EntityCount++;

        MarkAllColumnsWritten(chunk);
        return entity;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>The query for a description, with its matched archetypes remembered.</summary>
    /// <param name="description">What to ask for.</param>
    /// <returns>The query, which is the same object every time for the same description.</returns>
    /// <remarks>
    ///     Cached by reference on the description, so building one description at start-up and
    ///     asking for it every frame costs a dictionary lookup and nothing else. Building a fresh
    ///     description every frame works and re-tests every archetype's mask every frame.
    /// </remarks>
    public Query Query(QueryDescription description) {
        ArgumentNullException.ThrowIfNull(description);

        if (!queries.TryGetValue(description, out var query)) {
            query = new(this, description);
            queries[description] = query;
        }

        return query;
    }

    /// <summary>The chunks matching a description.</summary>
    /// <param name="description">What to ask for.</param>
    /// <param name="since">Only chunks written after this version, when the description filters on change.</param>
    /// <returns>Something to <c>foreach</c> over.</returns>
    public ChunkSequence Chunks(QueryDescription description, uint since = 0) => Query(description).Chunks(since);

    /// <summary>Creates a run of entities in one archetype.</summary>
    /// <param name="archetype">Where to put them. Their components are zeroed.</param>
    /// <param name="created">Filled with the new entities. Its length says how many to make.</param>
    /// <remarks>
    ///     What a prefab's instantiate plan and a scene load are written in terms of: a two-hundred
    ///     entity prefab is a handful of these rather than two hundred separate archetype lookups.
    ///     The rows are allocated through the same path a single <see cref="Create(Archetype)" />
    ///     uses, so nothing about chunk packing or versioning is special-cased.
    /// </remarks>
    public void CreateMany(Archetype archetype, Span<Entity> created) {
        for (var index = 0; index < created.Length; index++) {
            created[index] = Create(archetype);
        }
    }

    /// <summary>
    ///     Copies every component the target's archetype has from another entity, which may be in
    ///     another world.
    /// </summary>
    /// <param name="target">Where to copy to. Its archetype decides what is copied.</param>
    /// <param name="source">The world the source entity lives in. May be this one.</param>
    /// <param name="sourceEntity">What to copy from.</param>
    /// <remarks>
    ///     <para>
    ///         Components the source has and the target's archetype does not are skipped, and the
    ///         other way round leaves the target's zeroed. That makes this a projection rather than a
    ///         clone, which is what a prefab variant and a partial scene load both need.
    ///     </para>
    ///     <para>
    ///         <b>Entity-valued components are copied verbatim, not remapped.</b> A handle copied
    ///         into another world names a slot in the world it came from. Nothing here can know which
    ///         fields are handles, so the caller fixes up what it knows about — which for the
    ///         hierarchy means rebuilding it rather than translating it.
    ///     </para>
    /// </remarks>
    public void CopyComponentsFrom(Entity target, World source, Entity sourceEntity) {
        ArgumentNullException.ThrowIfNull(source);

        ref var targetInfo = ref Live(target);
        ref var sourceInfo = ref source.Live(sourceEntity);
        var targetArchetype = targetInfo.Archetype!;
        var sourceArchetype = sourceInfo.Archetype!;

        for (var column = 0; column < sourceArchetype.ColumnCount; column++) {
            var id = sourceArchetype.ColumnIds[column];
            var targetColumn = targetArchetype.ColumnOf(id);

            if (targetColumn < 0) {
                continue;
            }

            if (!ComponentRegistry.Get(id).IsManaged) {
                sourceInfo.Chunk!.RawRow(column, sourceInfo.Row)
                    .CopyTo(targetInfo.Chunk!.RawRow(targetColumn, targetInfo.Row));

                continue;
            }

            // A managed component's chunk cell is a handle into the store of *its* world, so the
            // value is boxed out of one and into a slot taken in the other. The source's store is
            // what tells the target's world which typed store to make, since neither has the type.
            if (source.StoreFor(id) is not { } sourceStore) {
                continue;
            }

            var boxed = sourceStore.Box(sourceInfo.Chunk!.At<int>(column, sourceInfo.Row));
            var targetStore = EnsureStoreLike(id, sourceStore);
            ref var handle = ref targetInfo.Chunk!.At<int>(targetColumn, targetInfo.Row);

            if (handle == 0) {
                handle = targetStore.TakeSlot();
            }

            targetStore.Unbox(handle, boxed);
            targetInfo.Chunk.MarkWritten(targetColumn, Version);
        }
    }

    IManagedComponentStore? StoreFor(ComponentTypeId id) =>
        id.Value < managedStores.Length ? managedStores[id.Value] : null;

    IManagedComponentStore EnsureStoreLike(ComponentTypeId id, IManagedComponentStore like) {
        if (id.Value >= managedStores.Length) {
            Array.Resize(ref managedStores, Math.Max(id.Value + 1, managedStores.Length * 2));
        }

        return managedStores[id.Value] ??= like.CreateSibling();
    }

    /// <summary>
    ///     The archetype for a combination of type parameters, remembered so the second
    ///     <c>Create&lt;Position, Velocity&gt;</c> costs an array index.
    /// </summary>
    /// <remarks>
    ///     Without this every create builds a <see cref="ComponentSignature" /> — allocate, sort,
    ///     de-duplicate, hash — for a set that was fixed when the call site was compiled. It measured
    ///     129 ns per entity at a hundred thousand, most of it that; the key is assigned once per
    ///     distinct combination of type parameters in the process, and the lookup after that is a
    ///     bounds check.
    /// </remarks>
    Archetype CachedArchetype(int key, ReadOnlySpan<ComponentTypeId> componentTypes) {
        if ((uint)key < (uint)cachedArchetypes.Length && cachedArchetypes[key] is { } cached) {
            return cached;
        }

        var archetype = ArchetypeOf(componentTypes);

        if (key >= cachedArchetypes.Length) {
            Array.Resize(ref cachedArchetypes, Math.Max(key + 1, cachedArchetypes.Length * 2));
        }

        cachedArchetypes[key] = archetype;
        return archetype;
    }

    static int nextArchetypeKey = -1;

    /// <summary>A dense index for one combination of type parameters, assigned once per process.</summary>
    static int NextArchetypeKey() => Interlocked.Increment(ref nextArchetypeKey);

    static class ArchetypeKey<T0> {
        public static readonly int Index = NextArchetypeKey();
    }

    static class ArchetypeKey<T0, T1> {
        public static readonly int Index = NextArchetypeKey();
    }

    static class ArchetypeKey<T0, T1, T2> {
        public static readonly int Index = NextArchetypeKey();
    }

    static class ArchetypeKey<T0, T1, T2, T3> {
        public static readonly int Index = NextArchetypeKey();
    }

    /// <summary>The archetype for a set of component types, creating it if this is the first ask.</summary>
    /// <param name="componentTypes">The component type ids, in any order.</param>
    /// <returns>The archetype.</returns>
    public Archetype ArchetypeOf(ReadOnlySpan<ComponentTypeId> componentTypes) =>
        GetOrCreateArchetype(ComponentSignature.Of(componentTypes));

    // ---------------------------------------------------------------- lifetime

    /// <summary>Whether a handle still names a live entity of this world.</summary>
    /// <param name="entity">The handle.</param>
    /// <returns>Whether it is live.</returns>
    public bool IsAlive(Entity entity) =>
        entity.WorldId == Id
        && (uint)entity.Id < (uint)nextId
        && infos[entity.Id].Archetype is not null
        && infos[entity.Id].Version == entity.Version;

    /// <summary>Destroys an entity and frees its slot for reuse.</summary>
    /// <param name="entity">The handle.</param>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    public void Destroy(Entity entity) {
        ref var info = ref Live(entity);
        var archetype = info.Archetype!;
        var chunk = info.Chunk!;
        var row = info.Row;

        ReleaseManagedComponents(archetype, chunk, row);
        var moved = archetype.Release(chunk, row);

        if (!moved.IsNull) {
            ref var movedInfo = ref infos[moved.Id];
            movedInfo.Chunk = chunk;
            movedInfo.Row = row;
        }

        info.Archetype = null;
        info.Chunk = null;
        info.Row = 0;

        // The version is what makes every outstanding handle to this entity stale from here on, so
        // it moves before the slot can be handed out again.
        info.Version++;
        EntityCount--;

        if (freeCount == freeIds.Length) {
            Array.Resize(ref freeIds, freeIds.Length * 2);
        }

        freeIds[freeCount++] = entity.Id;
    }

    /// <summary>The archetype an entity is in.</summary>
    /// <param name="entity">The handle.</param>
    /// <returns>Its archetype.</returns>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    public Archetype ArchetypeOf(Entity entity) => Live(entity).Archetype!;

    // ---------------------------------------------------------------- components

    /// <summary>Whether an entity has a component, tags included.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <returns>Whether it has one.</returns>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    public bool Has<T>(Entity entity) => Live(entity).Archetype!.Has(ComponentType<T>.Id);

    /// <summary>
    ///     A reference to a component, for writing. Marks the chunk's column as changed at the
    ///     current <see cref="Version" />.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <returns>A reference to the value.</returns>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    /// <exception cref="ComponentNotFoundException">The entity has no such component.</exception>
    /// <remarks>
    ///     Handing out a <c>ref</c> is treated as a write whether or not one happens, because there
    ///     is no way to find out afterwards. A reader that must not disturb the change filter uses
    ///     <see cref="Read{T}" />, and that choice being visible in the call is the point — it is
    ///     what makes "a system that writes nothing must not mark chunks dirty" a property the
    ///     compiler helps with rather than a convention.
    /// </remarks>
    public ref T Get<T>(Entity entity) {
        ref var info = ref Live(entity);
        var column = Column<T>(entity, in info);
        info.Chunk!.MarkWritten(column, Version);
        return ref Reference<T>(info.Chunk, column, info.Row);
    }

    /// <summary>A reference to a component, for reading. Does not mark anything as changed.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <returns>A read-only reference to the value.</returns>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    /// <exception cref="ComponentNotFoundException">The entity has no such component.</exception>
    public ref readonly T Read<T>(Entity entity) {
        ref var info = ref Live(entity);
        var column = Column<T>(entity, in info);
        return ref Reference<T>(info.Chunk!, column, info.Row);
    }

    /// <summary>Reads a component if the entity has one.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <param name="value">The value, or <see langword="default" />.</param>
    /// <returns>Whether the entity had one.</returns>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    public bool TryGet<T>(Entity entity, out T? value) {
        ref var info = ref Live(entity);
        var column = info.Archetype!.ColumnOf(ComponentType<T>.Id);

        if (column < 0) {
            value = default;

            // A tag has no column and is still present, so the answer is the mask's, not the
            // layout's — otherwise `TryGet` and `Has` disagree about the same entity.
            return info.Archetype.Has(ComponentType<T>.Id);
        }

        value = Reference<T>(info.Chunk!, column, info.Row);
        return true;
    }

    /// <summary>Overwrites a component the entity already has.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <param name="value">The new value.</param>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    /// <exception cref="ComponentNotFoundException">The entity has no such component.</exception>
    public void Set<T>(Entity entity, in T value) {
        ref var info = ref Live(entity);
        var column = Column<T>(entity, in info);
        info.Chunk!.MarkWritten(column, Version);
        Reference<T>(info.Chunk, column, info.Row) = value;
    }

    /// <summary>Adds a component, moving the entity to the archetype that has it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <param name="value">Its value. Ignored for a tag.</param>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    /// <exception cref="InvalidOperationException">The entity already has one.</exception>
    public void Add<T>(Entity entity, in T value) {
        ref var info = ref Live(entity);
        var id = ComponentType<T>.Id;
        var source = info.Archetype!;

        if (source.Has(id)) {
            throw new InvalidOperationException(
                $"Entity {entity} already has {typeof(T).Name}. Adding is a structural change; use "
                + $"{nameof(Set)} to overwrite the value."
            );
        }

        Move(entity, ref info, AddTarget(source, id));
        Write(entity, value);
    }

    /// <summary>Adds a component with its default value — the usual way to add a tag.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    /// <exception cref="InvalidOperationException">The entity already has one.</exception>
    public void Add<T>(Entity entity) => Add<T>(entity, default!);

    /// <summary>Removes a component, moving the entity to the archetype without it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="entity">The handle.</param>
    /// <exception cref="EntityNotFoundException">The handle is stale, or from another world.</exception>
    /// <exception cref="ComponentNotFoundException">The entity has no such component.</exception>
    public void Remove<T>(Entity entity) {
        ref var info = ref Live(entity);
        var id = ComponentType<T>.Id;
        var source = info.Archetype!;

        if (!source.Has(id)) {
            throw new ComponentNotFoundException(entity, typeof(T), source.Signature);
        }

        Move(entity, ref info, RemoveTarget(source, id));
    }

    /// <summary>Destroys every entity, keeping the archetypes and their chunk memory.</summary>
    public void Clear() {
        foreach (var archetype in archetypes) {
            foreach (var chunk in archetype.Chunks) {
                ReleaseManagedComponents(archetype, chunk);
            }
        }

        foreach (var archetype in archetypes) {
            archetype.Clear();
        }

        for (var id = 1; id < nextId; id++) {
            ref var info = ref infos[id];

            if (info.Archetype is not null) {
                info.Archetype = null;
                info.Chunk = null;
                info.Row = 0;
                info.Version++;
            }
        }

        foreach (var store in managedStores) {
            store?.Clear();
        }

        freeCount = 0;
        nextId = 1;
        EntityCount = 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;
        Clear();

        lock (WorldsGate) {
            if ((uint)Id < (uint)worlds.Length && ReferenceEquals(worlds[Id], this)) {
                worlds[Id] = null;
            }
        }
    }

    // ---------------------------------------------------------------- internals

    internal ref EntityInfo Live(Entity entity) {
        if (entity.WorldId != Id) {
            throw new EntityNotFoundException(
                entity,
                $"belongs to world {entity.WorldId} and this is world {Id} ('{Name}')"
            );
        }

        if ((uint)entity.Id >= (uint)nextId || entity.Id == 0) {
            throw new EntityNotFoundException(entity, "was never created in this world");
        }

        ref var info = ref infos[entity.Id];

        if (info.Archetype is null) {
            throw new EntityNotFoundException(entity, "has been destroyed");
        }

        if (info.Version != entity.Version) {
            throw new EntityNotFoundException(
                entity,
                $"is a stale handle; slot {entity.Id} is now on version {info.Version}"
            );
        }

        return ref info;
    }

    int Column<T>(Entity entity, ref readonly EntityInfo info) {
        var column = info.Archetype!.ColumnOf(ComponentType<T>.Id);

        if (column >= 0) {
            return column;
        }

        if (info.Archetype.Has(ComponentType<T>.Id)) {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is a tag: entity {entity} has it, and it stores no value to read "
                + $"or write. Ask {nameof(Has)}<{typeof(T).Name}> instead."
            );
        }

        throw new ComponentNotFoundException(entity, typeof(T), info.Archetype.Signature);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ref T Reference<T>(Chunk chunk, int column, int row) {
        // The test is on a static readonly bool of a closed generic, so the JIT resolves it at
        // compile time and one of the two arms disappears entirely.
        if (ComponentType<T>.Info.IsManaged) {
            return ref Managed<T>(chunk, column, row);
        }

        return ref chunk.At<T>(column, row);
    }

    ref T Managed<T>(Chunk chunk, int column, int row) {
        ref var handle = ref chunk.At<int>(column, row);
        var store = StoreFor<T>();

        if (handle == 0) {
            // A row is zeroed when it is allocated or moved into, so a managed component that has
            // never been written has no slot yet. Taking one lazily here is what makes `Add<T>()`
            // with no value, and a move that gains the component, both land on a real reference
            // rather than a null handle nobody can write through.
            handle = store.Allocate(default!);
        }

        return ref store.Get(handle);
    }

    void ReleaseManaged(ComponentTypeId id, int handle) {
        if (handle != 0 && id.Value < managedStores.Length) {
            managedStores[id.Value]?.Release(handle);
        }
    }

    /// <summary>Writes a component into an entity that already has the column, without version bookkeeping.</summary>
    void Write<T>(Entity entity, in T value) {
        if (ComponentType<T>.Info.IsTag) {
            return;
        }

        ref var info = ref infos[entity.Id];
        Reference<T>(info.Chunk!, info.Archetype!.ColumnOf(ComponentType<T>.Id), info.Row) = value;
    }

    ManagedComponentStore<T> StoreFor<T>() {
        var id = ComponentType<T>.Id.Value;

        if (id >= managedStores.Length) {
            Array.Resize(ref managedStores, Math.Max(id + 1, managedStores.Length * 2));
        }

        return (ManagedComponentStore<T>)(managedStores[id] ??= new ManagedComponentStore<T>());
    }

    Archetype AddTarget(Archetype source, ComponentTypeId id) {
        if (source.AddEdge(id) is { } cached) {
            return cached;
        }

        var target = GetOrCreateArchetype(source.Signature.With(id));
        source.LinkAdd(id, target);
        return target;
    }

    Archetype RemoveTarget(Archetype source, ComponentTypeId id) {
        if (source.RemoveEdge(id) is { } cached) {
            return cached;
        }

        var target = GetOrCreateArchetype(source.Signature.Without(id));
        source.LinkRemove(id, target);
        return target;
    }

    Archetype GetOrCreateArchetype(ComponentSignature signature) {
        if (archetypesBySignature.TryGetValue(signature, out var existing)) {
            return existing;
        }

        var archetype = new Archetype(this, signature);
        archetypesBySignature[signature] = archetype;
        archetypes.Add(archetype);
        StructuralVersion++;
        return archetype;
    }

    void Move(Entity entity, ref EntityInfo info, Archetype target) {
        var source = info.Archetype!;
        var sourceChunk = info.Chunk!;
        var sourceRow = info.Row;

        var (targetChunk, targetRow) = target.Allocate(entity);
        sourceChunk.CopySharedColumnsTo(sourceRow, targetChunk, targetRow);

        // Components the target does not have are gone, so their managed slots go back now — after
        // the copy, which does not touch them, and before the release, which overwrites the row.
        for (var column = 0; column < source.ColumnCount; column++) {
            var id = source.ColumnIds[column];

            if (target.ColumnOf(id) < 0 && ComponentRegistry.Get(id).IsManaged) {
                ReleaseManaged(id, sourceChunk.At<int>(column, sourceRow));
            }
        }

        var moved = source.Release(sourceChunk, sourceRow);

        if (!moved.IsNull) {
            ref var movedInfo = ref infos[moved.Id];
            movedInfo.Chunk = sourceChunk;
            movedInfo.Row = sourceRow;
        }

        info.Archetype = target;
        info.Chunk = targetChunk;
        info.Row = targetRow;
        MarkAllColumnsWritten(targetChunk);
    }

    void MarkAllColumnsWritten(Chunk chunk) {
        for (var column = 0; column < chunk.Archetype.ColumnCount; column++) {
            chunk.MarkWritten(column, Version);
        }
    }

    void ReleaseManagedComponents(Archetype archetype, Chunk chunk, int row) {
        for (var column = 0; column < archetype.ColumnCount; column++) {
            var id = archetype.ColumnIds[column];

            if (ComponentRegistry.Get(id).IsManaged) {
                ReleaseManaged(id, chunk.At<int>(column, row));
            }
        }
    }

    void ReleaseManagedComponents(Archetype archetype, Chunk chunk) {
        for (var row = 0; row < chunk.Count; row++) {
            ReleaseManagedComponents(archetype, chunk, row);
        }
    }

    void EnsureInfoCapacity(int id) {
        if (id < infos.Length) {
            return;
        }

        var grown = infos.Length;

        while (grown <= id) {
            grown *= 2;
        }

        Array.Resize(ref infos, grown);
    }

    static short Claim(World world) {
        lock (WorldsGate) {
            for (var id = 0; id < worlds.Length; id++) {
                if (worlds[id] is null) {
                    worlds[id] = world;
                    return (short)id;
                }
            }

            var previous = worlds.Length;

            if (previous >= short.MaxValue) {
                throw new InvalidOperationException(
                    $"{short.MaxValue} worlds are alive at once. A world id is part of every entity "
                    + "handle, so ids are not recycled while a world holds one — this is a leak, not "
                    + "a limit that was reached honestly."
                );
            }

            var grown = new World?[Math.Min(previous * 2, short.MaxValue)];
            Array.Copy(worlds, grown, previous);
            grown[previous] = world;
            worlds = grown;
            return (short)previous;
        }
    }

    /// <summary>Renders the name, entity count and archetype count.</summary>
    /// <returns>The world in text.</returns>
    public override string ToString() =>
        $"{Name}#{Id}: {EntityCount} entities in {archetypes.Count} archetypes";
}
