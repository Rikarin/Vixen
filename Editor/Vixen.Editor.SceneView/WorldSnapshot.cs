// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Editor.SceneView;

/// <summary>A copy of a world, kept so that leaving play mode can put the scene back.</summary>
/// <remarks>
///     <para>
///         <b>This is what makes in-process play mode possible</b>, and doc 11 states it as a
///         constraint on the ECS rather than an afterthought: a world has to be cheaply copyable, and
///         the archetype/chunk layout is what makes that true. A snapshot is a walk over the
///         archetypes copying rows, not a serialisation pass.
///     </para>
///     <para>
///         ⚠ <b>An <c>Entity</c> is a slot in a world, so copying one verbatim into another world
///         names the wrong thing.</b> <c>World.CopyComponentsFrom</c> says so and leaves the fix-up
///         to the caller, because nothing generic can know which fields are handles. What is fixed up
///         here is the hierarchy — <c>Parent</c>, <c>Child</c> and <c>Sibling</c> — because those are
///         the ones the engine itself declares. A game component holding an <c>Entity</c> comes back
///         from play mode pointing at whatever is in that slot, and the way out of that is
///         <see cref="Remap" />, which is handed the whole translation table.
///     </para>
///     <para>
///         <b>Restoring gives every entity a new handle, and the editor's selection holds old
///         ones.</b> That is why <see cref="Restore" /> returns the table rather than being a
///         <c>void</c>: a selection that was not translated would highlight whatever entity happened
///         to land in the slot, which is the sort of bug that looks like a rendering fault.
///     </para>
/// </remarks>
public sealed class WorldSnapshot : IDisposable {
    readonly World store;
    readonly Dictionary<Entity, Entity> captured;

    bool disposed;

    /// <summary>How many entities were captured.</summary>
    public int EntityCount => captured.Count;

    WorldSnapshot(World store, Dictionary<Entity, Entity> captured) {
        this.store = store;
        this.captured = captured;
    }

    /// <summary>Takes a copy of a world.</summary>
    /// <param name="source">The world to copy.</param>
    /// <returns>The snapshot.</returns>
    public static WorldSnapshot Capture(World source) {
        ArgumentNullException.ThrowIfNull(source);

        var store = new World(source.Name + " (snapshot)");
        Dictionary<Entity, Entity> map = [];

        Copy(source, store, map);
        Rewire(store, map);

        return new(store, map);
    }

    /// <summary>Puts the world back to what was captured.</summary>
    /// <param name="target">The world to overwrite. Usually the one that was captured.</param>
    /// <returns>What each originally-captured entity is now, so a selection can be translated.</returns>
    /// <exception cref="ObjectDisposedException">The snapshot has been disposed.</exception>
    /// <remarks>
    ///     ⚠ <b><see cref="World.Clear" /> first, and that is the point.</b> Play mode's hazard is
    ///     state that outlives it — an entity a script spawned, a component it added. Restoring on
    ///     top of what is there would keep every one of them; clearing first is what makes leaving
    ///     play mode mean what it says.
    /// </remarks>
    public IReadOnlyDictionary<Entity, Entity> Restore(World target) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();

        Dictionary<Entity, Entity> restored = [];
        Copy(store, target, restored);
        Rewire(target, restored);

        Dictionary<Entity, Entity> translation = new(captured.Count);

        foreach (var (original, stored) in captured) {
            if (restored.TryGetValue(stored, out var now)) {
                translation[original] = now;
            }
        }

        return translation;
    }

    /// <summary>Translates a list of entities through a table, dropping the ones that are gone.</summary>
    /// <param name="entities">The entities.</param>
    /// <param name="translation">What <see cref="Restore" /> returned.</param>
    /// <returns>The translated entities, in order.</returns>
    /// <remarks>
    ///     ⚠ <b>Dropping rather than keeping the untranslated one.</b> An entity with no translation
    ///     is one that did not exist when the snapshot was taken — something play mode spawned — and
    ///     keeping its handle would name whatever is in that slot after the restore.
    /// </remarks>
    public static IReadOnlyList<Entity> Remap(
        IEnumerable<Entity> entities,
        IReadOnlyDictionary<Entity, Entity> translation
    ) {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(translation);

        List<Entity> remapped = [];

        foreach (var entity in entities) {
            if (translation.TryGetValue(entity, out var now)) {
                remapped.Add(now);
            }
        }

        return remapped;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        store.Dispose();
    }

    /// <summary>Copies every live entity, archetype by archetype.</summary>
    /// <remarks>
    ///     ⚠ <b>The entities are collected before any are created.</b> Creating into the target while
    ///     walking the source would be fine — they are different worlds — but the walk is over the
    ///     source's chunk list, and the same routine is used for the restore where the two happen to
    ///     be different objects only by convention. Collecting first makes it true by construction.
    /// </remarks>
    static void Copy(World source, World target, Dictionary<Entity, Entity> map) {
        List<Entity> entities = [];

        foreach (var archetype in source.Archetypes) {
            foreach (var chunk in archetype.Chunks) {
                foreach (var entity in chunk.Entities[..chunk.Count]) {
                    entities.Add(entity);
                }
            }
        }

        foreach (var entity in entities) {
            var archetype = target.ArchetypeOf(source.ArchetypeOf(entity).Signature.Ids);
            var copy = target.Create(archetype);

            map[entity] = copy;
            target.CopyComponentsFrom(copy, source, entity);
        }
    }

    /// <summary>Points the copied hierarchy at the copies rather than at the originals.</summary>
    /// <remarks>
    ///     The links were copied verbatim, so each one still names an entity in the world that was
    ///     copied <i>from</i>. Reading them here and putting them through the same table is what turns
    ///     a hierarchy that points at another world into one that points at itself.
    /// </remarks>
    static void Rewire(World target, Dictionary<Entity, Entity> map) {
        foreach (var copy in map.Values) {
            if (target.Has<Parent>(copy)) {
                target.Get<Parent>(copy).Value = Translate(map, target.Read<Parent>(copy).Value);
            }

            if (target.Has<Child>(copy)) {
                target.Get<Child>(copy).First = Translate(map, target.Read<Child>(copy).First);
            }

            if (target.Has<Sibling>(copy)) {
                ref var sibling = ref target.Get<Sibling>(copy);

                sibling.Next = Translate(map, sibling.Next);
                sibling.Previous = Translate(map, sibling.Previous);
            }
        }
    }

    /// <summary>
    ///     What an entity became, or <see cref="Entity.Null" /> for one that was not copied.
    /// </summary>
    /// <remarks>
    ///     Null rather than the original: a hierarchy link to an entity outside the snapshot is a
    ///     link into another world, and following it would read a slot that means something else.
    ///     Nulling it leaves a root, which is wrong in a visible way rather than in a corrupt one.
    /// </remarks>
    static Entity Translate(Dictionary<Entity, Entity> map, Entity entity) =>
        entity.IsNull ? Entity.Null : map.TryGetValue(entity, out var copy) ? copy : Entity.Null;
}
